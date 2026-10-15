using System.Text;

namespace MbaLms.Api.Infrastructure.Documents;

public static class PdfFile
{
    public const string ContentType = "application/pdf";

    private const int HeaderWindow = 1024;
    private const int TrailerWindow = 2048;
    private static readonly byte[] Header = "%PDF-"u8.ToArray();
    private static readonly byte[] Trailer = "%%EOF"u8.ToArray();

    /// <summary>
    /// Cheap structural check: a PDF header near the start and an end-of-file marker near the end.
    /// It rejects renamed images, Office files and text; it does not prove the document renders.
    /// </summary>
    public static async Task<bool> LooksLikePdfAsync(Stream stream, CancellationToken ct)
    {
        if (!stream.CanSeek || stream.Length < Header.Length + Trailer.Length) return false;

        stream.Position = 0;
        var head = new byte[(int)Math.Min(HeaderWindow, stream.Length)];
        await stream.ReadExactlyAsync(head, ct);
        var headerAt = head.AsSpan().IndexOf(Header);
        if (headerAt < 0 || headerAt + Header.Length + 3 > head.Length) return false;
        // "%PDF-1.7", "%PDF-2.0"
        if (!char.IsAsciiDigit((char)head[headerAt + 5]) || head[headerAt + 6] != (byte)'.') return false;

        var tailLength = (int)Math.Min(TrailerWindow, stream.Length);
        stream.Position = stream.Length - tailLength;
        var tail = new byte[tailLength];
        await stream.ReadExactlyAsync(tail, ct);
        stream.Position = 0;
        return tail.AsSpan().LastIndexOf(Trailer) >= 0;
    }

    /// <summary>Display name only: no directories or control characters, at most 255 characters, ends with .pdf.</summary>
    public static string DisplayName(string? original)
    {
        var name = Path.GetFileName((original ?? string.Empty).Replace('\\', '/').Split('/').Last());
        var clean = new StringBuilder(name.Length);
        foreach (var c in name)
            if (!char.IsControl(c) && c != '"') clean.Append(c);
        var result = clean.ToString().Trim();
        if (!result.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) result += ".pdf";
        if (result.Length <= 4) result = "program.pdf";
        if (result.Length > 255) result = result[..251] + ".pdf";
        return result;
    }
}
