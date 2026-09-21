using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ShelfRow.Core.Models;
using ShelfRow.Storage;

namespace ShelfRow.App.Services;

/// <summary>
/// Opens a book in an external application. ShelfRow has no viewer of its own, on
/// either platform.
/// </summary>
public class BookLauncher
{
    private readonly AppSettingsService _settingsService;
    private readonly VolumePathResolver _pathResolver;

    public BookLauncher(AppSettingsService settingsService, VolumePathResolver pathResolver)
    {
        _settingsService = settingsService;
        _pathResolver = pathResolver;
    }

    public record LaunchResult(bool Opened, string? Error);

    /// <summary>
    /// Picks an application the way the Mac app does: a helper registered for the
    /// extension, then the ZIP helper, then the slideshow helper for a folder of images,
    /// and failing all of those whatever Windows associates with the file.
    /// </summary>
    public LaunchResult Open(Item item, Volume? volume)
    {
        string path = _pathResolver.ResolveToWindowsPath(item, volume);

        if (string.IsNullOrWhiteSpace(path))
            return new LaunchResult(false, "このアイテムのファイルパスを解決できませんでした。");

        bool isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path))
            return new LaunchResult(false, $"ファイルが見つかりません:\n{path}");

        var settings = _settingsService.Current;
        string extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

        if (extension.Length > 0 && FindHelperFor(extension, settings.HelperMappings) is { } helper)
            return Start(helper, path);

        if (extension == "zip")
        {
            return string.IsNullOrWhiteSpace(settings.ZipHelperPath)
                ? new LaunchResult(false, "Zip アーカイブを開くための外部ビューアが設定されていません。\n\n設定 > ヘルパー で「Zip アーカイブヘルパー」を指定してください。")
                : Start(settings.ZipHelperPath, path);
        }

        if (isDirectory)
        {
            return string.IsNullOrWhiteSpace(settings.SlideshowHelperPath)
                ? new LaunchResult(false, "画像フォルダを開くための外部ビューアが設定されていません。\n\n設定 > ヘルパー で「スライドショーヘルパー」を指定してください。")
                : Start(settings.SlideshowHelperPath, path);
        }

        return StartWithSystemDefault(path);
    }

    private static string? FindHelperFor(string extension, List<HelperMapping> mappings)
    {
        foreach (var mapping in mappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.ApplicationPath))
                continue;

            bool matches = mapping.Extensions
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(e => e.TrimStart('.').Equals(extension, StringComparison.OrdinalIgnoreCase));

            if (matches)
                return mapping.ApplicationPath;
        }

        return null;
    }

    private static LaunchResult Start(string applicationPath, string filePath)
    {
        if (!File.Exists(applicationPath))
            return new LaunchResult(false, $"ヘルパーアプリが見つかりません:\n{applicationPath}");

        try
        {
            Process.Start(new ProcessStartInfo(applicationPath)
            {
                // Passed as an argument rather than in a command string, so a path with
                // spaces or quotes cannot turn into extra arguments.
                ArgumentList = { filePath },
                UseShellExecute = false
            });
            return new LaunchResult(true, null);
        }
        catch (Exception ex)
        {
            return new LaunchResult(false, $"ヘルパーアプリを起動できませんでした: {ex.Message}");
        }
    }

    private static LaunchResult StartWithSystemDefault(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return new LaunchResult(true, null);
        }
        catch (Exception ex)
        {
            return new LaunchResult(false, $"「{Path.GetFileName(path)}」を開けませんでした: {ex.Message}");
        }
    }
}
