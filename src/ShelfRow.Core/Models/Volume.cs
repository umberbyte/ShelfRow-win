using System;

namespace ShelfRow.Core.Models;

/// <summary>
/// Represents a mount point holding books (NAS share or external drive).
/// Modeled after ShelfRow for macOS (Volume.swift) and CloudKit CD_Volume.
/// </summary>
public class Volume
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Synced path hint (usually macOS POSIX mount path e.g. /Volumes/Books)
    /// </summary>
    public string LastKnownPath { get; set; } = string.Empty;

    /// <summary>
    /// Windows-specific local mount path (UNC e.g. \\NAS\Books or drive letter Z:\)
    /// </summary>
    public string? WindowsMountPath { get; set; }

    /// <summary>CloudKit record name; see <see cref="Item.CloudKitRecordName"/>.</summary>
    public string? CloudKitRecordName { get; set; }

    public string? CloudKitChangeTag { get; set; }
}
