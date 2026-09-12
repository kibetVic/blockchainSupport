using EasyBlockSupport.Data;
using EasyBlockSupport.Models;
using EasyBlockSupport.Models.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.ComponentModel.DataAnnotations;

namespace EasyBlockSupport.Services
{
    public interface IFunctionService
    {
        Task<bool> DeleteLoanAsync(string loanNo, string companyCode, string deletedBy, string reason);
        Task<LoanDeletionInfoDTO?> GetLoanForDeletionAsync(string searchTerm, string companyCode);
        Task<ContributionDeleteResultDTO> ReverseContributionAsync(int contributionId, string deleteReason, string deletedBy);
        Task<bool> DeleteContributionAsync(int contributionId, string companyCode, string deletedBy, string reason);
        Task<List<ContributionResponseDTO>> SearchContributionsAsync( DateTime? fromDate, DateTime? toDate, string? memberNo = null, string? shareType = null);
        // Used by the Delete Contribution page's AJAX search
        Task<ContributionResponseDTO?> GetContributionByReceiptOrTransactionAsync( string searchTerm, string companyCode);
        Task<JournalActionResultDTO> ReverseJournalAsync(
        string voucherNo, string companyCode, string reversedBy, string reason);

        Task<JournalActionResultDTO> DeleteJournalAsync(
            string voucherNo, string companyCode, string deletedBy, string reason);
    }

    public class FunctionService : IFunctionService
    {
        private readonly ApplicationDbContext _context;
        private readonly AppDbContext _appDbContext;
        private readonly IBlockchainService _blockchainService;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly ICompanyContextService _companyContextService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<FunctionService> _logger;   

        public FunctionService(
            ApplicationDbContext context,
            IBlockchainService blockchainService,
            IHttpContextAccessor httpContextAccessor,
            ICompanyContextService companyContextService,
            IConfiguration configuration,
            ILogger<FunctionService> logger,
            AppDbContext appDbContext)
        {
            _context = context;
            _blockchainService = blockchainService;
            _httpContextAccessor = httpContextAccessor;
            _companyContextService = companyContextService;
            _configuration = configuration;
            _logger = logger;
            _appDbContext = appDbContext;
        }



        #region Loan Deletion - Permanent Delete
        public async Task<LoanDeletionInfoDTO?> GetLoanForDeletionAsync(string searchTerm, string companyCode)
        {
            if (string.IsNullOrWhiteSpace(searchTerm))
                return null;

            var term = searchTerm.Trim();
            var termLower = term.ToLowerInvariant();

            // ============================================================
            // STRICT LOOKUP — exact loan number, case-insensitive.
            // No ordinal stripping, no member fallback, no partial match.
            // ============================================================
            var loan = await _context.Loans
                .Where(l => l.LoanNo.ToLower() == termLower)
                .OrderByDescending(l => l.ApplicDate)
                .FirstOrDefaultAsync();

            if (loan == null)
                return null;

            // ============================================================
            // Enrich — use the loan's OWN company from here on
            // ============================================================
            var targetCompanyCode = loan.CompanyCode ?? string.Empty;

            var memberName = await _context.Members
                .Where(m => m.MemberNo == loan.MemberNo && m.CompanyCode == targetCompanyCode)
                .Select(m => (m.Surname + " " + m.OtherNames).Trim())
                .FirstOrDefaultAsync() ?? loan.MemberNo ?? "N/A";

            var loanTypeName = await _context.Loantypes
                .Where(lt => lt.LoanCode == loan.LoanCode && lt.CompanyCode == targetCompanyCode)
                .Select(lt => lt.LoanType1)
                .FirstOrDefaultAsync() ?? loan.LoanCode ?? "N/A";

            // Guarantors
            var guarantorRows = await _context.Loanguar
                .Where(g => g.LoanNo == loan.LoanNo && g.CompanyCode == targetCompanyCode)
                .Select(g => new { g.MemberNo, g.FullNames, g.Amount })
                .ToListAsync();

            var guarantors = new List<GuarantorLineDTO>();
            foreach (var g in guarantorRows)
            {
                var name = g.FullNames;
                if (string.IsNullOrEmpty(name))
                {
                    name = await _context.Members
                        .Where(m => m.MemberNo == g.MemberNo && m.CompanyCode == targetCompanyCode)
                        .Select(m => (m.Surname + " " + m.OtherNames).Trim())
                        .FirstOrDefaultAsync() ?? g.MemberNo;
                }

                guarantors.Add(new GuarantorLineDTO
                {
                    MemberNo = g.MemberNo ?? string.Empty,
                    Name = name ?? string.Empty,
                    Amount = g.Amount ?? 0
                });
            }

            // Collateral guarantees
            var collaterals = await _context.ColloanGuars
                .Where(cg => cg.LoanNo == loan.LoanNo
                          && cg.CompanyCode == targetCompanyCode
                          && cg.Balance > 0)
                .Select(cg => new CollateralLineDTO
                {
                    ColCode = cg.ColCode ?? string.Empty,
                    DocNo = cg.DocNo ?? string.Empty,
                    MarketValue = cg.Mktvalue,
                    GuaranteeAmount = cg.Balance
                })
                .ToListAsync();

            bool hasEndorsement = await _context.Endmain
                .AnyAsync(e => e.LoanNo == loan.LoanNo && e.CompanyCode == targetCompanyCode);

            return new LoanDeletionInfoDTO
            {
                LoanNo = loan.LoanNo ?? string.Empty,
                MemberNo = loan.MemberNo ?? string.Empty,
                MemberName = memberName,
                LoanType = loanTypeName,
                PrincipalAmount = loan.LoanAmt ?? 0,
                Status = ((Status)(loan.Status ?? 0)).ToString(),
                ApplicationDate = loan.ApplicDate.ToString("yyyy-MM-dd"),
                InterestRate = loan.Interest ?? 0,
                RepayPeriod = loan.RepayPeriod ?? 0,
                RepayMethod = loan.RepayMethod ?? "N/A",
                HasEndorsement = hasEndorsement,
                Guarantors = guarantors,
                CollateralGuarantees = collaterals,
                // Deliberately not populated. The view no longer shows it.
                OtherLoansForMember = new List<string>()
            };
        }

