using Microsoft.Extensions.Options;

namespace MbaLms.Api.Infrastructure.Documents;

/// <summary>
/// Stores document bytes on disk under server-generated keys. File names from users never reach the file system,
/// and the directory is not served statically: documents are returned only by access-checked endpoints.
/// </summary>
public sealed class DocumentStorage
{
    private readonly string _root;
    private readonly ILogger<DocumentStorage> _logger;

    public DocumentStorage(IOptions<DocumentOptions> options, IHostEnvironment env, ILogger<DocumentStorage> logger)
    {
        var configured = options.Value.StoragePath;
        _root = Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(env.ContentRootPath, "App_Data", "documents")
            : configured);
        Directory.CreateDirectory(_root);
        _logger = logger;
    }

    /// <summary>Writes the content to a new key. The file appears under its final name only when complete.</summary>
    public async Task<Guid> SaveAsync(Stream content, CancellationToken ct)
    {
        var key = Guid.NewGuid();
        var final = PathOf(key);
        var temp = final + ".tmp";
        try
        {
            await using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await content.CopyToAsync(target, ct);
            File.Move(temp, final);
            return key;
        }
        catch
        {
            TryDeleteFile(temp);
            throw;
        }
    }

    /// <returns>A read stream, or null when the file is missing.</returns>
    public Stream? OpenRead(Guid key)
    {
        var path = PathOf(key);
        return File.Exists(path)
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)
            : null;
    }

    public bool Exists(Guid key) => File.Exists(PathOf(key));

    /// <summary>Best-effort removal; a failure is logged by key only, never with content.</summary>
    public void Delete(Guid key) => TryDeleteFile(PathOf(key));

    private string PathOf(Guid key) => Path.Combine(_root, key.ToString("N") + ".pdf");

    private void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not delete stored document {File}: {Error}", Path.GetFileName(path), e.GetType().Name);
        }
    }
}
