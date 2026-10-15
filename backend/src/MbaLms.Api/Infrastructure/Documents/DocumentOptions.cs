namespace MbaLms.Api.Infrastructure.Documents;

public class DocumentOptions
{
    public const string Section = "Documents";

    /// <summary>
    /// Directory for uploaded documents. Must be persistent (a Docker volume in production).
    /// Empty: <c>App_Data/documents</c> under the content root (local runs only; ignored by Git).
    /// </summary>
    public string? StoragePath { get; set; }

    /// <summary>Upper limit for one discipline programme PDF.</summary>
    public long MaxProgramPdfBytes { get; set; } = 20 * 1024 * 1024;
}
