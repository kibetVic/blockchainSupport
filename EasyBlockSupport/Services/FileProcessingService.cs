using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using OfficeOpenXml;
using EasyBlockSupport.Models.DTOs;

namespace EasyBlockSupport.Services
{
    public interface IFileProcessingService
    {
        Task<List<BulkContributionRowDTO>> ParseExcelFileAsync(IFormFile file, string companyCode);
        Task<byte[]> GenerateErrorReportAsync(List<BulkContributionRowDTO> rows);
        Task<byte[]> GenerateSuccessReportAsync(List<BulkContributionRowDTO> rows, string batchReference);
        Task<byte[]> GenerateTemplateAsync();
    }

    public class FileProcessingService : IFileProcessingService
    {
        private readonly ILogger<FileProcessingService> _logger;

        public FileProcessingService(ILogger<FileProcessingService> logger)
        {
            _logger = logger;
            // Set EPPlus license context (static)
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        }

        public async Task<List<BulkContributionRowDTO>> ParseExcelFileAsync(IFormFile file, string companyCode)
        {
            _logger.LogInformation($"Parsing Excel file: {file.FileName}");

            var rows = new List<BulkContributionRowDTO>();

            using var stream = new MemoryStream();
            await file.CopyToAsync(stream);
            stream.Position = 0;

            using var package = new ExcelPackage(stream);
            var worksheet = package.Workbook.Worksheets[0];
            if (worksheet == null)
            {
                throw new Exception("No worksheet found in the Excel file");
            }

            // Get column headers from first row
            var headers = new Dictionary<string, int>();
            for (int col = 1; col <= worksheet.Dimension.Columns; col++)
            {
                var header = worksheet.Cells[1, col].Text?.Trim();
                if (!string.IsNullOrEmpty(header))
                {
                    headers[header.ToLower()] = col;
                }
            }

            // Validate required columns
            var requiredColumns = new[] { "memberno", "sharescode", "amount" };
            foreach (var required in requiredColumns)
            {
                if (!headers.ContainsKey(required))
                {
                    throw new Exception($"Required column '{required}' not found in the Excel file");
                }
            }

            // Process rows (skip header row)
            for (int row = 2; row <= worksheet.Dimension.Rows; row++)
            {
                try
                {
                    var rowDto = new BulkContributionRowDTO
                    {
                        RowNumber = row - 1,
                        IsValid = true
                    };

                    // Map columns based on headers
                    foreach (var header in headers)
                    {
                        var cellValue = worksheet.Cells[row, header.Value].Text?.Trim();

                        switch (header.Key)
                        {
                            case "memberno":
                                rowDto.MemberNo = cellValue;
                                break;
                            case "sharescode":
                                rowDto.SharesCode = cellValue;
                                break;
                            case "amount":
                                if (decimal.TryParse(cellValue, out decimal amount))
                                {
                                    rowDto.Amount = amount;
                                }
                                break;
                            case "transactiondate":
                            case "contrdate":
                                if (DateTime.TryParse(cellValue, out DateTime txDate))
                                {
                                    rowDto.TransactionDate = txDate;
                                }
                                break;
                            case "depositeddate":
                                if (DateTime.TryParse(cellValue, out DateTime depDate))
                                {
                                    rowDto.DepositedDate = depDate;
                                }
                                break;
                            case "paymentmethod":
                                rowDto.PaymentMethod = cellValue;
                                break;
                            case "referenceno":
                            case "refno":
                                rowDto.ReferenceNo = cellValue;
                                break;
                            case "remarks":
                                rowDto.Remarks = cellValue;
                                break;
                            case "receiptno":
                                rowDto.ReceiptNo = cellValue;
                                break;
                        }
                    }

                    // Set default values if not provided
                    if (!rowDto.TransactionDate.HasValue)
                        rowDto.TransactionDate = DateTime.Now;
                    if (!rowDto.DepositedDate.HasValue)
                        rowDto.DepositedDate = DateTime.Now;
                    if (string.IsNullOrEmpty(rowDto.PaymentMethod))
                        rowDto.PaymentMethod = "CASH";

                    rows.Add(rowDto);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error parsing row {row}: {ex.Message}");
                    rows.Add(new BulkContributionRowDTO
                    {
                        RowNumber = row - 1,
                        IsValid = false,
                        ValidationMessage = $"Error parsing row: {ex.Message}"
                    });
                }
            }

            _logger.LogInformation($"Parsed {rows.Count} rows from Excel file");
            return rows;
        }

