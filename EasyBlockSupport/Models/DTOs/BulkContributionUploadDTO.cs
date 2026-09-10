using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;

namespace EasyBlockSupport.Models.DTOs
{
    public class BulkContributionUploadDTO
    {
        public IFormFile File { get; set; }
        public string CompanyCode { get; set; }
        public string CreatedBy { get; set; }
        public bool AutoPost { get; set; } = true;
        public bool ValidateOnly { get; set; } = false;
    }

    public class BulkContributionRowDTO
    {
        public int RowNumber { get; set; }
        public string MemberNo { get; set; }
        public string SharesCode { get; set; }
        public decimal Amount { get; set; }
        public DateTime? TransactionDate { get; set; }
        public DateTime? DepositedDate { get; set; }
        public string PaymentMethod { get; set; } // CASH, CHEQUE, BANK TRANSFER, MOBILE MONEY
        public string ReferenceNo { get; set; }
        public string Remarks { get; set; }
        public string ReceiptNo { get; set; } // Optional - system generates if empty

        // Validation properties
        public bool IsValid { get; set; }
        public string ValidationMessage { get; set; }
        public int? ProcessedContribId { get; set; }
        public string ProcessedReceiptNo { get; set; }
        public string ProcessedTransactionNo { get; set; }
        public string BlockchainTxId { get; set; }
        public string TransactionNo { get; set; }
    }

    public class BulkContributionResultDTO
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public int TotalRows { get; set; }
        public int ValidRows { get; set; }
        public int InvalidRows { get; set; }
        public int SuccessfullyProcessed { get; set; }
        public int FailedRows { get; set; }
        public List<BulkContributionRowDTO> Rows { get; set; } = new();
        public List<string> Errors { get; set; } = new();
        public string BatchReference { get; set; }
        public DateTime ProcessedAt { get; set; }
        public string ProcessedBy { get; set; }
        public string CompanyCode { get; set; }
    }

    public class ContributionDTO
    {
        public string MemberNo { get; set; }
        public string SharesCode { get; set; }
        public decimal Amount { get; set; }
        public DateTime TransactionDate { get; set; }
        public DateTime? DepositedDate { get; set; }
        public string PaymentMethod { get; set; } = "CASH";
        public string ReferenceNo { get; set; }
        public string Remarks { get; set; }
        public string CompanyCode { get; set; }
        public string CreatedBy { get; set; }
        public string ReceiptNo { get; set; }
        public string TransactionNo { get; set; }
    }
}