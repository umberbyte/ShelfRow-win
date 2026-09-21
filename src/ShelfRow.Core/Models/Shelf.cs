using System;
using System.Collections.Generic;

namespace ShelfRow.Core.Models;

/// <summary>
/// Modeled after ShelfRow for macOS (Shelf.swift) and CloudKit CD_Shelf.
/// </summary>
public class Shelf
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title { get; set; } = string.Empty;

    public int Icon { get; set; } = 0;

    /// <summary>
    /// 0 = standard manual shelf, 1 = smart shelf
    /// </summary>
    public int Type { get; set; } = 0;

    public int SortOrder { get; set; } = 0;

    public bool SortAscending { get; set; } = true;

    public string SortKey { get; set; } = "title";

    public string? SmartConditionsJson { get; set; }

    public List<Guid> ItemIds { get; set; } = new();

    public bool IsSmart => Type == 1;
}
