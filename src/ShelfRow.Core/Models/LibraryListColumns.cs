using System;
using System.Collections.Generic;
using System.Linq;

namespace ShelfRow.Core.Models;

/// <summary>
/// Stable identifiers for the columns shared by the list header and its rows.
/// The string representation intentionally matches the macOS persisted values.
/// </summary>
public enum LibraryListColumn
{
    Unread,
    BookType,
    Title,
    Rating,
    Author,
    Genre,
    Relation,
    KeywordA,
    KeywordB,
    LastReadDate,
    AddedDate
}

public static class LibraryListColumns
{
    public const double MaximumWidth = 1200;

    public static readonly IReadOnlyList<LibraryListColumn> Canonical =
    [
        LibraryListColumn.Unread,
        LibraryListColumn.BookType,
        LibraryListColumn.Title,
        LibraryListColumn.Rating,
        LibraryListColumn.Author,
        LibraryListColumn.Genre,
        LibraryListColumn.Relation,
        LibraryListColumn.KeywordA,
        LibraryListColumn.KeywordB,
        LibraryListColumn.LastReadDate,
        LibraryListColumn.AddedDate
    ];

    public static readonly IReadOnlyList<LibraryListColumn> Toggleable =
        Canonical.Where(column => column != LibraryListColumn.Title).ToArray();

    public static string Id(LibraryListColumn column) => column switch
    {
        LibraryListColumn.Unread => "unread",
        LibraryListColumn.BookType => "bookType",
        LibraryListColumn.Title => "title",
        LibraryListColumn.Rating => "rating",
        LibraryListColumn.Author => "author",
        LibraryListColumn.Genre => "genre",
        LibraryListColumn.Relation => "relation",
        LibraryListColumn.KeywordA => "keywordA",
        LibraryListColumn.KeywordB => "keywordB",
        LibraryListColumn.LastReadDate => "lastReadDate",
        LibraryListColumn.AddedDate => "addedDate",
        _ => throw new ArgumentOutOfRangeException(nameof(column))
    };

    public static bool TryParse(string? value, out LibraryListColumn column)
    {
        foreach (LibraryListColumn candidate in Canonical)
        {
            if (string.Equals(Id(candidate), value?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                column = candidate;
                return true;
            }
        }

        column = default;
        return false;
    }

    /// <summary>Repairs duplicate, unknown, and missing entries while preserving known order.</summary>
    public static IReadOnlyList<LibraryListColumn> DecodeOrder(string? rawValue)
    {
        var result = new List<LibraryListColumn>(Canonical.Count);
        var seen = new HashSet<LibraryListColumn>();
        foreach (string value in (rawValue ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParse(value, out LibraryListColumn column) && seen.Add(column))
                result.Add(column);
        }

        result.AddRange(Canonical.Where(seen.Add));
        return result;
    }

    public static string EncodeOrder(IEnumerable<LibraryListColumn> order) =>
        string.Join(',', DecodeOrder(string.Join(',', order.Select(Id))).Select(Id));

    public static IReadOnlyList<LibraryListColumn> Move(
        LibraryListColumn source,
        LibraryListColumn target,
        IEnumerable<LibraryListColumn> order)
    {
        var result = DecodeOrder(string.Join(',', order.Select(Id))).ToList();
        int sourceIndex = result.IndexOf(source);
        int targetIndex = result.IndexOf(target);
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
            return result;

        result.RemoveAt(sourceIndex);
        result.Insert(targetIndex, source);
        return result;
    }

    public static bool IsResizable(LibraryListColumn column) => column is not
        (LibraryListColumn.Unread or LibraryListColumn.BookType or LibraryListColumn.Rating
        or LibraryListColumn.LastReadDate or LibraryListColumn.AddedDate);

    public static double? DefaultWidth(LibraryListColumn column) => column switch
    {
        LibraryListColumn.Unread or LibraryListColumn.BookType => 44,
        LibraryListColumn.Title => null,
        // WinUI's standard RatingControl needs about 24 px per star. Unlike the
        // surrounding text it does not shrink with our compact layout scale.
        LibraryListColumn.Rating => 120,
        LibraryListColumn.Author => 120,
        LibraryListColumn.Genre or LibraryListColumn.Relation => 90,
        LibraryListColumn.KeywordA or LibraryListColumn.KeywordB => 100,
        LibraryListColumn.LastReadDate or LibraryListColumn.AddedDate => 96,
        _ => null
    };

    public static double MinimumWidth(LibraryListColumn column) => column switch
    {
        LibraryListColumn.Unread or LibraryListColumn.BookType => 28,
        LibraryListColumn.Rating => 72,
        LibraryListColumn.LastReadDate or LibraryListColumn.AddedDate => 68,
        _ => 60
    };

    public static double ClampWidth(LibraryListColumn column, double width) =>
        double.IsFinite(width) ? Math.Clamp(width, MinimumWidth(column), MaximumWidth) : MinimumWidth(column);
}
