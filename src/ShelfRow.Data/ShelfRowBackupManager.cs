using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace ShelfRow.Data;

public sealed class ShelfRowBackupSummary
{
    public int CopiedFiles { get; internal set; }
    public int SkippedFiles { get; internal set; }
    public int RemovedFiles { get; internal set; }
}

/// <summary>
/// Creates the same ShelfRowBackup/manifest.json layout as the macOS app.
/// SQLite itself is snapshotted through SQLite's backup API so committed WAL pages are included.
/// </summary>
public sealed class ShelfRowBackupManager
{
    public const string BackupFolderName = "ShelfRowBackup";
    private const string PendingRestoreFolderName = ".pending-restore";
    private const string ManifestFileName = "manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _applicationSupportDirectory;
    private readonly string _thumbnailDirectory;
    private readonly string _settingsFilePath;
    private readonly string _databasePath;
    private readonly string _pendingRestoreDirectory;

    public ShelfRowBackupManager(
        string applicationSupportDirectory,
        string thumbnailDirectory,
        string settingsFilePath,
        string databasePath)
    {
        _applicationSupportDirectory = Path.GetFullPath(applicationSupportDirectory);
        _thumbnailDirectory = Path.GetFullPath(thumbnailDirectory);
        _settingsFilePath = Path.GetFullPath(settingsFilePath);
        _databasePath = Path.GetFullPath(databasePath);
        _pendingRestoreDirectory = Path.Combine(_applicationSupportDirectory, PendingRestoreFolderName);
    }

    public bool HasPendingRestore => File.Exists(Path.Combine(_pendingRestoreDirectory, ManifestFileName));

    public async Task<ShelfRowBackupSummary> BackUpAsync(
        string selectedFolder,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedFolder);
        string backupRoot = Path.Combine(Path.GetFullPath(selectedFolder), BackupFolderName);
        Directory.CreateDirectory(backupRoot);

        string manifestPath = Path.Combine(backupRoot, ManifestFileName);
        var previousManifest = await ReadManifestAsync(manifestPath, cancellationToken);
        var currentManifest = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
        var summary = new ShelfRowBackupSummary();

        await BackUpDatabaseAsync(backupRoot, previousManifest, currentManifest, summary, cancellationToken);
        await BackUpDirectoryAsync("Thumbnails", _thumbnailDirectory, backupRoot,
            previousManifest, currentManifest, summary, cancellationToken);
        await BackUpFileAsync("Preferences/settings.json", _settingsFilePath, backupRoot,
            previousManifest, currentManifest, summary, cancellationToken);

        foreach (string relativePath in previousManifest.Keys.Where(path => !currentManifest.ContainsKey(path)))
        {
            string stalePath = ResolveContainedPath(backupRoot, relativePath);
            if (File.Exists(stalePath))
            {
                File.Delete(stalePath);
                summary.RemovedFiles++;
            }
        }