        public async Task<byte[]> GenerateErrorReportAsync(List<BulkContributionRowDTO> rows)
        {
            using var stream = new MemoryStream();
            using var package = new ExcelPackage(stream);

            var worksheet = package.Workbook.Worksheets.Add("Error Report");

            // Headers
            worksheet.Cells[1, 1].Value = "Row Number";
            worksheet.Cells[1, 2].Value = "Member No";
            worksheet.Cells[1, 3].Value = "Shares Code";
            worksheet.Cells[1, 4].Value = "Amount";
            worksheet.Cells[1, 5].Value = "Validation Error";
            worksheet.Cells[1, 6].Value = "Payment Method";
            worksheet.Cells[1, 7].Value = "Reference No";

            // Apply header styling
            using (var range = worksheet.Cells[1, 1, 1, 7])
            {
                range.Style.Font.Bold = true;
                range.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                range.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.LightGray);
            }

            int rowIndex = 2;
            var invalidRows = rows.Where(r => !r.IsValid).ToList();

            foreach (var row in invalidRows)
            {
                worksheet.Cells[rowIndex, 1].Value = row.RowNumber;
                worksheet.Cells[rowIndex, 2].Value = row.MemberNo;
                worksheet.Cells[rowIndex, 3].Value = row.SharesCode;
                worksheet.Cells[rowIndex, 4].Value = row.Amount;
                worksheet.Cells[rowIndex, 5].Value = row.ValidationMessage;
                worksheet.Cells[rowIndex, 6].Value = row.PaymentMethod;
                worksheet.Cells[rowIndex, 7].Value = row.ReferenceNo;
                rowIndex++;
            }

            worksheet.Cells[worksheet.Dimension.Address].AutoFitColumns();
            await package.SaveAsync();
            return stream.ToArray();
        }

        public async Task<byte[]> GenerateSuccessReportAsync(List<BulkContributionRowDTO> rows, string batchReference)
        {
            using var stream = new MemoryStream();
            using var package = new ExcelPackage(stream);

            var worksheet = package.Workbook.Worksheets.Add("Success Report");

            // Headers
            worksheet.Cells[1, 1].Value = "Row Number";
            worksheet.Cells[1, 2].Value = "Member No";
            worksheet.Cells[1, 3].Value = "Shares Code";
            worksheet.Cells[1, 4].Value = "Amount";
            worksheet.Cells[1, 5].Value = "Receipt No";
            worksheet.Cells[1, 6].Value = "Transaction No";
            worksheet.Cells[1, 7].Value = "Blockchain Tx ID";

            // Apply header styling
            using (var range = worksheet.Cells[1, 1, 1, 7])
            {
                range.Style.Font.Bold = true;
                range.Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                range.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.LightGray);
            }

            int rowIndex = 2;
            var successRows = rows.Where(r => r.IsValid && r.ProcessedContribId.HasValue).ToList();

            foreach (var row in successRows)
            {
                worksheet.Cells[rowIndex, 1].Value = row.RowNumber;
                worksheet.Cells[rowIndex, 2].Value = row.MemberNo;
                worksheet.Cells[rowIndex, 3].Value = row.SharesCode;
                worksheet.Cells[rowIndex, 4].Value = row.Amount;
                worksheet.Cells[rowIndex, 5].Value = row.ProcessedReceiptNo;
                worksheet.Cells[rowIndex, 6].Value = row.ProcessedTransactionNo;
                worksheet.Cells[rowIndex, 7].Value = row.BlockchainTxId;
                rowIndex++;
            }

