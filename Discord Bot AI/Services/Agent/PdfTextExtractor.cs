using System.Text;
using Serilog;
using UglyToad.PdfPig;

namespace Discord_Bot_AI.Services.Agent;

/// <summary>
/// Extracts embedded text from PDF documents using PdfPig library.
/// </summary>
public class PdfTextExtractor : IPdfParser
{
    private const int MaxPages = 100;
    private const int MaxTextLength = 50_000;
    
    public Task<string> ExtractTextAsync(Stream pdfStream, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var document = PdfDocument.Open(pdfStream);
            var sb = new StringBuilder();
            int pageCount = Math.Min(document.NumberOfPages, MaxPages);

            for (int i = 1; i <= pageCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var page = document.GetPage(i);
                var text = page.Text;

                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.AppendLine($"--- Page {i} ---");
                    sb.AppendLine(text);
                    sb.AppendLine();
                }

                if (sb.Length >= MaxTextLength)
                {
                    sb.Length = MaxTextLength;
                    sb.AppendLine("\n[... truncated due to length limit ...]");
                    break;
                }
            }

            var result = sb.ToString().Trim();

            if (string.IsNullOrWhiteSpace(result))
            {
                Log.Warning("PDF text extraction returned empty result - document may be image-only (no OCR support)");
                return Task.FromResult(string.Empty);
            }

            Log.Debug("Extracted {Length} characters from {Pages} PDF pages", result.Length, pageCount);
            return Task.FromResult(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to extract text from PDF document");
            return Task.FromResult(string.Empty);
        }
    }
}
