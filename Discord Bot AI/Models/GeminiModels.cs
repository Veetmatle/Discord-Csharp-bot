namespace Discord_Bot_AI.Models;

public class GeminiRequest
{
    public string Prompt { get; set; } = string.Empty;
    public List<GeminiAttachment> Attachments { get; set; } = new();
}

public class GeminiAttachment
{
    public string Url { get; set; } = string.Empty;
    public string MimeType { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long Size { get; set; }
}

public static class GeminiSupportedTypes
{
    /// <summary>
    /// Image MIME types supported by Gemini.
    /// </summary>
    public static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/jpg", 
        "image/gif",
        "image/webp"
    };
    
    /// <summary>
    /// Document MIME types supported by Gemini.
    /// </summary>
    public static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "text/plain",
        "text/csv",
        "text/html",
        "text/markdown",
        "application/json"
    };
    
    /// <summary>
    /// Office document types - these will be processed as binary.
    /// </summary>
    public static readonly HashSet<string> OfficeDocuments = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document", // .docx
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",       // .xlsx
        "application/vnd.openxmlformats-officedocument.presentationml.presentation" // .pptx
    };
    
    public const long MaxFileSizeBytes = 20 * 1024 * 1024;
    
    public static bool IsSupported(string mimeType)
    {
        return Images.Contains(mimeType) || 
               Documents.Contains(mimeType) || 
               OfficeDocuments.Contains(mimeType);
    }
}