        public async Task<bool> DeleteLoanAsync(string loanNo, string companyCode, string deletedBy, string reason)
        {
            // -------- 0. Input validation --------
            if (string.IsNullOrWhiteSpace(loanNo))
                throw new ArgumentException("Loan number is required.", nameof(loanNo));
            if (string.IsNullOrWhiteSpace(deletedBy))
                throw new ArgumentException("DeletedBy (user) is required.", nameof(deletedBy));
            if (string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException("A reason is required to delete a loan.", nameof(reason));

            // ============================================================
            // MAIN DB TRANSACTION (ApplicationDbContext)
            // ============================================================
            using var transaction = await _context.Database.BeginTransactionAsync();
            IDbContextTransaction? b2cTransaction = null;

            try
            {
                _logger.LogInformation(
                    "DeleteLoan requested. LoanNo={LoanNo}, CallerCompany={Company}, By={User}, Reason={Reason}",
                    loanNo, companyCode, deletedBy, reason);

                // ============================================================
                // 1. LOAD THE LOAN — NO COMPANY FILTER (support tool)
                // ============================================================
                var loan = await _context.Loans
                    .FirstOrDefaultAsync(l => l.LoanNo == loanNo);

                if (loan == null)
                {
                    throw new InvalidOperationException(
                        $"Loan {loanNo} was not found.");
                }

                // Use the loan's OWN company for all downstream writes.
                var targetCompanyCode = loan.CompanyCode
                    ?? throw new InvalidOperationException(
                        $"Loan {loanNo} has no company code.");

                _logger.LogInformation(
                    "Loan found. Status={Status}, Amount={Amount}, Company={Company}",
                    loan.Status, loan.LoanAmt, targetCompanyCode);

                // ============================================================
                // 2. SHARE THE SAME DB TRANSACTION WITH AppDbContext (B2C)
                // ============================================================
                // If AppDbContext points at the same physical DB, join the
                // current transaction so B2C deletions roll back together
                // with the main loan deletion.
                try
                {
                    // EF Core has no async overload for joining an existing DbTransaction.
                    // Use the synchronous DatabaseFacade.UseTransaction(...) — it enlists
                    // _appDbContext in the ADO.NET transaction started by the main context.
                    _appDbContext.Database.UseTransaction(transaction.GetDbTransaction());
                    _logger.LogInformation("AppDbContext enlisted in the main DB transaction.");
                }
                catch (Exception txEx)
                {
                    _logger.LogWarning(txEx,
                        "Could not join AppDbContext to main transaction. " +
                        "B2C deletions will run in their own scope.");
                }

                // ============================================================
                // 3. SNAPSHOT DATA FOR AUDIT / BLOCKCHAIN
                // ============================================================
                var loanInfo = new
                {
                    loan.LoanNo,
                    loan.MemberNo,
                    loan.LoanCode,
                    loan.LoanAmt,
                    loan.Status,
                    loan.ApplicDate,
                    loan.CompanyCode,
                    DeletedBy = deletedBy,
                    Reason = reason,
                    DeletedAt = DateTime.Now
                };

                // ============================================================
                // 4. GATHER ALL CHILD RECORDS (for reporting + deletion)
                // ============================================================

                // -------- Main DbContext tables --------
                var memberGuarantors = await _context.Loanguar
                    .Where(g => g.LoanNo == loanNo && g.CompanyCode == targetCompanyCode)
                    .ToListAsync();

                var collateralGuarantees = await _context.ColloanGuars
                    .Where(cg => cg.LoanNo == loanNo && cg.CompanyCode == targetCompanyCode)
                    .ToListAsync();

                var appraisals = await _context.Appraisal
                    .Where(a => a.LoanNo == loanNo && a.CompanyCode == targetCompanyCode)
                    .ToListAsync();

                // Endmain holds BOTH the approval record AND the endorsement record
                var endorsements = await _context.Endmain
                    .Where(e => e.LoanNo == loanNo && e.CompanyCode == targetCompanyCode)
                    .ToListAsync();

                var cheques = await _context.Cheques
                    .Where(c => c.LoanNo == loanNo && c.CompanyCode == targetCompanyCode)
                    .ToListAsync();

                var loanBals = await _context.Loanbal
                    .Where(lb => lb.LoanNo == loanNo && lb.Companycode == targetCompanyCode)
                    .ToListAsync();

                var schedules = await _context.LoanSchedules
                    .Where(s => s.LoanNo == loanNo && s.CompanyCode == targetCompanyCode)
                    .ToListAsync();

                var repayments = await _context.Repay
                    .Where(r => r.LoanNo == loanNo && r.CompanyCode == targetCompanyCode)
                    .ToListAsync();

                // GL transactions: match by cheque voucher numbers, loan number in
                // description, OR transaction numbers from cheques/repayments.
                var chequeVoucherNos = cheques
                    .Where(c => !string.IsNullOrEmpty(c.Voucherno))
                    .Select(c => c.Voucherno)
                    .Distinct()
                    .ToList();

                var chequeTxnNos = cheques
                    .Where(c => !string.IsNullOrEmpty(c.TransactionNo))
                    .Select(c => c.TransactionNo)
                    .Distinct()
                    .ToList();

                var repayTxnNos = repayments
                    .Where(r => !string.IsNullOrEmpty(r.TransactionNo))
                    .Select(r => r.TransactionNo)
                    .Distinct()
                    .ToList();

                var glTransactions = await _context.Gltransactions
                    .Where(g => g.CompanyCode == targetCompanyCode
                             && (
                                  (g.DocumentNo != null && chequeVoucherNos.Contains(g.DocumentNo))
                               || (!string.IsNullOrEmpty(g.TransactionNo) && chequeTxnNos.Contains(g.TransactionNo))
                               || (!string.IsNullOrEmpty(g.TransactionNo) && repayTxnNos.Contains(g.TransactionNo))
                               || (g.TransDescript != null && g.TransDescript.Contains(loanNo))
                                ))
                    .ToListAsync();

                // -------- AppDbContext tables (B2C / M-Pesa) --------
                var apiTxRows = await _appDbContext.ApiTransactions
                    .Where(a => a.LoanNo == loanNo)
                    .ToListAsync();

                var txDetailRows = await _appDbContext.Transaction_detail
                    .Where(t => t.TransactionCode == "DISP-" + loanNo
                             || (t.MemberNo == loan.MemberNo
                                 && t.TransactionCode != null
                                 && t.TransactionCode.StartsWith("DISP-")))
                    .ToListAsync();

                // Transactions / Transactions2 are linked via the cheque's TransactionNo
                var t1Rows = new List<Transaction>();
                var t2Rows = new List<Transactions2>();
                if (chequeTxnNos.Any())
                {
                    t1Rows = await _appDbContext.Transactions
                        .Where(t => t.TransactionNo != null && chequeTxnNos.Contains(t.TransactionNo))
                        .ToListAsync();

                    t2Rows = await _appDbContext.Transactions2
                        .Where(t => t.TransactionNo != null && chequeTxnNos.Contains(t.TransactionNo))
                        .ToListAsync();
                }

                _logger.LogInformation(
                    "Cascade summary for loan {LoanNo}: Guarantors={G}, Collateral={C}, " +
                    "Appraisals={A}, Endorsements={E}, Cheques={Ch}, LoanBal={B}, " +
                    "Schedules={S}, Repayments={R}, GL={Gl}, " +
                    "ApiTx={Api}, TxDetail={Td}, Tx1={T1}, Tx2={T2}",
                    loanNo,
                    memberGuarantors.Count, collateralGuarantees.Count,
                    appraisals.Count, endorsements.Count, cheques.Count,
                    loanBals.Count, schedules.Count, repayments.Count,
                    glTransactions.Count,
                    apiTxRows.Count, txDetailRows.Count, t1Rows.Count, t2Rows.Count);

                // ============================================================
                // 5. BLOCKCHAIN RECORD (before deleting anything)
                // ============================================================
                var blockchainTx = new BlockchainTransaction
                {
                    TransactionId = Guid.NewGuid().ToString(),
                    TransactionType = "LOAN_PERMANENTLY_DELETED",
                    MemberNo = loan.MemberNo,
                    CompanyCode = targetCompanyCode,
                    Amount = loan.LoanAmt ?? 0,
                    Timestamp = DateTime.Now,
                    DataHash = await _blockchainService.GenerateTransactionHash(loanInfo),
                    PayloadJson = System.Text.Json.JsonSerializer.Serialize(loanInfo),
                    OffChainReferenceId = loanNo,
                    Status = "CONFIRMED",
                    CreatedAt = DateTime.Now
                };

                _context.BlockchainTransactions.Add(blockchainTx);
                await _context.SaveChangesAsync();

                // ============================================================
                // 6. DELETE AppDbContext (B2C) ROWS FIRST — no FK from main DB
                // ============================================================
                if (txDetailRows.Any())
                {
                    _appDbContext.Transaction_detail.RemoveRange(txDetailRows);
                    _logger.LogInformation("Removed {Count} Transaction_detail row(s)", txDetailRows.Count);
                }

                if (apiTxRows.Any())
                {
                    _appDbContext.ApiTransactions.RemoveRange(apiTxRows);
                    _logger.LogInformation("Removed {Count} ApiTransaction row(s)", apiTxRows.Count);
                }

                if (t1Rows.Any())
                {
                    _appDbContext.Transactions.RemoveRange(t1Rows);
                    _logger.LogInformation("Removed {Count} Transaction row(s)", t1Rows.Count);
                }

                if (t2Rows.Any())
                {
                    _appDbContext.Transactions2.RemoveRange(t2Rows);
                    _logger.LogInformation("Removed {Count} Transactions2 row(s)", t2Rows.Count);
                }

                await _appDbContext.SaveChangesAsync();

                // ============================================================
                // 7. DELETE MAIN DB CHILD ROWS (order matters for FK constraints)
                // ============================================================

                // 7a. GL transactions
                if (glTransactions.Any())
                {
                    _context.Gltransactions.RemoveRange(glTransactions);
                    _logger.LogInformation("Removed {Count} GL transaction(s)", glTransactions.Count);
                }

                // 7b. Repayments
                if (repayments.Any())
                {
                    _context.Repay.RemoveRange(repayments);
                    _logger.LogInformation("Removed {Count} repayment(s)", repayments.Count);
                }

                // 7c. Repayment schedules
                if (schedules.Any())
                {
                    _context.LoanSchedules.RemoveRange(schedules);
                    _logger.LogInformation("Removed {Count} schedule(s)", schedules.Count);
                }

                // 7d. Loan balance
                if (loanBals.Any())
                {
                    _context.Loanbal.RemoveRange(loanBals);
                    _logger.LogInformation("Removed {Count} loan balance record(s)", loanBals.Count);
                }

                // 7e. Cheques
                if (cheques.Any())
                {
                    _context.Cheques.RemoveRange(cheques);
                    _logger.LogInformation("Removed {Count} cheque record(s)", cheques.Count);
                }

                // 7f. Endorsements / approvals (Endmain holds both)
                if (endorsements.Any())
                {
                    _context.Endmain.RemoveRange(endorsements);
                    _logger.LogInformation("Removed {Count} endorsement/approval record(s)", endorsements.Count);
                }

                // 7g. Appraisals
                if (appraisals.Any())
                {
                    _context.Appraisal.RemoveRange(appraisals);
                    _logger.LogInformation("Removed {Count} appraisal record(s)", appraisals.Count);
                }

                // 7h. Collateral guarantees
                if (collateralGuarantees.Any())
                {
                    _context.ColloanGuars.RemoveRange(collateralGuarantees);
                    _logger.LogInformation("Removed {Count} collateral guarantee(s)", collateralGuarantees.Count);
                }

                // 7i. Member guarantors
                if (memberGuarantors.Any())
                {
                    _context.Loanguar.RemoveRange(memberGuarantors);
                    _logger.LogInformation("Removed {Count} member guarantor record(s)", memberGuarantors.Count);
                }

                // Flush child deletions first so FK constraints are satisfied
                await _context.SaveChangesAsync();

                // ============================================================
                // 8. DELETE THE LOAN ITSELF
                // ============================================================
                _logger.LogInformation("Deleting loan {LoanNo} from Loans table", loanNo);
                _context.Loans.Remove(loan);
                await _context.SaveChangesAsync();

                // ============================================================
                // 9. COMMIT BOTH CONTEXTS
                // ============================================================
                if (b2cTransaction != null)
                {
                    await b2cTransaction.CommitAsync();
                    await b2cTransaction.DisposeAsync();
                }

                await transaction.CommitAsync();

                _logger.LogInformation(
                    "Loan {LoanNo} PERMANENTLY DELETED. " +
                    "Guarantors={G}, Collateral={C}, Appraisals={A}, Endorsements={E}, " +
                    "Cheques={Ch}, LoanBal={B}, Schedules={S}, Repayments={R}, GL={Gl}, " +
                    "ApiTx={Api}, TxDetail={Td}, Tx1={T1}, Tx2={T2}, BlockchainTxId={Tx}",
                    loanNo,
                    memberGuarantors.Count, collateralGuarantees.Count,
                    appraisals.Count, endorsements.Count, cheques.Count,
                    loanBals.Count, schedules.Count, repayments.Count, glTransactions.Count,
                    apiTxRows.Count, txDetailRows.Count, t1Rows.Count, t2Rows.Count,
                    blockchainTx.TransactionId);

                return true;
            }
            catch (Exception ex)
            {
                // Roll back B2C first if it was joined, then the main transaction.
                try
                {
                    if (b2cTransaction != null)
                    {
                        await b2cTransaction.RollbackAsync();
                        await b2cTransaction.DisposeAsync();
                    }
                }
                catch (Exception rollbackEx)
                {
                    _logger.LogWarning(rollbackEx, "B2C rollback failed (main rollback will still run)");
                }

                await transaction.RollbackAsync();
                _logger.LogError(ex, "Error permanently deleting loan {LoanNo}", loanNo);
                throw;
            }
        }
        #endregion

        public async Task<List<ContributionResponseDTO>> SearchContributionsAsync(DateTime? fromDate, DateTime? toDate, string? memberNo = null, string? shareType = null)
        {
            var query = _context.Contribs.AsQueryable();

            // Apply filters
            if (fromDate.HasValue)
            {
                query = query.Where(c => c.ContrDate >= fromDate);
            }

            if (toDate.HasValue)
            {
                query = query.Where(c => c.ContrDate <= toDate);
            }

            if (!string.IsNullOrEmpty(memberNo))
            {
                query = query.Where(c => c.MemberNo != null && c.MemberNo.Contains(memberNo));
            }

            if (!string.IsNullOrEmpty(shareType))
            {
                query = query.Where(c => c.Sharescode == shareType);
            }

            // ✅ FIX: Use LEFT JOIN with GroupJoin (works with EF Core)
            var result = await (
                from c in query
                join m in _context.Members
                    on new { c.MemberNo, c.CompanyCode }
                    equals new { MemberNo = m.MemberNo, CompanyCode = m.CompanyCode } into memberJoin
                from m in memberJoin.DefaultIfEmpty()
                join s in _context.Sharetypes
                    on new { SharesCode = c.Sharescode.Trim(), CompanyCode = c.CompanyCode.Trim() }
                    equals new { SharesCode = s.SharesCode.Trim(), CompanyCode = s.CompanyCode.Trim() } into shareJoin
                from s in shareJoin.DefaultIfEmpty()
                orderby c.ContrDate descending
                select new ContributionResponseDTO
                {
                    Id = c.Id,
                    MemberNo = c.MemberNo ?? string.Empty,
                    MemberName = (m != null ? (m.Surname + " " + m.OtherNames).Trim() : c.MemberNo ?? "Unknown"),
                    TransactionDate = c.ContrDate ?? DateTime.MinValue,
                    SharesCode = c.Sharescode ?? string.Empty,
                    ShareTypeName = (s != null ? s.SharesType : c.Sharescode) ?? "Unknown",
                    Amount = c.Amount ?? 0,
                    ReceiptNo = c.ReceiptNo ?? string.Empty,
                    TransactionNo = c.TransactionNo ?? string.Empty,   // ← NEW
                    Status = c.Status ?? string.Empty,          // ← NEW
                    Remarks = c.Remarks ?? string.Empty,
                    BlockchainTxId = c.BlockchainTxId ?? string.Empty,
                    CreatedAt = c.AuditTime,
                    CreatedBy = c.AuditId ?? string.Empty,
                    CompanyCode = c.CompanyCode ?? string.Empty
                })
                .Take(200)
                .ToListAsync();

            return result;
        }

        public async Task<ContributionDeleteResultDTO> ReverseContributionAsync(int contributionId, string deleteReason, string deletedBy)
        {
            _logger.LogInformation("Starting contribution reversal for ID: {Id}", contributionId);

            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                // ============================================================
                // 1. LOAD CONTRIBUTION — NO COMPANY FILTER (support tool)
                // ============================================================
                var originalContribution = await _context.Contribs
                    .Include(c => c.MemberNoNavigation)
                    .FirstOrDefaultAsync(c => c.Id == contributionId);

                if (originalContribution == null)
                {
                    throw new ValidationException($"Contribution with ID {contributionId} not found");
                }

                // Use the contribution's OWN company code from here on.
                // That keeps GL, blockchain, wallet, share rows all in the
                // correct company — even if the caller is logged in under a
                // different one.
                var targetCompanyCode = originalContribution.CompanyCode;

                if (string.IsNullOrWhiteSpace(targetCompanyCode))
                {
                    throw new ValidationException(
                        $"Contribution {contributionId} has no company code and cannot be reversed.");
                }

                _logger.LogInformation(
                    "Contribution {Id} belongs to company {Company}. Caller may be logged in elsewhere.",
                    contributionId, targetCompanyCode);

                // ============================================================
                // 2. GUARD: already reversed?
                // ============================================================
                var existingReversal = await _context.Contribs
                    .FirstOrDefaultAsync(c => c.ReceiptNo == $"{originalContribution.ReceiptNo}-REVERSAL"
                                           && c.CompanyCode == targetCompanyCode);

                if (existingReversal != null)
                {
                    throw new ValidationException(
                        $"This contribution has already been reversed. Reversal Receipt: {existingReversal.ReceiptNo}");
                }

                var receiptNo = originalContribution.ReceiptNo ?? string.Empty;
                var memberNo = originalContribution.MemberNo;
                var originalAmount = originalContribution.Amount ?? 0;
                var sharesCode = originalContribution.Sharescode;
                var originalTxnNo = originalContribution.TransactionNo ?? string.Empty;

                _logger.LogInformation(
                    "Reversing contribution: Receipt {Receipt}, Member {Member}, Amount {Amount}, Company {Company}",
                    receiptNo, memberNo, originalAmount, targetCompanyCode);

                // ============================================================
                // STEP 1: REVERSAL Contrib (negative amount) — target company
                // ============================================================
                var reversalContribution = new Contrib
                {
                    MemberNo = memberNo,
                    ContrDate = DateTime.Now,
                    DepositedDate = DateTime.Now,
                    ReceiptDate = DateTime.Now,
                    Amount = -originalAmount,
                    CompanyCode = targetCompanyCode,               // ← contribution's company
                    ReceiptNo = $"{receiptNo}-REVERSAL",
                    Remarks = $"REVERSAL: {deleteReason}. Original: {originalContribution.Remarks}",
                    AuditId = deletedBy,
                    AuditTime = DateTime.Now,
                    AuditDateTime = DateTime.Now,
                    Sharescode = sharesCode,
                    TransactionNo = $"{originalContribution.TransactionNo}-REV",
                    Posted = "Y",
                    Locked = "Y",
                    StaffNo = originalContribution.StaffNo,
                    RefNo = originalContribution.RefNo,
                    ShareBal = 0,
                    TransBy = deletedBy,
                    ChequeNo = originalContribution.ChequeNo,
                    TransDate = DateTime.Now,
                    SharesAcc = originalContribution.SharesAcc,
                    ContraAcc = originalContribution.ContraAcc,
                    CashBookdate = DateTime.Now,
                    Dregard = 0,
                    Offs = 0,
                    UserName = deletedBy,
                    Run = 0,
                    Run2 = 0,
                    MrCleared = "N",
                    Offset = false,
                    Schemecode = targetCompanyCode,
                    Status = "REVERSED"
                };

                _context.Contribs.Add(reversalContribution);
                await _context.SaveChangesAsync();

                // ============================================================
                // STEP 2: REVERSAL ContribShare — target company
                // ============================================================
                var originalContribShare = await _context.ContribShares
                    .FirstOrDefaultAsync(cs => cs.TransactionNo == originalContribution.TransactionNo);

                var reversalContribShare = new ContribShare
                {
                    LocalId = reversalContribution.Id,
                    MemberNo = memberNo,
                    CompanyCode = targetCompanyCode,               // ← contribution's company
                    ReceiptNo = $"{receiptNo}-REVERSAL",
                    Sharescode = sharesCode,
                    Remarks = $"REVERSAL: {deleteReason}",
                    AuditId = deletedBy,
                    AuditTime = DateTime.Now,
                    AuditDateTime = DateTime.Now,
                    TransactionNo = reversalContribution.TransactionNo,
                    ContrDate = DateTime.Now,
                    DepositedDate = DateTime.Now,
                    ReceiptDate = DateTime.Now,
                    ShareCapitalAmount = originalContribShare?.ShareCapitalAmount > 0 ? -originalAmount : 0,
                    DepositsAmount = originalContribShare?.DepositsAmount > 0 ? -originalAmount : 0,
                    PassBookAmount = originalContribShare?.PassBookAmount > 0 ? -originalAmount : 0,
                    Donor = originalContribShare?.Donor > 0 ? -originalAmount : 0,
                    LoanAmount = originalContribShare?.LoanAmount > 0 ? -originalAmount : 0,
                    RegFeeAmount = originalContribShare?.RegFeeAmount > 0 ? -originalAmount : 0
                };

                _context.ContribShares.Add(reversalContribShare);
                await _context.SaveChangesAsync();

                // ============================================================
                // STEP 3: Reverse share balance — target company
                // ============================================================
                await ReverseShareBalanceAsync(memberNo, sharesCode, originalAmount, targetCompanyCode);

                // ============================================================
                // STEP 4: REVERSAL GL — target company
                // ============================================================
                var originalGL = await _context.Gltransactions
                    .FirstOrDefaultAsync(gl => gl.DocumentNo == receiptNo
                                            && gl.CompanyCode == targetCompanyCode);

                if (originalGL != null)
                {
                    var reversalGL = new Gltransaction
                    {
                        TransDate = DateTime.Now,
                        Amount = originalAmount,
                        DrAccNo = originalGL.CrAccNo,
                        CrAccNo = originalGL.DrAccNo,
                        Temp = "REVERSAL",
                        DocumentNo = $"{receiptNo}-REV",
                        Source = originalGL.Source,
                        CompanyCode = targetCompanyCode,           // ← contribution's company
                        TransDescript = $"REVERSAL: {originalGL.TransDescript} - Reason: {deleteReason}",
                        AuditTime = DateTime.Now,
                        AuditId = deletedBy,
                        AuditDateTime = DateTime.Now,
                        Cash = originalGL.Cash,
                        DocPosted = 1,
                        ChequeNo = originalGL.ChequeNo,
                        Dregard = false,
                        Recon = false,
                        TransactionNo = reversalContribution.TransactionNo,
                        Module = "CONTRIBUTION_REVERSAL",
                        ReconId = 0
                    };

                    _context.Gltransactions.Add(reversalGL);
                    await _context.SaveChangesAsync();
                }

                // ============================================================
                // STEP 5: Wallet balance — target company
                // ============================================================
                var memberRecord = await _context.Members
                    .FirstOrDefaultAsync(m => m.MemberNo == memberNo
                                           && m.CompanyCode == targetCompanyCode);

                if (memberRecord != null)
                {
                    var wallet = await _context.Wallets
                        .FirstOrDefaultAsync(w => w.MemberId == memberRecord.Id
                                               && w.CompanyCode == targetCompanyCode);

                    if (wallet != null)
                    {
                        if (originalContribShare?.ShareCapitalAmount > 0)
                        {
                            wallet.CapitalBalance -= originalAmount;
                            wallet.Balance -= originalAmount;
                        }
                        else if (originalContribShare?.DepositsAmount > 0)
                        {
                            wallet.DepositBalance -= originalAmount;
                            wallet.Balance -= originalAmount;
                        }
                        else
                        {
                            wallet.Balance -= originalAmount;
                        }

                        wallet.LastActivity = DateTime.UtcNow;
                        await _context.SaveChangesAsync();
                    }
                }

                // ============================================================
                // STEP 6: Blockchain reversal — target company
                // ============================================================
                string blockchainTxId = null;
                try
                {
                    var blockchainData = new
                    {
                        Action = "CONTRIBUTION_REVERSAL",
                        OriginalContributionId = contributionId,
                        OriginalReceiptNo = receiptNo,
                        ReversalReceiptNo = $"{receiptNo}-REVERSAL",
                        MemberNo = memberNo,
                        MemberName = originalContribution.MemberNoNavigation != null
                            ? $"{originalContribution.MemberNoNavigation.Surname} {originalContribution.MemberNoNavigation.OtherNames}"
                            : memberNo,
                        OriginalAmount = originalAmount,
                        ReversalAmount = -originalAmount,
                        SharesCode = sharesCode,
                        CompanyCode = targetCompanyCode,
                        DeleteReason = deleteReason,
                        ReversedBy = deletedBy,
                        ReversedAt = DateTime.Now,
                        OriginalTransactionDate = originalContribution.ContrDate,
                        OriginalCreatedBy = originalContribution.AuditId
                    };

                    var blockchainTx = await _blockchainService.CreateAndAddTransactionAsync(
                        "CONTRIBUTION_REVERSAL",
                        memberNo,
                        targetCompanyCode,                         // ← contribution's company
                        -originalAmount,
                        $"{receiptNo}-REV",
                        blockchainData
                    );

                    if (blockchainTx != null)
                    {
                        blockchainTxId = blockchainTx.TransactionId;
                        reversalContribution.BlockchainTxId = blockchainTxId;
                        reversalContribShare.BlockchainTxId = blockchainTxId;
                        await _context.SaveChangesAsync();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error recording blockchain reversal transaction");
                }

                // ============================================================
                // STEP 7: Mark original as reversed
                // ============================================================
                originalContribution.Status = "REVERSED";
                originalContribution.Remarks = $"[REVERSED] {originalContribution.Remarks} - Reversal: {deleteReason}";
                originalContribution.AuditTime = DateTime.Now;

                await _context.SaveChangesAsync();

                // ============================================================
                // STEP 8: AUDIT TRAIL  ← NEW
                // ============================================================
                var (ip, browser, host) = GetClientInfo();
                var correlationId = GetCorrelationId();
                var now = DateTime.Now;

                var memberName = originalContribution.MemberNoNavigation != null
                    ? $"{originalContribution.MemberNoNavigation.Surname} {originalContribution.MemberNoNavigation.OtherNames}".Trim()
                    : memberNo;

                var auditOldValue = System.Text.Json.JsonSerializer.Serialize(new
                {
                    contribution = new
                    {
                        originalContribution.Id,
                        originalContribution.ReceiptNo,
                        originalContribution.TransactionNo,
                        originalContribution.MemberNo,
                        originalContribution.Sharescode,
                        originalContribution.Amount,
                        originalContribution.ContrDate,
                        originalContribution.CompanyCode,
                        originalContribution.Remarks,
                        Status = "ACTIVE"   // before
                    },
                    shareBalanceAfter = null as decimal?,
                    walletBalanceAfter = null as decimal?
                });

                var auditNewValue = System.Text.Json.JsonSerializer.Serialize(new
                {
                    originalContribution.Id,
                    originalContribution.ReceiptNo,
                    originalContribution.TransactionNo,
                    originalContribution.MemberNo,
                    originalContribution.Sharescode,
                    originalContribution.Amount,
                    originalContribution.CompanyCode,
                    Status = "REVERSED",
                    reversalReceiptNo = reversalContribution.ReceiptNo,
                    reversalTxnNo = reversalContribution.TransactionNo,
                    reversedAt = now,
                    reversedBy = deletedBy
                });

                var auditExtraData = System.Text.Json.JsonSerializer.Serialize(new
                {
                    contributionId,
                    receiptNo,
                    originalTxnNo,
                    memberNo,
                    memberName,
                    sharesCode,
                    originalAmount,
                    reversalAmount = -originalAmount,
                    companyCode = targetCompanyCode,
                    reason = deleteReason,
                    reversedBy = deletedBy,
                    reversedAt = now,
                    reversalReceipt = reversalContribution.ReceiptNo,
                    reversalTxnNo = reversalContribution.TransactionNo,
                    blockchainTxId,
                    correlationId
                });

                _context.AuditTrails.Add(new AuditTrail
                {
                    CompanyCode = targetCompanyCode,
                    UserId = deletedBy,
                    UserName = deletedBy,
                    ActionType = "CONTRIBUTION_REVERSE",
                    ActionDescription = $"Reversed contribution {receiptNo} for member {memberNo}",
                    TableName = "Contribs, ContribShares, Gltransactions, Shares, Wallets, BlockchainTransactions",
                    RecordId = receiptNo,
                    OldValue = auditOldValue,
                    NewValue = auditNewValue,
                    IpAddress = ip,
                    BrowserAgent = browser,
                    HostName = host ?? GetHostNameFallback(),
                    CorrelationId = correlationId,
                    AuditTime = now,
                    Module = "CONTRIBUTION",
                    BlockchainTxId = blockchainTxId,
                    ExtraData = auditExtraData
                });

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                _logger.LogInformation(
                    "Contribution {Receipt} reversed. Reversal receipt: {ReversalReceipt}, Company: {Company}",
                    receiptNo, reversalContribution.ReceiptNo, targetCompanyCode);

                return new ContributionDeleteResultDTO
                {
                    Success = true,
                    Message = $"Contribution {receiptNo} has been successfully reversed. Reversal Receipt: {reversalContribution.ReceiptNo}",
                    ContributionId = contributionId,
                    ReceiptNo = receiptNo,
                    MemberNo = memberNo,
                    Amount = originalAmount,
                    DeletedAt = DateTime.Now,
                    DeletedBy = deletedBy,
                    BlockchainTxId = blockchainTxId ?? string.Empty
                };
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Error reversing contribution {Id}", contributionId);

                if (ex is ValidationException)
                    throw new Exception($"Validation error: {ex.Message}");

                throw new Exception($"Error reversing contribution: {ex.Message}");
            }
        }

        private async Task ReverseShareBalanceAsync(string memberNo, string sharesCode, decimal amount, string companyCode)
        {
            var existingShare = await _context.Shares
                .FirstOrDefaultAsync(s => s.MemberNo == memberNo &&
                                         s.Sharescode == sharesCode &&
                                         s.CompanyCode == companyCode);

            if (existingShare != null)
            {
                existingShare.TotalShares -= amount;

                // If total shares becomes negative, set to 0
                if (existingShare.TotalShares < 0)
                {
                    _logger.LogWarning($"Share balance would become negative for member {memberNo}. Setting to 0.");
                    existingShare.TotalShares = 0;
                }

                existingShare.TransDate = DateTime.Now;
                existingShare.AuditTime = DateTime.Now;
                existingShare.AuditDateTime = DateTime.Now;

                _context.Shares.Update(existingShare);
                _logger.LogInformation($"Updated share balance after reversal: {existingShare.TotalShares:C}");
            }
        }

        #region Contribution Deletion
        public async Task<bool> DeleteContributionAsync(int contributionId,string companyCode,string deletedBy,string reason)
        {
            // -------- 0. Input validation --------
            if (contributionId <= 0)
                throw new ArgumentException("Contribution id is required.", nameof(contributionId));
            if (string.IsNullOrWhiteSpace(deletedBy))
                throw new ArgumentException("DeletedBy (user) is required.", nameof(deletedBy));
            if (string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException("A reason is required to delete a contribution.", nameof(reason));

            using var transaction = await _context.Database.BeginTransactionAsync();

            try
            {
                _logger.LogInformation(
                    "DeleteContribution requested. Id={Id}, CallerCompany={Company}, By={User}, Reason={Reason}",
                    contributionId, companyCode, deletedBy, reason);

                // ============================================================
                // 1. LOAD THE CONTRIBUTION — NO COMPANY FILTER
                // ============================================================
                var contribution = await _context.Contribs
                    .Include(c => c.MemberNoNavigation)
                    .FirstOrDefaultAsync(c => c.Id == contributionId);

                if (contribution == null)
                    throw new InvalidOperationException(
                        $"Contribution {contributionId} was not found.");

                // Use the contribution's OWN company for all downstream writes.
                var targetCompanyCode = contribution.CompanyCode
                    ?? throw new InvalidOperationException(
                        $"Contribution {contributionId} has no company code.");

                var receiptNo = contribution.ReceiptNo ?? string.Empty;
                var txnNo = contribution.TransactionNo ?? string.Empty;
                var memberNo = contribution.MemberNo;
                var amount = contribution.Amount ?? 0;
                var sharesCode = contribution.Sharescode;

                _logger.LogInformation(
                    "Contribution found. Receipt={Receipt}, Txn={Txn}, Member={Member}, Amount={Amount}, Company={Company}",
                    receiptNo, txnNo, memberNo, amount, targetCompanyCode);

                // ============================================================
                // 2. RESOLVE THE WHOLE "DELETE GROUP"
                //    = original + every reversal that references it
                // ============================================================
                // A reversal can be linked to the original in several ways:
                //   a) ReceiptNo  = "{receiptNo}-REVERSAL"
                //   b) ReceiptNo  = "{receiptNo}-REV"
                //   c) TransactionNo = "{txnNo}-REV"
                //   d) Reversal row points back at the original via the same base
                //      receipt prefix (covers variants like "-REVERSAL1")
                // We collect all of them and delete the lot.
                var reversalReceipts = new[]
                {
            $"{receiptNo}-REVERSAL",
            $"{receiptNo}-REV"
        };
                var reversalTxns = new[]
                {
            $"{txnNo}-REV",
            $"{txnNo}-REVERSAL"
        };

                var reversals = await _context.Contribs
                    .Where(c => c.CompanyCode == targetCompanyCode
                             && c.Id != contribution.Id
                             && (
                                  reversalReceipts.Contains(c.ReceiptNo)
                               || reversalTxns.Contains(c.TransactionNo)
                               || (c.ReceiptNo != null && c.ReceiptNo.StartsWith(receiptNo + "-REV"))
                               || (c.TransactionNo != null && c.TransactionNo.StartsWith(txnNo + "-REV"))
                                ))
                    .ToListAsync();

                _logger.LogInformation(
                    "Reversal rows found for contribution {Id}: {Count}",
                    contributionId, reversals.Count);

                // ============================================================
                // 3. GATHER CHILD ROWS FOR THE ORIGINAL *AND* EACH REVERSAL
                // ============================================================
                // Build the full set of transaction numbers we're about to delete
                var allTxnNos = new List<string> { txnNo };
                allTxnNos.AddRange(reversals
                    .Where(r => !string.IsNullOrEmpty(r.TransactionNo))
                    .Select(r => r.TransactionNo!));

                var allReceiptNos = new List<string> { receiptNo };
                allReceiptNos.AddRange(reversals
                    .Where(r => !string.IsNullOrEmpty(r.ReceiptNo))
                    .Select(r => r.ReceiptNo!));

                // 3a. ContribShare rows for any of the above
                var contribShares = await _context.ContribShares
                    .Where(cs => cs.CompanyCode == targetCompanyCode
                              && (allTxnNos.Contains(cs.TransactionNo)
                                  || allReceiptNos.Contains(cs.ReceiptNo)))
                    .ToListAsync();

                // 3b. GL rows for any of the above
                var glTransactions = await _context.Gltransactions
                    .Where(g => g.CompanyCode == targetCompanyCode
                             && ((g.DocumentNo != null && allReceiptNos.Contains(g.DocumentNo))
                                 || (g.TransDescript != null
                                     && allReceiptNos.Any(r => g.TransDescript.Contains(r)))))
                    .ToListAsync();

                // 3c. Member's share row
                var share = await _context.Shares
                    .FirstOrDefaultAsync(s => s.MemberNo == memberNo
                                           && s.Sharescode == sharesCode
                                           && s.CompanyCode == targetCompanyCode);

                // 3d. Wallet row
                var memberRecord = await _context.Members
                    .FirstOrDefaultAsync(m => m.MemberNo == memberNo && m.CompanyCode == targetCompanyCode);

                var wallet = memberRecord != null
                    ? await _context.Wallets
                        .FirstOrDefaultAsync(w => w.MemberId == memberRecord.Id
                                               && w.CompanyCode == targetCompanyCode)
                    : null;

                _logger.LogInformation(
                    "Cascade summary for contribution {Id}: ContribShares={CS}, GL={GL}, Reversals={R}, ShareRow={SR}, WalletRow={W}",
                    contributionId, contribShares.Count, glTransactions.Count, reversals.Count,
                    share != null ? 1 : 0, wallet != null ? 1 : 0);

                // ============================================================
                // 4. SNAPSHOT FOR AUDIT / BLOCKCHAIN
                // ============================================================
                var snapshot = new
                {
                    contribution.Id,
                    contribution.ReceiptNo,
                    contribution.TransactionNo,
                    contribution.MemberNo,
                    contribution.Sharescode,
                    contribution.Amount,
                    contribution.ContrDate,
                    contribution.CompanyCode,
                    ReversalCount = reversals.Count,
                    ReversalIds = reversals.Select(r => r.Id).ToList(),
                    ReversalReceipts = reversals.Select(r => r.ReceiptNo).ToList(),
                    DeletedBy = deletedBy,
                    Reason = reason,
                    DeletedAt = DateTime.Now
                };

                // ============================================================
                // 5. BLOCKCHAIN RECORD (before any deletes)
                // ============================================================
                var blockchainTx = new BlockchainTransaction
                {
                    TransactionId = Guid.NewGuid().ToString(),
                    TransactionType = "CONTRIBUTION_PERMANENTLY_DELETED",
                    MemberNo = memberNo,
                    CompanyCode = targetCompanyCode,
                    Amount = amount,
                    Timestamp = DateTime.Now,
                    DataHash = await _blockchainService.GenerateTransactionHash(snapshot),
                    PayloadJson = System.Text.Json.JsonSerializer.Serialize(snapshot),
                    OffChainReferenceId = receiptNo,
                    Status = "CONFIRMED",
                    CreatedAt = DateTime.Now
                };
                _context.BlockchainTransactions.Add(blockchainTx);
                await _context.SaveChangesAsync();

                // ============================================================
                // 6. RESTORE THE LEDGER TO PRE-CONTRIBUTION STATE — ONCE
                // ============================================================
                // We are removing BOTH the original (+amount) and its
                // reversal (−amount). Net effect on the books is zero, which
                // is exactly what we want — but only if we actually did post
                // the ledger impact at original-creation time.
                //
                // Because the original could have been capital, deposits or
                // plain balance, we subtract from the same bucket the
                // original credited, using the ORIGINAL ContribShare row.
                var originalShare = contribShares
                    .FirstOrDefault(cs => cs.TransactionNo == txnNo);

                if (share != null && amount > 0)
                {
                    share.TotalShares -= amount;
                    if (share.TotalShares < 0) share.TotalShares = 0;
                    share.TransDate = DateTime.Now;
                    share.AuditTime = DateTime.Now;
                    share.AuditDateTime = DateTime.Now;
                    _context.Shares.Update(share);

                    _logger.LogInformation(
                        "Adjusted share balance by -{Amount:C} to {Balance:C} for member {Member}",
                        amount, share.TotalShares, memberNo);
                }

                if (wallet != null)
                {
                    if (originalShare?.ShareCapitalAmount > 0)
                    {
                        wallet.CapitalBalance -= amount;
                        wallet.Balance -= amount;
                    }
                    else if (originalShare?.DepositsAmount > 0)
                    {
                        wallet.DepositBalance -= amount;
                        wallet.Balance -= amount;
                    }
                    else
                    {
                        wallet.Balance -= amount;
                    }

                    wallet.LastActivity = DateTime.UtcNow;
                    _context.Wallets.Update(wallet);

                    _logger.LogInformation("Adjusted wallet balances for member {Member}", memberNo);
                }

                // ============================================================
                // 7. DELETE CHILD ROWS — reversals first, then original
                // ============================================================
                if (glTransactions.Any())
                {
                    _context.Gltransactions.RemoveRange(glTransactions);
                    _logger.LogInformation("Removed {Count} GL transaction(s)", glTransactions.Count);
                }

                if (contribShares.Any())
                {
                    _context.ContribShares.RemoveRange(contribShares);
                    _logger.LogInformation("Removed {Count} ContribShare row(s)", contribShares.Count);
                }

                if (reversals.Any())
                {
                    _context.Contribs.RemoveRange(reversals);
                    _logger.LogInformation("Removed {Count} reversal Contrib row(s)", reversals.Count);
                }

                await _context.SaveChangesAsync();

                // ============================================================
                // 8. DELETE THE ORIGINAL
                // ============================================================
                _context.Contribs.Remove(contribution);
                await _context.SaveChangesAsync();

                // ============================================================
                // 9. AUDIT TRAIL
                // ============================================================
                // Every support action writes a RuntimeData / AuditTrails row,
                // even though the underlying rows are gone. This is the only
                // permanent record of who deleted what and why, besides the
                // blockchain transaction.
                var (ip, browser, host) = GetClientInfo();
                var correlationId = GetCorrelationId();
                var now = DateTime.Now;

                // Compose the "old" state before deletion, so the audit row shows
                // exactly what the DB looked like at the moment of deletion.
                var auditOldValue = System.Text.Json.JsonSerializer.Serialize(new
                {
                    contribution = new
                    {
                        contribution.Id,
                        contribution.ReceiptNo,
                        contribution.TransactionNo,
                        contribution.MemberNo,
                        contribution.Sharescode,
                        contribution.Amount,
                        contribution.ContrDate,
                        contribution.CompanyCode,
                        contribution.Remarks,
                        contribution.Status
                    },
                    reversalCount = reversals.Count,
                    reversalIds = reversals.Select(r => r.Id).ToList(),
                    reversalReceipts = reversals.Select(r => r.ReceiptNo).ToList(),
                    reversalTxnNos = reversals.Select(r => r.TransactionNo).ToList(),
                    contribShareCount = contribShares.Count,
                    glTransactionCount = glTransactions.Count,
                    shareBalanceAfter = share?.TotalShares,
                    walletBalanceAfter = wallet?.Balance,
                    walletCapitalAfter = wallet?.CapitalBalance,
                    walletDepositAfter = wallet?.DepositBalance
                });

                // Compose the "new" state — all target rows removed.
                var auditNewValue = System.Text.Json.JsonSerializer.Serialize(new
                {
                    contributionId,
                    receiptNo,
                    transactionNo = txnNo,
                    deleted = true,
                    deletedAt = now,
                    deletedBy = deletedBy
                });

                // Extra structured context for analysts
                var auditExtraData = System.Text.Json.JsonSerializer.Serialize(new
                {
                    contributionId,
                    receiptNo,
                    txnNo,
                    memberNo,
                    memberName = contribution.MemberNoNavigation != null
                        ? $"{contribution.MemberNoNavigation.Surname} {contribution.MemberNoNavigation.OtherNames}".Trim()
                        : memberNo,
                    sharesCode,
                    amount,
                    companyCode = targetCompanyCode,
                    reason,
                    deletedBy,
                    deletedAt = now,
                    reversalCount = reversals.Count,
                    reversalIds = reversals.Select(r => r.Id).ToList(),
                    reversalReceipts = reversals.Select(r => r.ReceiptNo).ToList(),
                    contribShareCount = contribShares.Count,
                    glTransactionCount = glTransactions.Count,
                    shareAdjustment = -amount,
                    shareBalanceAfter = share?.TotalShares,
                    walletAdjustment = -amount,
                    blockchainTxId = blockchainTx.TransactionId,
                    correlationId
                });

                _context.AuditTrails.Add(new AuditTrail
                {
                    CompanyCode = targetCompanyCode,
                    UserId = deletedBy,
                    UserName = deletedBy,
                    ActionType = "CONTRIBUTION_DELETE",
                    ActionDescription = $"Permanently deleted contribution {receiptNo} for member {memberNo}",
                    TableName = "Contribs, ContribShares, Gltransactions, Shares, Wallets, BlockchainTransactions",
                    RecordId = receiptNo,
                    OldValue = auditOldValue,
                    NewValue = auditNewValue,

                    // ▼▼▼ Correctly populated client + correlation info ▼▼▼
                    IpAddress = ip,
                    BrowserAgent = browser,
                    HostName = host ?? GetHostNameFallback(),
                    CorrelationId = correlationId,

                    AuditTime = now,
                    Module = "CONTRIBUTION",
                    BlockchainTxId = blockchainTx.TransactionId,
                    ExtraData = auditExtraData
                });

                await _context.SaveChangesAsync();

                await transaction.CommitAsync();

                _logger.LogInformation(
                    "Contribution {Receipt} PERMANENTLY DELETED. Reversals={R}, ContribShares={CS}, GL={GL}, Tx={Tx}",
                    receiptNo, reversals.Count, contribShares.Count, glTransactions.Count,
                    blockchainTx.TransactionId);

                return true;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Error permanently deleting contribution {Id}", contributionId);
                throw;
            }
        }

        public async Task<ContributionResponseDTO?> GetContributionByReceiptOrTransactionAsync( string searchTerm, string companyCode)
        {
            if (string.IsNullOrWhiteSpace(searchTerm))
                return null;

            // Search at the DB level — no .Take(200), no company filter.
            // Trim + case-insensitive compare via ToLower on both sides.
            var term = searchTerm.Trim().ToLower();

            var row = await _context.Contribs
                .Where(c => (c.ReceiptNo != null && c.ReceiptNo.ToLower() == term)
                         || (c.TransactionNo != null && c.TransactionNo.ToLower() == term))
                .OrderByDescending(c => c.Id)      // prefer the most recent if duplicates exist
                .FirstOrDefaultAsync();

            if (row == null)
                return null;

            // Enrich with member + share-type info so the view has what it needs.
            var memberName = await _context.Members
                .Where(m => m.MemberNo == row.MemberNo && m.CompanyCode == row.CompanyCode)
                .Select(m => (m.Surname + " " + m.OtherNames).Trim())
                .FirstOrDefaultAsync() ?? row.MemberNo ?? "Unknown";

            var shareTypeName = await _context.Sharetypes
                .Where(s => s.SharesCode == row.Sharescode && s.CompanyCode == row.CompanyCode)
                .Select(s => s.SharesType)
                .FirstOrDefaultAsync() ?? row.Sharescode ?? "Unknown";

            return new ContributionResponseDTO
            {
                Id = row.Id,
                MemberNo = row.MemberNo ?? string.Empty,
                MemberName = memberName,
                ReceiptNo = row.ReceiptNo ?? string.Empty,
                TransactionNo = row.TransactionNo ?? string.Empty,
                Status = row.Status ?? string.Empty,
                SharesCode = row.Sharescode ?? string.Empty,
                ShareTypeName = shareTypeName,
                Amount = row.Amount ?? 0,
                TransactionDate = row.ContrDate ?? DateTime.MinValue,
                Remarks = row.Remarks ?? string.Empty,
                CompanyCode = row.CompanyCode ?? string.Empty,
                CreatedBy = row.AuditId ?? string.Empty,
                CreatedAt = row.AuditTime,
                BlockchainTxId = row.BlockchainTxId ?? string.Empty
            };
        }

        #endregion

        #region Journal Management
        public async Task<JournalActionResultDTO> ReverseJournalAsync( string voucherNo, string companyCode,string reversedBy, string reason)
        {
            if (string.IsNullOrWhiteSpace(voucherNo))
                throw new ArgumentException("Voucher number is required.", nameof(voucherNo));
            if (string.IsNullOrWhiteSpace(reversedBy))
                throw new ArgumentException("ReversedBy (user) is required.", nameof(reversedBy));
            if (string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException("A reason is required to reverse a journal.", nameof(reason));

            using var trx = await _context.Database.BeginTransactionAsync();

            try
            {
                _logger.LogInformation(
                    "ReverseJournal requested. Voucher={Voucher}, CallerCompany={Company}, By={User}",
                    voucherNo, companyCode, reversedBy);

                // -------- 1. Load original journal — NO COMPANY FILTER --------
                var originals = await _context.Journals
                    .Where(j => j.VNO == voucherNo)
                    .OrderBy(j => j.JVID)
                    .ToListAsync();

                if (!originals.Any())
                    throw new InvalidOperationException($"Journal {voucherNo} was not found.");

                if (!originals.First().POSTED)
                    throw new InvalidOperationException(
                        $"Journal {voucherNo} is still a draft. Delete it instead of reversing.");

                // Use the journal's OWN company from here on.
                var targetCompanyCode = originals.First().CompanyCode
                    ?? throw new InvalidOperationException(
                        $"Journal {voucherNo} has no company code.");

                // Guard: don't reverse twice
                var alreadyReversed = await _context.Journals
                    .AnyAsync(j => j.VNO == voucherNo + "-REV"
                                && j.CompanyCode == targetCompanyCode);
                if (alreadyReversed)
                    throw new InvalidOperationException(
                        $"Journal {voucherNo} has already been reversed.");

                // -------- 2. Totals --------
                decimal totalDr = originals.Where(j => j.TRANSTYPE == "DR").Sum(j => j.AMOUNT ?? 0);
                decimal totalCr = originals.Where(j => j.TRANSTYPE == "CR").Sum(j => j.AMOUNT ?? 0);

                if (Math.Abs(totalDr - totalCr) > 0.01m)
                    throw new InvalidOperationException(
                        $"Original journal is not balanced. DR={totalDr:N2}, CR={totalCr:N2}.");

                var first = originals.First();
                var now = DateTime.Now;
                var reversalVoucherNo = voucherNo + "-REV";
                var reversalTxnNo = $"TXN{now:yyyyMMdd}{Guid.NewGuid().ToString().Substring(0, 6).ToUpper()}";

                // -------- 3. Reversal rows --------
                var reversalJournals = new List<Journal>();
                var reversalListings = new List<JournalsListing>();

                foreach (var j in originals)
                {
                    var reversedType = j.TRANSTYPE == "DR" ? "CR" : "DR";

                    reversalJournals.Add(new Journal
                    {
                        VNO = reversalVoucherNo,
                        ACCNO = j.ACCNO,
                        NAME = j.NAME,
                        NARATION = $"REVERSAL of {voucherNo}: {reason}",
                        MEMBERNO = j.MEMBERNO,
                        SHARETYPE = j.SHARETYPE,
                        Loanno = j.Loanno,
                        AMOUNT = j.AMOUNT,
                        TRANSTYPE = reversedType,
                        TRANSDATE = now,
                        AUDITID = reversedBy,
                        AUDITDATE = now,
                        POSTED = true,
                        POSTEDDATE = now,
                        Transactionno = reversalTxnNo,
                        CompanyCode = targetCompanyCode      // ← journal's company
                    });

                    reversalListings.Add(new JournalsListing
                    {
                        VoucherNo = reversalVoucherNo,
                        AccountNo = j.ACCNO,
                        AccountName = j.NAME,
                        Narration = $"REVERSAL of {voucherNo}: {reason}",
                        MemberNo = j.MEMBERNO,
                        ShareType = j.SHARETYPE,
                        LoanNo = j.Loanno,
                        AmountDr = reversedType == "DR" ? j.AMOUNT : (decimal?)null,
                        AmountCr = reversedType == "CR" ? j.AMOUNT : (decimal?)null,
                        Amount = j.AMOUNT,
                        TransType = reversedType,
                        AuditId = reversedBy,
                        TransDate = now,
                        AuditDate = now,
                        Posted = true,
                        PostedDate = now,
                        TransactionNo = reversalTxnNo,
                        CompanyCode = targetCompanyCode     // ← journal's company
                    });
                }

                // -------- 4. Offsetting GL rows --------
                var originalGl = await _context.Gltransactions
                    .Where(g => g.DocumentNo == voucherNo
                             && g.CompanyCode == targetCompanyCode
                             && g.Source == "JOURNAL")
                    .ToListAsync();

                var reversalGl = new List<Gltransaction>();
                foreach (var g in originalGl)
                {
                    reversalGl.Add(new Gltransaction
                    {
                        TransDate = now,
                        Amount = g.Amount,
                        DrAccNo = g.CrAccNo,
                        CrAccNo = g.DrAccNo,
                        DocumentNo = reversalVoucherNo,
                        Source = "JOURNAL",
                        Temp = "JV-REV",
                        CompanyCode = targetCompanyCode,     // ← journal's company
                        TransDescript = $"REVERSAL of {voucherNo}: {reason}",
                        AuditId = reversedBy,
                        AuditTime = now,
                        TransactionNo = reversalTxnNo,
                        DocPosted = 1,
                        Module = "GL",
                        AuditDateTime = now
                    });
                }

                // -------- 5. Blockchain record --------
                var blockchainData = new
                {
                    Action = "JOURNAL_REVERSAL",
                    OriginalVoucher = voucherNo,
                    ReversalVoucher = reversalVoucherNo,
                    TotalDebit = totalDr,
                    TotalCredit = totalCr,
                    CompanyCode = targetCompanyCode,
                    Reason = reason,
                    ReversedBy = reversedBy,
                    ReversedAt = now
                };

                var blockchainTx = new BlockchainTransaction
                {
                    TransactionId = Guid.NewGuid().ToString(),
                    TransactionType = "JOURNAL_REVERSAL",
                    MemberNo = first.MEMBERNO ?? "SYSTEM",
                    CompanyCode = targetCompanyCode,      // ← journal's company
                    Amount = totalDr,
                    Timestamp = now,
                    DataHash = await _blockchainService.GenerateTransactionHash(blockchainData),
                    PayloadJson = System.Text.Json.JsonSerializer.Serialize(blockchainData),
                    OffChainReferenceId = voucherNo,
                    Status = "CONFIRMED",
                    CreatedAt = now
                };
                _context.BlockchainTransactions.Add(blockchainTx);
                await _context.SaveChangesAsync();

                // -------- 6. Mark original as reversed --------
                foreach (var j in originals)
                {
                    j.NARATION = $"[REVERSED by {reversalVoucherNo}] {j.NARATION}";
                }

                // -------- 7. Attach new rows --------
                foreach (var g in reversalGl) g.BlockchainTxId = blockchainTx.TransactionId;
                foreach (var j in reversalJournals) j.BlockchainTxId = blockchainTx.TransactionId;
                foreach (var l in reversalListings) l.BlockchainTxId = blockchainTx.TransactionId;

                await _context.Journals.AddRangeAsync(reversalJournals);
                await _context.JournalsListings.AddRangeAsync(reversalListings);
                if (reversalGl.Any()) await _context.Gltransactions.AddRangeAsync(reversalGl);

                // -------- 8. Audit --------
                var (ip, browser, host) = GetClientInfo();
                var correlationId = GetCorrelationId();

                _context.AuditTrails.Add(new AuditTrail
                {
                    CompanyCode = targetCompanyCode,
                    UserId = reversedBy,
                    UserName = reversedBy,
                    ActionType = "JOURNAL_REVERSE",
                    ActionDescription = $"Reversed journal {voucherNo}",
                    TableName = "Journals, JournalsListing, Gltransactions",
                    RecordId = voucherNo,
                    OldValue = $"{{\"voucher\":\"{voucherNo}\",\"status\":\"POSTED\"}}",
                    NewValue = $"{{\"voucher\":\"{voucherNo}\",\"reversal\":\"{reversalVoucherNo}\",\"tx\":\"{blockchainTx.TransactionId}\"}}",

                    // ▼▼▼ Now correctly populated ▼▼▼
                    IpAddress = ip,
                    BrowserAgent = browser,
                    HostName = host ?? GetHostNameFallback(),
                    CorrelationId = correlationId,

                    AuditTime = now,
                    Module = "JOURNAL",
                    BlockchainTxId = blockchainTx.TransactionId,
                    ExtraData = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        originalVoucher = voucherNo,
                        reversalVoucher = reversalVoucherNo,
                        totalDebit = totalDr,
                        totalCredit = totalCr,
                        reason
                    })
                });

                await _context.SaveChangesAsync();
                await trx.CommitAsync();

                _logger.LogInformation(
                    "Journal {Voucher} reversed to {Reversal}. Company={Company}, Tx={Tx}",
                    voucherNo, reversalVoucherNo, targetCompanyCode, blockchainTx.TransactionId);

                return new JournalActionResultDTO
                {
                    Success = true,
                    Message = $"Journal {voucherNo} reversed. Reversal voucher: {reversalVoucherNo}",
                    VoucherNo = voucherNo,
                    ReversalVoucherNo = reversalVoucherNo,
                    BlockchainTxId = blockchainTx.TransactionId,
                    GlRowsAffected = reversalGl.Count,
                    TotalDebit = totalDr,
                    TotalCredit = totalCr
                };
            }
            catch (Exception ex)
            {
                await trx.RollbackAsync();
                _logger.LogError(ex, "Error reversing journal {Voucher}", voucherNo);
                throw;
            }
        }

        public async Task<JournalActionResultDTO> DeleteJournalAsync(string voucherNo, string companyCode,string deletedBy, string reason)
        {
            if (string.IsNullOrWhiteSpace(voucherNo))
                throw new ArgumentException("Voucher number is required.", nameof(voucherNo));
            if (string.IsNullOrWhiteSpace(deletedBy))
                throw new ArgumentException("DeletedBy (user) is required.", nameof(deletedBy));
            if (string.IsNullOrWhiteSpace(reason))
                throw new ArgumentException("A reason is required to delete a journal.", nameof(reason));

            using var trx = await _context.Database.BeginTransactionAsync();

            try
            {
                _logger.LogInformation(
                    "DeleteJournal requested. Voucher={Voucher}, CallerCompany={Company}, By={User}",
                    voucherNo, companyCode, deletedBy);

                // -------- 1. Load — NO COMPANY FILTER --------
                var journals = await _context.Journals
                    .Where(j => j.VNO == voucherNo)
                    .ToListAsync();

                if (!journals.Any())
                    throw new InvalidOperationException($"Journal {voucherNo} was not found.");

                // Use the journal's OWN company from here on.
                var targetCompanyCode = journals.First().CompanyCode
                    ?? throw new InvalidOperationException(
                        $"Journal {voucherNo} has no company code.");

                var listings = await _context.JournalsListings
                    .Where(l => l.VoucherNo == voucherNo
                             && l.CompanyCode == targetCompanyCode)
                    .ToListAsync();

                // -------- 2. Guard: posted journals cannot be deleted --------
                if ((journals.Any() && journals.First().POSTED) ||
                    (listings.Any() && listings.First().Posted))
                {
                    throw new InvalidOperationException(
                        $"Journal {voucherNo} is already posted. Use Reverse instead of Delete.");
                }

                // -------- 3. Guard: any GL rows --------
                var glRows = await _context.Gltransactions
                    .Where(g => g.DocumentNo == voucherNo
                             && g.CompanyCode == targetCompanyCode
                             && g.Source == "JOURNAL")
                    .ToListAsync();

                if (glRows.Any())
                    throw new InvalidOperationException(
                        $"Journal {voucherNo} has GL postings and cannot be deleted. Use Reverse instead.");

                // -------- 4. Snapshot --------
                var now = DateTime.Now;
                var snapshot = new
                {
                    VoucherNo = voucherNo,
                    JournalCount = journals.Count,
                    ListingCount = listings.Count,
                    CompanyCode = targetCompanyCode,
                    DeletedBy = deletedBy,
                    Reason = reason,
                    DeletedAt = now
                };

                // -------- 5. Blockchain record --------
                var blockchainTx = new BlockchainTransaction
                {
                    TransactionId = Guid.NewGuid().ToString(),
                    TransactionType = "JOURNAL_DELETED",
                    MemberNo = journals.FirstOrDefault()?.MEMBERNO ?? "SYSTEM",
                    CompanyCode = targetCompanyCode,
                    Amount = journals.Sum(j => j.AMOUNT ?? 0),
                    Timestamp = now,
                    DataHash = await _blockchainService.GenerateTransactionHash(snapshot),
                    PayloadJson = System.Text.Json.JsonSerializer.Serialize(snapshot),
                    OffChainReferenceId = voucherNo,
                    Status = "CONFIRMED",
                    CreatedAt = now
                };
                _context.BlockchainTransactions.Add(blockchainTx);
                await _context.SaveChangesAsync();

                // -------- 6. Remove listings then journals --------
                if (listings.Any()) _context.JournalsListings.RemoveRange(listings);
                if (journals.Any()) _context.Journals.RemoveRange(journals);

                // -------- 7. Audit --------
                var (ip, browser, host) = GetClientInfo();
                var correlationId = GetCorrelationId();

                _context.AuditTrails.Add(new AuditTrail
                {
                    CompanyCode = targetCompanyCode,
                    UserId = deletedBy,
                    UserName = deletedBy,
                    ActionType = "JOURNAL_DELETE",
                    ActionDescription = $"Deleted draft journal {voucherNo}",
                    TableName = "Journals, JournalsListing",
                    RecordId = voucherNo,
                    OldValue = $"{{\"voucher\":\"{voucherNo}\",\"status\":\"DRAFT\"}}",
                    NewValue = "null",

                    // ▼▼▼ Now correctly populated ▼▼▼
                    IpAddress = ip,
                    BrowserAgent = browser,
                    HostName = host ?? GetHostNameFallback(),
                    CorrelationId = correlationId,

                    AuditTime = now,
                    Module = "JOURNAL",
                    BlockchainTxId = blockchainTx.TransactionId,
                    ExtraData = System.Text.Json.JsonSerializer.Serialize(snapshot)
                });

                await _context.SaveChangesAsync();
                await trx.CommitAsync();

                _logger.LogInformation(
                    "Journal {Voucher} DELETED. Company={Company}, Journals={J}, Listings={L}, Tx={Tx}",
                    voucherNo, targetCompanyCode, journals.Count, listings.Count, blockchainTx.TransactionId);

                return new JournalActionResultDTO
                {
                    Success = true,
                    Message = $"Journal {voucherNo} deleted.",
                    VoucherNo = voucherNo,
                    BlockchainTxId = blockchainTx.TransactionId,
                    GlRowsAffected = 0
                };
            }
            catch (Exception ex)
            {
                await trx.RollbackAsync();
                _logger.LogError(ex, "Error deleting journal {Voucher}", voucherNo);
                throw;
            }
        }

        #endregion

        #region Audit Helpers
        private (string? IpAddress, string? BrowserAgent, string? HostName) GetClientInfo()
        {
            var http = _httpContextAccessor?.HttpContext;
            if (http == null) return (null, null, null);

            // -------- IP address --------
            string? ip = null;

            // Prefer X-Forwarded-For when behind a proxy / reverse-proxy / LB
            var forwarded = http.Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(forwarded))
            {
                // XFF is a comma-separated chain: client, proxy1, proxy2, ...
                ip = forwarded.Split(',')[0].Trim();
            }

            // Fall back to the connection's remote address
            if (string.IsNullOrWhiteSpace(ip))
            {
                ip = http.Connection.RemoteIpAddress?.ToString();
            }

            // Normalise loopback for readability
            if (ip == "::1") ip = "127.0.0.1";

            // -------- Browser agent --------
            string? ua = http.Request.Headers["User-Agent"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(ua) && ua.Length > 500)
            {
                // Some browsers send huge UAs; trim to fit StringLength(500) if needed
                ua = ua.Substring(0, 500);
            }

            // -------- Host name --------
            // The server that handled the request (not the client's machine name;
            // a web app can't reliably know the client's machine name).
            string? hostName = http.Request.Host.Host;   // e.g. "easysacco.local"

            return (ip, ua, hostName);
        }

        /// <summary>
        /// Correlation id shared by every audit row written during one request.
        /// Reuses HttpContext.TraceIdentifier, which ASP.NET Core generates once
        /// per request and reuses across everything logged in that scope.
        /// </summary>
        private string GetCorrelationId()
        {
            var http = _httpContextAccessor?.HttpContext;
            if (http != null && !string.IsNullOrEmpty(http.TraceIdentifier))
                return http.TraceIdentifier;

            return Guid.NewGuid().ToString("N");
        }

        /// <summary>
        /// Best-effort host name of the machine running the app. Useful when the
        /// Host header is empty (e.g. invoked from a worker).
        /// </summary>
        private string GetHostNameFallback()
        {
            try { return System.Net.Dns.GetHostName(); }
            catch { return Environment.MachineName; }
        }

        #endregion
    }

}
