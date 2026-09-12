using EasyBlockSupport.Data;
using EasyBlockSupport.Models;
using EasyBlockSupport.Models.DTOs;
using EasyBlockSupport.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EasyBlockSupport.Controllers
{
    public class FunctionsController : Controller
    {
        private readonly ILogger<FunctionsController> _logger;
        private readonly IFunctionService _functionService;              // ← interface
        private readonly IBlockchainService _blockchainService;          // ← interface
        private readonly ICompanyContextService _companyContextService;  // ← interface
        private readonly ApplicationDbContext _context;

        public FunctionsController(
            ILogger<FunctionsController> logger,
            IFunctionService functionService,
            IBlockchainService blockchainService,
            ICompanyContextService companyContextService,
            ApplicationDbContext context)
        {
            _logger = logger;
            _functionService = functionService;
            _blockchainService = blockchainService;
            _companyContextService = companyContextService;
            _context = context;
        }

        public IActionResult Index()
        {
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> Reverse(string searchTerm)
        {
            try
            {
                ViewBag.SearchTerm = searchTerm;

                // No search yet → render Reverse.cshtml with an empty DTO
                if (string.IsNullOrEmpty(searchTerm))
                {
                    return View("Reverse", new ContributionReverseDTO());
                }

                var contributions = await _functionService.SearchContributionsAsync(null, null, null, null);

                // ============================================================
                // STEP 1: find the ORIGINAL row (ignore reversal rows here)
                // ============================================================
                var contribution = contributions.FirstOrDefault(c =>
                    !string.IsNullOrEmpty(c.ReceiptNo) &&
                    !c.ReceiptNo.Contains("-REVERSAL") &&
                    !c.ReceiptNo.Contains("-REV") &&
                    ((c.ReceiptNo != null && c.ReceiptNo.Equals(searchTerm, StringComparison.OrdinalIgnoreCase)) ||
                     (c.TransactionNo != null && c.TransactionNo.Equals(searchTerm, StringComparison.OrdinalIgnoreCase))));

                // ============================================================
                // STEP 2: find any REVERSAL row that references the search term
                //         (either the original receipt or a transaction no)
                // ============================================================
                var existingReversal = contributions.FirstOrDefault(c =>
                    (c.ReceiptNo != null && c.ReceiptNo.Contains("-REVERSAL")
                        && c.ReceiptNo.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                    ||
                    (c.TransactionNo != null && c.TransactionNo.Contains("-REV")
                        && c.TransactionNo.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)));

                // ============================================================
                // STEP 3: if a reversal already exists, show the error FIRST
                // ============================================================
                if (existingReversal != null)
                {
                    ViewBag.ErrorMessage =
                        $"Transaction '{searchTerm}' has already been reversed. " +
                        $"Reversal Receipt: {existingReversal.ReceiptNo}";
                    return View("Reverse", new ContributionReverseDTO());
                }

                // ============================================================
                // STEP 4: original not found at all → generic not-found error
                // ============================================================
                if (contribution == null)
                {
                    ViewBag.ErrorMessage =
                        $"No transaction found with Receipt/Transaction Number: '{searchTerm}'";
                    return View("Reverse", new ContributionReverseDTO());
                }

                // ============================================================
                // STEP 5: the original itself is marked REVERSED → show error
                // ============================================================
                if (!string.IsNullOrEmpty(contribution.Status) &&
                    contribution.Status.Equals("REVERSED", StringComparison.OrdinalIgnoreCase))
                {
                    ViewBag.ErrorMessage = $"Transaction '{searchTerm}' has already been reversed.";
                    return View("Reverse", new ContributionReverseDTO());
                }

                // ============================================================
                // STEP 6: everything clean → show confirmation panel
                // ============================================================
                var reverseDto = new ContributionReverseDTO
                {
                    ContributionId = contribution.Id,
                    ReceiptNo = contribution.ReceiptNo,
                    MemberNo = contribution.MemberNo,
                    MemberName = contribution.MemberName,
                    Amount = contribution.Amount,
                    ShareTypeName = contribution.ShareTypeName,
                    TransactionDate = contribution.TransactionDate,
                    CreatedBy = contribution.CreatedBy,
                    BlockchainTxId = contribution.BlockchainTxId,
                    ReverseReason = string.Empty
                };

                return View("Reverse", reverseDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching contribution for reversal");
                ViewBag.ErrorMessage = $"Error: {ex.Message}";
                return View("Reverse", new ContributionReverseDTO());
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Reverse(ContributionReverseDTO reverseDto)
        {
            try
            {
                _logger.LogInformation("Reverse contribution POST for ID: {Id}", reverseDto.ContributionId);

                if (!ModelState.IsValid)
                {
                    // Re-render the SAME combined view; the search box is still there,
                    // and the confirmation panel re-displays with validation errors.
                    ViewBag.SearchTerm = reverseDto.ReceiptNo;
                    return View("Reverse", reverseDto);
                }

                var reversedBy = User.Identity?.Name ?? "SYSTEM";

                var result = await _functionService.ReverseContributionAsync(
                    reverseDto.ContributionId,
                    reverseDto.ReverseReason,
                    reversedBy);

                if (result.Success)
                {
                    TempData["SuccessMessage"] = result.Message;
                    // Go back to a clean search page (empty DTO)
                    return RedirectToAction("Reverse");
                }

                TempData["ErrorMessage"] = result.Message;
                ViewBag.SearchTerm = reverseDto.ReceiptNo;
                return View("Reverse", reverseDto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reversing contribution {Id}", reverseDto.ContributionId);
                TempData["ErrorMessage"] = $"Error reversing contribution: {ex.Message}";
                ViewBag.SearchTerm = reverseDto.ReceiptNo;
                return View("Reverse", reverseDto);
            }
        }

        [HttpGet]
        public IActionResult DeleteContributionIndex()
        {
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> GetContributionDetailsForDeletion(string searchTerm)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(searchTerm))
                    return Json(new { success = false, message = "Please provide a receipt or transaction number." });

                var contribution = await _functionService
                    .GetContributionByReceiptOrTransactionAsync(searchTerm, GetUserCompanyCode());

                if (contribution == null)
                {
                    return Json(new
                    {
                        success = false,
                        message = $"No contribution found with receipt/transaction number '{searchTerm}'."
                    });
                }

                // If the ROW ITSELF is a reversal, refuse — deleting a reversal
                // directly would leave the original out of sync.
                bool isReversalRow =
                    (contribution.ReceiptNo != null
                        && (contribution.ReceiptNo.Contains("-REVERSAL") || contribution.ReceiptNo.Contains("-REV")))
                    || (contribution.TransactionNo != null && contribution.TransactionNo.Contains("-REV"))
                    || string.Equals(contribution.Status, "REVERSED", StringComparison.OrdinalIgnoreCase);

                if (isReversalRow)
                {
                    return Json(new
                    {
                        success = false,
                        message = $"'{searchTerm}' is a reversal row. Search for the original receipt instead — " +
                                  $"deleting the original will also remove its reversal."
                    });
                }

                return Json(new
                {
                    success = true,
                    contribution = new
                    {
                        id = contribution.Id,
                        receiptNo = contribution.ReceiptNo,
                        transactionNo = contribution.TransactionNo,
                        memberNo = contribution.MemberNo,
                        memberName = contribution.MemberName,
                        shareTypeName = contribution.ShareTypeName,
                        amount = contribution.Amount,
                        transactionDate = contribution.TransactionDate.ToString("yyyy-MM-dd HH:mm"),
                        createdBy = contribution.CreatedBy,
                        blockchainTxId = contribution.BlockchainTxId
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error looking up contribution {SearchTerm}", searchTerm);
                return Json(new { success = false, message = "Error: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmDeleteContribution(int contributionId, string reason)
        {
            try
            {
                if (contributionId <= 0)
                {
                    TempData["ErrorMessage"] = "A valid contribution must be selected.";
                    return RedirectToAction("DeleteContributionIndex");
                }

                if (string.IsNullOrWhiteSpace(reason))
                {
                    TempData["ErrorMessage"] = "Please provide a reason for deleting the contribution.";
                    return RedirectToAction("DeleteContributionIndex");
                }

                var companyCode = GetUserCompanyCode();
                var userId = User.Identity?.Name ?? "SYSTEM";

                await _functionService.DeleteContributionAsync(contributionId, companyCode, userId, reason);

                TempData["SuccessMessage"] = $"Contribution has been permanently deleted.";
                return RedirectToAction("DeleteContributionIndex");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Unauthorized delete attempt for contribution {Id}", contributionId);
                TempData["ErrorMessage"] = "You are not authorized to delete that contribution.";
                return RedirectToAction("DeleteContributionIndex");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Delete refused for contribution {Id}: {Message}", contributionId, ex.Message);
                TempData["ErrorMessage"] = ex.Message;
                return RedirectToAction("DeleteContributionIndex");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error deleting contribution {Id}", contributionId);
                TempData["ErrorMessage"] = "An unexpected error occurred while deleting the contribution.";
                return RedirectToAction("DeleteContributionIndex");
            }
        }

        [HttpGet]
        public IActionResult DeleteLoanIndex()
        {
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> GetLoanDetailsForDeletion(string searchTerm)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(searchTerm))
                    return Json(new { success = false, message = "Please enter a loan number or member number." });

                var companyCode = GetUserCompanyCode();

                var loan = await _functionService.GetLoanForDeletionAsync(searchTerm, companyCode);

                if (loan == null)
                {
                    return Json(new
                    {
                        success = false,
                        message = $"No loan found for loan number or member number '{searchTerm}'."
                    });
                }

                // Shape the response exactly as your view's render() expects
                return Json(new
                {
                    success = true,
                    loan = new
                    {
                        loanNo = loan.LoanNo,
                        memberNo = loan.MemberNo,
                        memberName = loan.MemberName,
                        loanType = loan.LoanType,
                        principalAmount = loan.PrincipalAmount,
                        status = loan.Status,
                        applicationDate = loan.ApplicationDate,
                        interestRate = loan.InterestRate,
                        repayPeriod = loan.RepayPeriod,
                        repayMethod = loan.RepayMethod,
                        hasEndorsement = loan.HasEndorsement,
                        otherLoansForMember = loan.OtherLoansForMember,
                        guarantors = loan.Guarantors.Select(g => new
                        {
                            memberNo = g.MemberNo,
                            name = g.Name,
                            amount = g.Amount
                        }),
                        collateralGuarantees = loan.CollateralGuarantees.Select(c => new
                        {
                            colCode = c.ColCode,
                            docNo = c.DocNo,
                            marketValue = c.MarketValue,
                            guaranteeAmount = c.GuaranteeAmount
                        })
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error looking up loan for deletion {SearchTerm}", searchTerm);
                return Json(new { success = false, message = "Error: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmDeleteLoan(string loanNo, string reason)
        {
            try
            {
                _logger.LogInformation("ConfirmDeleteLoan POST called for loan {LoanNo}", loanNo);

                // -------- Input validation --------
                if (string.IsNullOrWhiteSpace(loanNo))
                {
                    TempData["ErrorMessage"] = "Loan number is required.";
                    return RedirectToAction("DeleteLoanIndex");
                }

                if (string.IsNullOrWhiteSpace(reason))
                {
                    TempData["ErrorMessage"] = "Please provide a reason for deleting the loan.";
                    return RedirectToAction("DeleteLoanIndex");
                }

                // -------- Company code is resolved SERVER-SIDE --------
                // Never accept companyCode from the form. This is what
                // prevents one SACCO from deleting another SACCO's loans.
                var companyCode = GetUserCompanyCode();
                var userId = User.Identity?.Name ?? "SYSTEM";

                // -------- Call the hardened service --------
                await _functionService.DeleteLoanAsync(loanNo, companyCode, userId, reason);

                TempData["SuccessMessage"] = $"Loan {loanNo} has been successfully deleted.";
                return RedirectToAction("DeleteLoanIndex");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Unauthorized delete attempt for loan {LoanNo}", loanNo);
                TempData["ErrorMessage"] = "You are not authorized to delete that loan.";
                return RedirectToAction("DeleteLoanIndex");
            }
            catch (InvalidOperationException ex)
            {
                // These are our business-rule refusals (already disbursed, has payments, cross-company, etc.)
                _logger.LogWarning(ex, "Delete refused for loan {LoanNo}: {Message}", loanNo, ex.Message);
                TempData["ErrorMessage"] = ex.Message;
                return RedirectToAction("DeleteLoanIndex");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error deleting loan {LoanNo}", loanNo);
                TempData["ErrorMessage"] = "An unexpected error occurred while deleting the loan.";
                return RedirectToAction("DeleteLoanIndex");
            }
        }
        private string GetUserCompanyCode()
        {
            // 1. Try claim (recommended)
            var code = User?.FindFirst("CompanyCode")?.Value;

            // 2. Fall back to session if your app stores it there
            if (string.IsNullOrEmpty(code))
                code = HttpContext?.Session?.GetString("CompanyCode");

            // 3. If your app has a company context service, use it
            // if (string.IsNullOrEmpty(code))
            //     code = _companyContextService.GetCurrentCompanyCode();

            if (string.IsNullOrEmpty(code))
                throw new InvalidOperationException(
                    "Company code could not be determined for the current user.");

            return code;
        }



        [HttpGet]
        public IActionResult JournalActions()
        {
            return View();   
        }
        [HttpGet]
        public async Task<IActionResult> GetJournalDetails(string searchTerm)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(searchTerm))
                    return Json(new { success = false, message = "Voucher / Transaction number is required." });

                var term = searchTerm.Trim();
                var termLower = term.ToLowerInvariant();

                // ============================================================
                // STEP 1 — Resolve the VOUCHER NO
                // ============================================================
                // The term could be either:
                //   a) a VNO (shared by all rows of the voucher), or
                //   b) a Transactionno (unique per row, so we must read the VNO
                //      of the matched row and then load every sibling row).
                //
                // Resolve VNO in this priority order:
                //   1. exact VNO match
                //   2. exact Transactionno match → use its VNO
                // ============================================================
                string? resolvedVno = null;

                // 1a. Try exact VNO
                resolvedVno = await _context.Journals
                    .Where(j => j.VNO != null && j.VNO.ToLower() == termLower)
                    .Select(j => j.VNO)
                    .FirstOrDefaultAsync();

                // 1b. Fall back to Transactionno → grab its VNO
                if (string.IsNullOrEmpty(resolvedVno))
                {
                    resolvedVno = await _context.Journals
                        .Where(j => j.Transactionno != null && j.Transactionno.ToLower() == termLower)
                        .OrderBy(j => j.JVID)
                        .Select(j => j.VNO)
                        .FirstOrDefaultAsync();
                }

                if (string.IsNullOrEmpty(resolvedVno))
                {
                    return Json(new
                    {
                        success = false,
                        message = $"Journal '{searchTerm}' was not found."
                    });
                }

                // ============================================================
                // STEP 2 — Load ALL rows for that VNO
                // ============================================================
                // This is what makes DR + CR both show even when the user
                // searched by one row's Transactionno.
                var journals = await _context.Journals
                    .Where(j => j.VNO == resolvedVno)
                    .OrderBy(j => j.JVID)
                    .ToListAsync();

                if (!journals.Any())
                {
                    return Json(new
                    {
                        success = false,
                        message = $"Journal '{searchTerm}' was not found."
                    });
                }

                var first = journals.First();

                var dr = journals.Where(j => j.TRANSTYPE == "DR").Sum(j => j.AMOUNT ?? 0);
                var cr = journals.Where(j => j.TRANSTYPE == "CR").Sum(j => j.AMOUNT ?? 0);

                return Json(new
                {
                    success = true,
                    // Echo which VNO was actually resolved so the UI can show it
                    matchedBy = resolvedVno.Equals(term, StringComparison.OrdinalIgnoreCase) ? "VNO" : "TransactionNo",
                    resolvedVoucherNo = resolvedVno,
                    journal = new
                    {
                        voucherNo = first.VNO,
                        // The searched transaction number (if any) is echoed back too,
                        // but the main identifier is always the VNO.
                        searchedTransactionNo = term,
                        voucherDate = first.TRANSDATE?.ToString("yyyy-MM-dd HH:mm"),
                        narration = first.NARATION,
                        memberNo = first.MEMBERNO,
                        loanNo = first.Loanno,
                        companyCode = first.CompanyCode,
                        posted = first.POSTED,
                        postedDate = first.POSTEDDATE?.ToString("yyyy-MM-dd HH:mm"),
                        createdBy = first.AUDITID,
                        totalDebit = dr,
                        totalCredit = cr,
                        entryCount = journals.Count,
                        lines = journals.Select(j => new
                        {
                            jvid = j.JVID,
                            transactionNo = j.Transactionno,       // per-row, for the table
                            accountNo = j.ACCNO,
                            accountName = j.NAME,
                            narration = j.NARATION,
                            debit = j.TRANSTYPE == "DR" ? (j.AMOUNT ?? 0) : 0,
                            credit = j.TRANSTYPE == "CR" ? (j.AMOUNT ?? 0) : 0
                        })
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error looking up journal {SearchTerm}", searchTerm);
                return Json(new { success = false, message = "Error: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmReverseJournal(string voucherNo, string reason)
        {
            try
            {
               if (string.IsNullOrWhiteSpace(voucherNo))
                {
                    TempData["ErrorMessage"] = "Voucher number is required.";
                    return RedirectToAction("JournalActions");
                }
                if (string.IsNullOrWhiteSpace(reason))
                {
                    TempData["ErrorMessage"] = "Please provide a reason for reversing the journal.";
                    return RedirectToAction("JournalActions");
                }

                var companyCode = GetUserCompanyCode();
                var userId = User.Identity?.Name ?? "SYSTEM";

                var result = await _functionService.ReverseJournalAsync(voucherNo, companyCode, userId, reason);

                TempData["SuccessMessage"] = result.Message;
                return RedirectToAction("JournalActions");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Reverse refused for journal {Voucher}", voucherNo);
                TempData["ErrorMessage"] = ex.Message;
                return RedirectToAction("JournalActions");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error reversing journal {Voucher}", voucherNo);
                TempData["ErrorMessage"] = "An unexpected error occurred while reversing the journal.";
                return RedirectToAction("JournalActions");
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmDeleteJournal(string voucherNo, string reason)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(voucherNo))
                {
                    TempData["ErrorMessage"] = "Voucher number is required.";
                    return RedirectToAction("JournalActions");
                }
                if (string.IsNullOrWhiteSpace(reason))
                {
                    TempData["ErrorMessage"] = "Please provide a reason for deleting the journal.";
                    return RedirectToAction("JournalActions");
                }

                var companyCode = GetUserCompanyCode();
                var userId = User.Identity?.Name ?? "SYSTEM";

                var result = await _functionService.DeleteJournalAsync(voucherNo, companyCode, userId, reason);

                TempData["SuccessMessage"] = result.Message;
                return RedirectToAction("JournalActions");
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Delete refused for journal {Voucher}", voucherNo);
                TempData["ErrorMessage"] = ex.Message;
                return RedirectToAction("JournalActions");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error deleting journal {Voucher}", voucherNo);
                TempData["ErrorMessage"] = "An unexpected error occurred while deleting the journal.";
                return RedirectToAction("JournalActions");
            }
        }
    }
}