            worksheet.Cells[worksheet.Dimension.Address].AutoFitColumns();
            await package.SaveAsync();
            return stream.ToArray();
        }

        public async Task<byte[]> GenerateTemplateAsync()
        {
            using var stream = new MemoryStream();
            using var package = new ExcelPackage(stream);
            var worksheet = package.Workbook.Worksheets.Add("Bulk Contributions Template");

            // Create headers with all required fields
            var headers = new[]
            {
                "MemberNo*",
                "SharesCode*",
                "Amount*",
                "TransactionDate",
                "DepositedDate",
                "PaymentMethod",
                "ReferenceNo",
                "Remarks",
                "ReceiptNo"
            };

            for (int i = 0; i < headers.Length; i++)
            {
                worksheet.Cells[1, i + 1].Value = headers[i];
                worksheet.Cells[1, i + 1].Style.Font.Bold = true;
                worksheet.Cells[1, i + 1].Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                worksheet.Cells[1, i + 1].Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.LightGray);
            }

            // Add example data with realistic values
            var examples = new[]
            {
                new { MemberNo = "M001", SharesCode = "SC001", Amount = 1000.00, TransactionDate = DateTime.Now.AddDays(-5), DepositedDate = DateTime.Now.AddDays(-5), PaymentMethod = "CASH", ReferenceNo = "REF12345", Remarks = "Monthly contribution", ReceiptNo = "" },
                new { MemberNo = "M002", SharesCode = "SC002", Amount = 500.00, TransactionDate = DateTime.Now.AddDays(-3), DepositedDate = DateTime.Now.AddDays(-3), PaymentMethod = "BANK TRANSFER", ReferenceNo = "REF12346", Remarks = "Savings deposit", ReceiptNo = "" },
                new { MemberNo = "M003", SharesCode = "SC003", Amount = 2500.00, TransactionDate = DateTime.Now.AddDays(-1), DepositedDate = DateTime.Now.AddDays(-1), PaymentMethod = "CHEQUE", ReferenceNo = "CHQ12347", Remarks = "Share capital payment", ReceiptNo = "REC001" },
                new { MemberNo = "M004", SharesCode = "SC001", Amount = 750.00, TransactionDate = DateTime.Now, DepositedDate = DateTime.Now, PaymentMethod = "MOBILE MONEY", ReferenceNo = "MPESA12348", Remarks = "Mobile payment", ReceiptNo = "" }
            };

            int rowIndex = 2;
            foreach (var example in examples)
            {
                worksheet.Cells[rowIndex, 1].Value = example.MemberNo;
                worksheet.Cells[rowIndex, 2].Value = example.SharesCode;
                worksheet.Cells[rowIndex, 3].Value = example.Amount;
                worksheet.Cells[rowIndex, 4].Value = example.TransactionDate.ToString("yyyy-MM-dd");
                worksheet.Cells[rowIndex, 5].Value = example.DepositedDate.ToString("yyyy-MM-dd");
                worksheet.Cells[rowIndex, 6].Value = example.PaymentMethod;
                worksheet.Cells[rowIndex, 7].Value = example.ReferenceNo;
                worksheet.Cells[rowIndex, 8].Value = example.Remarks;
                worksheet.Cells[rowIndex, 9].Value = example.ReceiptNo;
                rowIndex++;
            }

            // Set column widths
            worksheet.Cells[worksheet.Dimension.Address].AutoFitColumns();

            // Add validation notes at the bottom with detailed instructions
            int notesRow = rowIndex + 2;
            worksheet.Cells[notesRow, 1].Value = "📋 INSTRUCTIONS:";
            worksheet.Cells[notesRow, 1].Style.Font.Bold = true;
            worksheet.Cells[notesRow, 1].Style.Font.Size = 12;

            notesRow++;
            worksheet.Cells[notesRow, 1].Value = "1. Columns marked with * are REQUIRED";
            worksheet.Cells[notesRow, 1].Style.Font.Bold = true;

            notesRow++;
            worksheet.Cells[notesRow, 1].Value = "2. Payment Method options: CASH, CHEQUE, BANK TRANSFER, MOBILE MONEY";

            notesRow++;
            worksheet.Cells[notesRow, 1].Value = "3. TransactionDate and DepositedDate format: YYYY-MM-DD (e.g., 2024-01-15)";

            notesRow++;
            worksheet.Cells[notesRow, 1].Value = "4. ReceiptNo is optional - system will generate if not provided";

            notesRow++;
            worksheet.Cells[notesRow, 1].Value = "5. MemberNo must exist in the system for the specified CompanyCode";

            notesRow++;
            worksheet.Cells[notesRow, 1].Value = "6. SharesCode must be a valid share type for the specified CompanyCode";

            notesRow++;
            worksheet.Cells[notesRow, 1].Value = "7. Amount must be greater than zero and within share type limits";

            notesRow++;
            worksheet.Cells[notesRow, 1].Value = "8. System will auto-generate unique TransactionNo and ReceiptNo";

            // Merge notes cells for better readability
            worksheet.Cells[notesRow - 8, 1, notesRow, 9].Merge = true;
            worksheet.Cells[notesRow - 8, 1, notesRow, 9].Style.WrapText = true;

            await package.SaveAsync();
            return stream.ToArray();
        }
    }
}