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
        if (!File.Exists(archivePath))
            return false;

        await using var archiveStream = new FileStream(
            archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
        var entry = archive.Entries
            .Where(candidate => candidate.Length > 0
                                && candidate.Length <= MaxImageBytes
                                && !candidate.FullName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)
                                && ImageExtensions.Contains(Path.GetExtension(candidate.Name)))
            .OrderBy(candidate => candidate.FullName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (entry is null)
            return false;

        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        string tempPath = destinationPath + $".{Environment.ProcessId}.tmp";
        try
        {
            await using var source = entry.Open();
            await using (var destination = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, useAsync: true))
            {
                await source.CopyToAsync(destination, cancellationToken);
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
}

