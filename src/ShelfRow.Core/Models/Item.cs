using System;
using System.Collections.Generic;

namespace ShelfRow.Core.Models;

/// <summary>
/// Represents a single book in the library.
/// Modeled after ShelfRow for macOS (Item.swift) and CloudKit CD_Item.
/// </summary>
public class Item
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Legacy Stackroom Book ID, if imported from Stackroom Library.xml.
    /// </summary>
    public int? LegacyId { get; set; }

    /// <summary>
    /// ID of the volume (NAS or external drive) where the file resides.
    /// </summary>
    public Guid? VolumeId { get; set; }

    /// <summary>
    /// Relative path from the Volume root.
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    public int Rating { get; set; } = 0;

    public bool IsUnread { get; set; } = true;

    public string Genre { get; set; } = string.Empty;

    public string Relation { get; set; } = string.Empty;

    public string KeywordA { get; set; } = string.Empty;

    public string KeywordB { get; set; } = string.Empty;

    public string Memo { get; set; } = string.Empty;

    public string CoverImageName { get; set; } = string.Empty;

    public string CoverImagePath { get; set; } = string.Empty;

    public DateTime AddedDate { get; set; } = DateTime.UtcNow;

    public DateTime? LastReadDate { get; set; }

    public int Pages { get; set; } = 0;

    public int BookType { get; set; } = 0;

    public int FileType { get; set; } = 0;

    /// <summary>
    /// Generation version of the cover thumbnail.
    /// Incremented whenever a new thumbnail is extracted/chosen.
    /// </summary>
    public int CoverVersion { get; set; } = 0;

    /// <summary>
    /// File size in bytes of the thumbnail for pre-calculating download size.
    /// </summary>
    public long CoverBytes { get; set; } = 0;

    /// <summary>
    /// Associated shelves.
    /// </summary>
    public List<Guid> ShelfIds { get; set; } = new();
}
