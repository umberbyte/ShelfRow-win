using System;

namespace ShelfRow.Core.Models;

/// <summary>
/// Records the status of cover extraction for a book item.
/// Modeled after ShelfRow for macOS (CoverExtractionRecord.swift) and CloudKit CD_CoverExtractionRecord.
/// </summary>
public class CoverExtractionRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ItemId { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public string Status { get; set; } = "pending";
}