        await WriteManifestAsync(manifestPath, currentManifest, cancellationToken);
        RemoveEmptyDirectories(backupRoot);
        return summary;
    }

    /// <summary>
    /// Validates and copies a backup locally. The live database is changed only by
    /// ApplyPendingRestoreAsync, called before the repository opens on the next launch.
    /// </summary>
    public async Task<ShelfRowBackupSummary> StageRestoreAsync(
        string selectedFolder,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedFolder);
        string backupRoot = Path.Combine(Path.GetFullPath(selectedFolder), BackupFolderName);
        string manifestPath = Path.Combine(backupRoot, ManifestFileName);
        var manifest = await ReadManifestAsync(manifestPath, cancellationToken);
        await ValidateRestoreAsync(manifest, backupRoot, cancellationToken);

        string staging = _pendingRestoreDirectory + ".new-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        var summary = new ShelfRowBackupSummary();
        try
        {
            foreach ((string relativePath, ManifestEntry entry) in manifest.OrderBy(pair => pair.Key))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsKnownRestorePath(relativePath))
                    continue;

                string source = ResolveContainedPath(backupRoot, relativePath);
                string destination = ResolveContainedPath(staging, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
                File.SetLastWriteTimeUtc(destination, DateTimeOffset.FromUnixTimeMilliseconds(
                    checked((long)Math.Round(entry.ModificationTime * 1000))).UtcDateTime);
                summary.CopiedFiles++;
            }

            await WriteManifestAsync(Path.Combine(staging, ManifestFileName), manifest, cancellationToken);
            if (Directory.Exists(_pendingRestoreDirectory))
                Directory.Delete(_pendingRestoreDirectory, recursive: true);
            Directory.Move(staging, _pendingRestoreDirectory);
        }
        catch
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
            throw;
        }

        return summary;
    }

    public async Task<ShelfRowBackupSummary?> ApplyPendingRestoreAsync(
        CancellationToken cancellationToken = default)
    {
        string manifestPath = Path.Combine(_pendingRestoreDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
            return null;

        var manifest = await ReadManifestAsync(manifestPath, cancellationToken);
        await ValidateRestoreAsync(manifest, _pendingRestoreDirectory, cancellationToken);
        var summary = new ShelfRowBackupSummary();

        // This method runs before the repository opens. Clearing pools also makes sure a
        // connection disposed during a previous in-process test cannot retain WAL/SHM locks.
        SqliteConnection.ClearAllPools();
        await RemoveFilesMissingFromBackupAsync(manifest, summary, cancellationToken);
        foreach ((string relativePath, ManifestEntry entry) in manifest.OrderBy(pair => pair.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsKnownRestorePath(relativePath))
                continue;

            string source = ResolveContainedPath(_pendingRestoreDirectory, relativePath);
            string destination = GetRestoreDestination(relativePath);
            ReplaceFileAtomically(source, destination, entry.ModificationTime);
            summary.CopiedFiles++;
        }

        Directory.Delete(_pendingRestoreDirectory, recursive: true);
        return summary;
    }

    private async Task BackUpDatabaseAsync(
        string backupRoot,
        IReadOnlyDictionary<string, ManifestEntry> previousManifest,
        IDictionary<string, ManifestEntry> currentManifest,
        ShelfRowBackupSummary summary,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_databasePath))
            return;

        const string relativePath = "ApplicationSupport/shelfrow.db";
        string tempPath = Path.Combine(Path.GetTempPath(), $"shelfrow-backup-{Guid.NewGuid():N}.db");
        try
        {
            await using var source = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            await source.OpenAsync(cancellationToken);
            await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = tempPath,
                Pooling = false
            }.ToString());
            await destination.OpenAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            source.BackupDatabase(destination);
            await destination.CloseAsync();

            DateTime modificationTime = GetDatabaseModificationTimeUtc();
            File.SetLastWriteTimeUtc(tempPath, modificationTime);
            await CopyWithManifestAsync(tempPath, relativePath, backupRoot, previousManifest,
                currentManifest, summary, cancellationToken);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private DateTime GetDatabaseModificationTimeUtc()
    {
        DateTime latest = File.GetLastWriteTimeUtc(_databasePath);
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
        {
            string auxiliary = _databasePath + suffix;
            if (File.Exists(auxiliary) && File.GetLastWriteTimeUtc(auxiliary) > latest)
                latest = File.GetLastWriteTimeUtc(auxiliary);
        }
        return latest;
    }

    private async Task BackUpDirectoryAsync(
        string sourceName,
        string sourceDirectory,
        string backupRoot,
        IReadOnlyDictionary<string, ManifestEntry> previousManifest,
        IDictionary<string, ManifestEntry> currentManifest,
        ShelfRowBackupSummary summary,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourceDirectory)) return;
        foreach (string file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSqliteAuxiliaryFile(file)) continue;
            string insideSource = Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/');
            string relativePath = sourceName + "/" + insideSource;
            try
            {
                await CopyWithManifestAsync(file, relativePath, backupRoot,
                    previousManifest, currentManifest, summary, cancellationToken);
            }
            catch (IOException)
            {
                PreservePreviousEntry(relativePath, backupRoot, previousManifest, currentManifest);
                summary.SkippedFiles++;
            }
            catch (UnauthorizedAccessException)
            {
                PreservePreviousEntry(relativePath, backupRoot, previousManifest, currentManifest);
                summary.SkippedFiles++;
            }
        }
    }

    private async Task BackUpFileAsync(
        string relativePath,
        string sourcePath,
        string backupRoot,
        IReadOnlyDictionary<string, ManifestEntry> previousManifest,
        IDictionary<string, ManifestEntry> currentManifest,
        ShelfRowBackupSummary summary,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath)) return;
        await CopyWithManifestAsync(sourcePath, relativePath, backupRoot,
            previousManifest, currentManifest, summary, cancellationToken);
    }

    private static async Task CopyWithManifestAsync(
        string sourcePath,
        string relativePath,
        string backupRoot,
        IReadOnlyDictionary<string, ManifestEntry> previousManifest,
        IDictionary<string, ManifestEntry> currentManifest,
        ShelfRowBackupSummary summary,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(sourcePath);
        var entry = new ManifestEntry(info.Length, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds() / 1000d);
        currentManifest[relativePath] = entry;
        string destination = ResolveContainedPath(backupRoot, relativePath);

        if (previousManifest.TryGetValue(relativePath, out ManifestEntry? previous)
            && previous.Matches(entry) && File.Exists(destination))
        {
            summary.SkippedFiles++;
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                             1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await input.CopyToAsync(output, cancellationToken);
            File.SetLastWriteTimeUtc(temporary, info.LastWriteTimeUtc);
            File.Move(temporary, destination, overwrite: true);
            summary.CopiedFiles++;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task RemoveFilesMissingFromBackupAsync(
        IReadOnlyDictionary<string, ManifestEntry> manifest,
        ShelfRowBackupSummary summary,
        CancellationToken cancellationToken)
    {
        foreach ((string sourceName, string directory) in new[]
                 {
                     ("ApplicationSupport", _applicationSupportDirectory),
                     ("Thumbnails", _thumbnailDirectory)
                 })
        {
            if (!Directory.Exists(directory)) continue;
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsSqliteAuxiliaryFile(file))
                {
                    if (sourceName == "ApplicationSupport"
                        && Path.GetFullPath(file).StartsWith(_databasePath + "-", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(file);
                        summary.RemovedFiles++;
                    }
                    continue;
                }
                if (sourceName == "ApplicationSupport" && !Path.GetFullPath(file).Equals(_databasePath, StringComparison.OrdinalIgnoreCase))
                    continue;
                string relative = sourceName + "/" + Path.GetRelativePath(directory, file).Replace('\\', '/');
                if (!manifest.ContainsKey(relative))
                {
                    File.Delete(file);
                    summary.RemovedFiles++;
                }
            }
        }

        const string settingsRelative = "Preferences/settings.json";
        if (!manifest.ContainsKey(settingsRelative) && File.Exists(_settingsFilePath))
        {
            File.Delete(_settingsFilePath);
            summary.RemovedFiles++;
        }
        await Task.CompletedTask;
    }

    private static async Task ValidateRestoreAsync(
        IReadOnlyDictionary<string, ManifestEntry> manifest,
        string backupRoot,
        CancellationToken cancellationToken)
    {
        if (manifest.Count == 0)
            throw new InvalidDataException("バックアップのmanifest.jsonが見つからないか、空です。");
        if (!manifest.ContainsKey("ApplicationSupport/shelfrow.db"))
            throw new InvalidDataException("このバックアップにはWindows版の蔵書データベースがありません。");

        foreach (string relativePath in manifest.Keys.Where(IsKnownRestorePath))
        {
            string file = ResolveContainedPath(backupRoot, relativePath);
            if (!File.Exists(file))
                throw new InvalidDataException($"バックアップファイルが見つかりません: {relativePath}");
            if (new FileInfo(file).Length != manifest[relativePath].Size)
                throw new InvalidDataException($"バックアップファイルのサイズがmanifestと一致しません: {relativePath}");
        }

        string database = ResolveContainedPath(backupRoot, "ApplicationSupport/shelfrow.db");
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA quick_check;";
        string? result = (string?)await check.ExecuteScalarAsync(cancellationToken);
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"バックアップの蔵書データベースが破損しています: {result ?? "検査結果なし"}");

        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Items';";
        if (Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken)) != 1)
            throw new InvalidDataException("バックアップの蔵書データベース形式を認識できません。");
    }

    private static bool IsKnownRestorePath(string relativePath) =>
        relativePath.Equals("ApplicationSupport/shelfrow.db", StringComparison.Ordinal)
        || relativePath.Equals("Preferences/settings.json", StringComparison.Ordinal)
        || relativePath.StartsWith("Thumbnails/", StringComparison.Ordinal);

    private string GetRestoreDestination(string relativePath)
    {
        if (relativePath == "ApplicationSupport/shelfrow.db") return _databasePath;
        if (relativePath == "Preferences/settings.json") return _settingsFilePath;
        if (relativePath.StartsWith("Thumbnails/", StringComparison.Ordinal))
            return ResolveContainedPath(_thumbnailDirectory, relativePath["Thumbnails/".Length..]);
        throw new InvalidDataException($"不明なバックアップ項目です: {relativePath}");
    }

    private static bool IsSqliteAuxiliaryFile(string path) =>
        path.EndsWith("-wal", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("-journal", StringComparison.OrdinalIgnoreCase);

    private static void PreservePreviousEntry(
        string relativePath,
        string backupRoot,
        IReadOnlyDictionary<string, ManifestEntry> previousManifest,
        IDictionary<string, ManifestEntry> currentManifest)
    {
        if (previousManifest.TryGetValue(relativePath, out ManifestEntry? previous)
            && File.Exists(ResolveContainedPath(backupRoot, relativePath)))
            currentManifest[relativePath] = previous;
    }

    private static void ReplaceFileAtomically(string source, string destination, double modificationTime)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".restore-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, temporary, overwrite: false);
            File.SetLastWriteTimeUtc(temporary, DateTimeOffset.FromUnixTimeMilliseconds(
                checked((long)Math.Round(modificationTime * 1000))).UtcDateTime);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(Path.Combine(fullRoot, normalized));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"バックアップ内の不正なパスです: {relativePath}");
        return fullPath;
    }

    private static async Task<Dictionary<string, ManifestEntry>> ReadManifestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return new(StringComparer.Ordinal);
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, ManifestEntry>>(stream, JsonOptions, cancellationToken)
               ?? new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
    }

    private static async Task WriteManifestAsync(
        string path,
        IReadOnlyDictionary<string, ManifestEntry> manifest,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(
                    stream,
                    new SortedDictionary<string, ManifestEntry>(
                        manifest.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                        StringComparer.Ordinal),
                    JsonOptions,
                    cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void RemoveEmptyDirectories(string root)
    {
        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }

    private sealed class ManifestEntry
    {
        public ManifestEntry() { }
        public ManifestEntry(long size, double modificationTime)
        {
            Size = size;
            ModificationTime = modificationTime;
        }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("modificationTime")]
        public double ModificationTime { get; set; }

        public bool Matches(ManifestEntry other) =>
            Size == other.Size && ModificationTime.Equals(other.ModificationTime);
    }
}
