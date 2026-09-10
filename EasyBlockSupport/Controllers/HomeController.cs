using EasyBlockSupport.Data;
using EasyBlockSupport.Models;
using EasyBlockSupport.Models.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Security.Claims;

namespace EasyBlockSupport.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly ApplicationDbContext _context;

        public HomeController(ILogger<HomeController> logger, ApplicationDbContext context)
        {
            _logger = logger;
            _context = context;
        }

        [Authorize]
        public async Task<IActionResult> Index()
        {
            try
            {
                // ===== User context =====
                ViewBag.UserName = User.Identity?.Name;
                ViewBag.CompanyName = User.FindFirstValue("CompanyName");
                ViewBag.UserRole = User.FindFirstValue(ClaimTypes.Role);
                ViewBag.CurrentCompanyCode = User.FindFirstValue("CompanyCode");

                var model = new DashboardViewModel();

                // ============================================================
                // KPI CARDS
                // ============================================================

                // Total members + gender split
                model.TotalMembers = await _context.Members.CountAsync();
                model.TotalMaleMembers = await _context.Members
                    .CountAsync(m => m.Sex != null && (m.Sex == "M" || m.Sex == "Male"));
                model.TotalFemaleMembers = await _context.Members
                    .CountAsync(m => m.Sex != null && (m.Sex == "F" || m.Sex == "Female"));
                model.TotalUnknownGender = model.TotalMembers - model.TotalMaleMembers - model.TotalFemaleMembers;

                // Users & Companies
                model.TotalUsers = await _context.UserAccounts1.CountAsync();
                model.TotalCompanies = await _context.Companies.CountAsync();

                // Loans
                model.TotalLoans = await _context.Loans.CountAsync();
                model.TotalLoanAmount = await _context.Loans
                    .Where(l => l.LoanAmt != null)
                    .SumAsync(l => l.LoanAmt ?? 0);

                // Active loans (status 4,5,6 = Approved, Endorsed, Disbursed)
                model.ActiveLoans = await _context.Loans
                    .CountAsync(l => l.Status == 4 || l.Status == 5 || l.Status == 6);

                // Contributions (ContribShare)
                model.TotalDeposits = await _context.ContribShares
                    .Where(c => c.DepositsAmount != null)
                    .SumAsync(c => c.DepositsAmount ?? 0);

                model.TotalShareCapital = await _context.ContribShares
                    .Where(c => c.ShareCapitalAmount != null)
                    .SumAsync(c => c.ShareCapitalAmount ?? 0);

                model.TotalRegFees = await _context.ContribShares
                    .Where(c => c.RegFeeAmount != null)
                    .SumAsync(c => c.RegFeeAmount ?? 0);

                model.TotalContributions = model.TotalDeposits + model.TotalShareCapital + model.TotalRegFees;

                // ============================================================
                // LINE CHART: MEMBERS vs USERS (last 12 months)
                // ============================================================
                var now = DateTime.Now;
                var startDate = new DateTime(now.Year, now.Month, 1).AddMonths(-11);

                var months = new List<string>();
                for (int i = 0; i < 12; i++)
                {
                    months.Add(startDate.AddMonths(i).ToString("MMM yyyy"));
                }
                model.Months = months;

                // Members per month (by DateCreated/AsAtDate/ApplicDate — using AsAtDate)
                var membersByMonth = await _context.Members
                    .Where(m => m.AsAtDate != null && m.AsAtDate >= startDate)
                    .GroupBy(m => new { m.AsAtDate.Value.Year, m.AsAtDate.Value.Month })
                    .Select(g => new { g.Key.Year, g.Key.Month, Count = g.Count() })
                    .ToListAsync();

                // Users per month (by DateCreated)
                var usersByMonth = await _context.UserAccounts1
                    .Where(u => u.DateCreated != null && u.DateCreated >= startDate)
                    .GroupBy(u => new { u.DateCreated.Value.Year, u.DateCreated.Value.Month })
                    .Select(g => new { g.Key.Year, g.Key.Month, Count = g.Count() })
                    .ToListAsync();

                // Companies per month (by AuditTime)
                var companiesByMonth = await _context.Companies
                    .Where(c => c.AuditTime != null && c.AuditTime >= startDate)
                    .GroupBy(c => new { c.AuditTime.Value.Year, c.AuditTime.Value.Month })
                    .Select(g => new { g.Key.Year, g.Key.Month, Count = g.Count() })
                    .ToListAsync();

                // Loans per month
                var loansByMonth = await _context.Loans
                    .Where(l => l.ApplicDate >= startDate)
                    .GroupBy(l => new { l.ApplicDate.Year, l.ApplicDate.Month })
                    .Select(g => new
                    {
                        g.Key.Year,
                        g.Key.Month,
                        Count = g.Count(),
                        Amount = g.Sum(x => x.LoanAmt ?? 0)
                    })
                    .ToListAsync();

                // Fill arrays (0 for missing months)
                for (int i = 0; i < 12; i++)
                {
                    var d = startDate.AddMonths(i);

                    model.MembersPerMonth.Add(
                        membersByMonth.FirstOrDefault(x => x.Year == d.Year && x.Month == d.Month)?.Count ?? 0);

                    model.UsersPerMonth.Add(
                        usersByMonth.FirstOrDefault(x => x.Year == d.Year && x.Month == d.Month)?.Count ?? 0);

                    model.CompaniesPerMonth.Add(
                        companiesByMonth.FirstOrDefault(x => x.Year == d.Year && x.Month == d.Month)?.Count ?? 0);

                    model.LoansPerMonth.Add(
                        loansByMonth.FirstOrDefault(x => x.Year == d.Year && x.Month == d.Month)?.Count ?? 0);

                    model.LoanAmountsPerMonth.Add(
                        loansByMonth.FirstOrDefault(x => x.Year == d.Year && x.Month == d.Month)?.Amount ?? 0);
                }

                // ============================================================
                // LOANS BY GENDER
                // ============================================================
                var loansWithMembers = await (from l in _context.Loans
                                              join m in _context.Members on l.MemberNo equals m.MemberNo
                                              select new { l.LoanAmt, m.Sex })
                                             .ToListAsync();

                model.MaleLoanCount = loansWithMembers.Count(x => x.Sex == "M" || x.Sex == "Male");
                model.FemaleLoanCount = loansWithMembers.Count(x => x.Sex == "F" || x.Sex == "Female");
                model.MaleLoanAmount = loansWithMembers
                    .Where(x => x.Sex == "M" || x.Sex == "Male")
                    .Sum(x => x.LoanAmt ?? 0);
                model.FemaleLoanAmount = loansWithMembers
                    .Where(x => x.Sex == "F" || x.Sex == "Female")
                    .Sum(x => x.LoanAmt ?? 0);

                // ============================================================
                // RECENT TRANSACTIONS
                // ============================================================
                model.RecentTransactions = await _context.BlockchainTransactions
                    .OrderByDescending(t => t.Timestamp)
                    .Take(10)
                    .Select(t => new RecentTransactionDto
                    {
                        TransactionId = t.TransactionId,
                        TransactionType = t.TransactionType,
                        MemberNo = t.MemberNo ?? "-",
                        CompanyCode = t.CompanyCode ?? "-",
                        Amount = t.Amount,
                        Timestamp = t.Timestamp,
                        Status = t.Status
                    })
                    .ToListAsync();

                return View(model);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading dashboard");
                ViewBag.ErrorMessage = "Could not load full dashboard data. Please try again.";
                return View(new DashboardViewModel());
            }
        }

        public IActionResult Privacy()
        {
            return View();
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}