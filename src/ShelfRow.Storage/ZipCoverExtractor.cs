using System.IO.Compression;

namespace ShelfRow.Storage;

/// <summary>Extracts the first plausible cover image from a ZIP/CBZ archive.</summary>
public sealed class ZipCoverExtractor
{
    private const long MaxImageBytes = 64L * 1024 * 1024;
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif"
    };

    public async Task<bool> ExtractFirstImageAsync(
        string archivePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var entries = await ListImagesAsync(archivePath, cancellationToken);
        if (entries.Count == 0)
            return false;

        return await ExtractImageAsync(archivePath, entries[0], destinationPath, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListImagesAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(archivePath)) return Array.Empty<string>();
        await using var archiveStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        cancellationToken.ThrowIfCancellationRequested();
        return archive.Entries.Where(IsImageEntry)
            .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.FullName).ToArray();
    }

    public async Task<byte[]?> ReadImageAsync(string archivePath, string entryName, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(archivePath)) return null;
        await using var archiveStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        var entry = archive.GetEntry(entryName);
        if (entry is null || !IsImageEntry(entry)) return null;
        await using var source = entry.Open();
        using var memory = new MemoryStream((int)entry.Length);
        await source.CopyToAsync(memory, cancellationToken);
        return memory.ToArray();
    }

    public async Task<bool> ExtractImageAsync(
        string archivePath,
        string entryName,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        byte[]? bytes = await ReadImageAsync(archivePath, entryName, cancellationToken);
        if (bytes is null) return false;

        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        string tempPath = destinationPath + $".{Environment.ProcessId}.tmp";
        try
        {
            await using (var destination = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, useAsync: true))
            {
                await destination.WriteAsync(bytes, cancellationToken);
            }
            File.Move(tempPath, destinationPath, overwrite: true);
            return true;
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
            throw;
        }
    }

    private static bool IsImageEntry(ZipArchiveEntry candidate) =>
        candidate.Length > 0
        && candidate.Length <= MaxImageBytes
        && !candidate.FullName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)
        && ImageExtensions.Contains(Path.GetExtension(candidate.Name));
}
