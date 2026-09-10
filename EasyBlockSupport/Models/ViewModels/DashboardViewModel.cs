using System;
using System.Collections.Generic;

namespace EasyBlockSupport.Models.ViewModels
{
    public class DashboardViewModel
    {
        // ===== KPI Cards =====
        public int TotalMembers { get; set; }
        public int TotalMaleMembers { get; set; }
        public int TotalFemaleMembers { get; set; }
        public int TotalUnknownGender { get; set; }

        public int TotalUsers { get; set; }
        public int TotalCompanies { get; set; }

        public int TotalLoans { get; set; }
        public decimal TotalLoanAmount { get; set; }
        public int ActiveLoans { get; set; }

        public decimal TotalDeposits { get; set; }
        public decimal TotalShareCapital { get; set; }
        public decimal TotalRegFees { get; set; }
        public decimal TotalContributions { get; set; }

        // ===== Charts =====
        public List<string> Months { get; set; } = new();
        public List<int> MembersPerMonth { get; set; } = new();
        public List<int> UsersPerMonth { get; set; } = new();
        public List<int> CompaniesPerMonth { get; set; } = new();

        public List<int> LoansPerMonth { get; set; } = new();
        public List<decimal> LoanAmountsPerMonth { get; set; } = new();

        // Loans per gender
        public int MaleLoanCount { get; set; }
        public int FemaleLoanCount { get; set; }
        public decimal MaleLoanAmount { get; set; }
        public decimal FemaleLoanAmount { get; set; }

        // ===== Recent Activity =====
        public List<RecentTransactionDto> RecentTransactions { get; set; } = new();
    }

    public class RecentTransactionDto
    {
        public string TransactionId { get; set; } = string.Empty;
        public string TransactionType { get; set; } = string.Empty;
        public string MemberNo { get; set; } = string.Empty;
        public string CompanyCode { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public DateTime Timestamp { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}