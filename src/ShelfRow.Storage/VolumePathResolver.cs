using System;
using System.IO;
using System.Text.RegularExpressions;
using ShelfRow.Core.Models;

namespace ShelfRow.Storage;

public class VolumePathResolver
{
    /// <summary>
    /// Splits a POSIX path into (volumePath, volumeName, relativePath).
    /// Modeled after PathParser.split in ShelfRow macOS.
    /// </summary>
    public static (string VolumePath, string VolumeName, string RelativePath) SplitPosixPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ("/", "Boot Volume", string.Empty);

        string normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("/Volumes/"))
        {
            string[] components = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (components.Length >= 2 && components[0] == "Volumes")
            {
                string volumeName = components[1];
                string rel = components.Length > 2 ? string.Join('/', components[2..]) : string.Empty;
                return ($"/Volumes/{volumeName}", volumeName, rel);
            }
        }

        string cleaned = normalized.TrimStart('/');
        return ("/", "Boot Volume", cleaned);
    }

    /// <summary>
    /// Converts a POSIX path or relative path to a Windows accessible path based on the Volume configuration.
    /// </summary>
    public string ResolveToWindowsPath(Item item, Volume? volume)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));

        // 1. If Volume has a WindowsMountPath configured (e.g. \\NAS\Books or Z:\)
        if (volume != null && !string.IsNullOrWhiteSpace(volume.WindowsMountPath))
        {
            string relative = item.RelativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            return Path.Combine(volume.WindowsMountPath, relative);
        }

        // 2. If item.RelativePath is already a rooted Windows path
        if (Path.IsPathRooted(item.RelativePath) && (item.RelativePath.Contains(':') || item.RelativePath.StartsWith(@"\\")))
        {
            return item.RelativePath;
        }

        // 3. If volume has a LastKnownPath (e.g. /Volumes/Media/Books), attempt heuristic conversion
        if (volume != null && !string.IsNullOrWhiteSpace(volume.LastKnownPath))
        {
            string winPath = ConvertPosixToWindows(volume.LastKnownPath);
            string relative = item.RelativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            return Path.Combine(winPath, relative);
        }

        // 4. Default fallback: replace slashes
        return item.RelativePath.Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// Converts /Volumes/ShareName/... to \\ShareName\... or standard Windows UNC path.
    /// </summary>
    public static string ConvertPosixToWindows(string posixPath)
    {
        if (string.IsNullOrWhiteSpace(posixPath)) return string.Empty;

        string normalized = posixPath.Replace('\\', '/');
        if (normalized.StartsWith("/Volumes/", StringComparison.OrdinalIgnoreCase))
        {
            string sharePart = normalized.Substring("/Volumes/".Length);
            return @"\\" + sharePart.Replace('/', '\\');
        }

        return normalized.Replace('/', '\\');
    }

    /// <summary>
    /// Converts a Windows UNC or drive path to a POSIX path (/Volumes/ShareName/...)
    /// for interoperability with macOS.
    /// </summary>
    public static string ConvertWindowsToPosix(string windowsPath)
    {
        if (string.IsNullOrWhiteSpace(windowsPath)) return string.Empty;

        string normalized = windowsPath.Replace('/', '\\');
        if (normalized.StartsWith(@"\\"))
        {
            string sharePart = normalized.TrimStart('\\');
            return "/Volumes/" + sharePart.Replace('\\', '/');
        }

        Match match = Regex.Match(normalized, @"^([a-zA-Z]):\\(.*)$");
        if (match.Success)
        {
            string drive = match.Groups[1].Value;
            string rest = match.Groups[2].Value.Replace('\\', '/');
            return $"/Volumes/{drive}/{rest}";
        }

        return normalized.Replace('\\', '/');
    }
}
