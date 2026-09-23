using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using ShelfRow.Core.Models;

namespace ShelfRow.App.Models;

/// <summary>
/// One immutable layout snapshot shared by the header and every realized row.
/// Keeping the eleven slots stable lets Grid retain virtualization while columns move.
/// </summary>
public sealed class LibraryListLayout : INotifyPropertyChanged
{
    private LibraryListColumn[] _order = [];
    private HashSet<LibraryListColumn> _visible = [];
    private IReadOnlyDictionary<LibraryListColumn, double> _logicalWidths = new Dictionary<LibraryListColumn, double>();
    private double _scale = 1;

    public LibraryListLayout(
        IEnumerable<LibraryListColumn> order,
        IEnumerable<LibraryListColumn> visible,
        IReadOnlyDictionary<LibraryListColumn, double>? logicalWidths,
        bool compactDisplay)
    {
        Update(order, visible, logicalWidths, compactDisplay, notify: false);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(
        IEnumerable<LibraryListColumn> order,
        IEnumerable<LibraryListColumn> visible,
        IReadOnlyDictionary<LibraryListColumn, double>? logicalWidths,
        bool compactDisplay,
        bool notify = true)
    {
        _order = LibraryListColumns.DecodeOrder(string.Join(',', order.Select(LibraryListColumns.Id))).ToArray();
        _visible = new HashSet<LibraryListColumn>(visible) { LibraryListColumn.Title };
        _logicalWidths = logicalWidths ?? new Dictionary<LibraryListColumn, double>();
        _scale = compactDisplay ? 0.75 : 1;
        if (notify)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public int UnreadColumn => IndexOf(LibraryListColumn.Unread);
    public int BookTypeColumn => IndexOf(LibraryListColumn.BookType);
    public int TitleColumn => IndexOf(LibraryListColumn.Title);
    public int RatingColumn => IndexOf(LibraryListColumn.Rating);
    public int AuthorColumn => IndexOf(LibraryListColumn.Author);
    public int GenreColumn => IndexOf(LibraryListColumn.Genre);
    public int RelationColumn => IndexOf(LibraryListColumn.Relation);
    public int KeywordAColumn => IndexOf(LibraryListColumn.KeywordA);
    public int KeywordBColumn => IndexOf(LibraryListColumn.KeywordB);
    public int LastReadDateColumn => IndexOf(LibraryListColumn.LastReadDate);
    public int AddedDateColumn => IndexOf(LibraryListColumn.AddedDate);

    public Visibility UnreadVisibility => VisibilityOf(LibraryListColumn.Unread);
    public Visibility BookTypeVisibility => VisibilityOf(LibraryListColumn.BookType);
    public Visibility TitleVisibility => Visibility.Visible;
    public Visibility RatingVisibility => VisibilityOf(LibraryListColumn.Rating);
    public Visibility AuthorVisibility => VisibilityOf(LibraryListColumn.Author);
    public Visibility GenreVisibility => VisibilityOf(LibraryListColumn.Genre);
    public Visibility RelationVisibility => VisibilityOf(LibraryListColumn.Relation);
    public Visibility KeywordAVisibility => VisibilityOf(LibraryListColumn.KeywordA);
    public Visibility KeywordBVisibility => VisibilityOf(LibraryListColumn.KeywordB);
    public Visibility LastReadDateVisibility => VisibilityOf(LibraryListColumn.LastReadDate);
    public Visibility AddedDateVisibility => VisibilityOf(LibraryListColumn.AddedDate);

    public GridLength Slot0Width => WidthAt(0);
    public GridLength Slot1Width => WidthAt(1);
    public GridLength Slot2Width => WidthAt(2);
    public GridLength Slot3Width => WidthAt(3);
    public GridLength Slot4Width => WidthAt(4);
    public GridLength Slot5Width => WidthAt(5);
    public GridLength Slot6Width => WidthAt(6);
    public GridLength Slot7Width => WidthAt(7);
    public GridLength Slot8Width => WidthAt(8);
    public GridLength Slot9Width => WidthAt(9);
    public GridLength Slot10Width => WidthAt(10);

    public bool IsVisible(LibraryListColumn column) => _visible.Contains(column);

    public double LogicalWidth(LibraryListColumn column) =>
        !LibraryListColumns.IsResizable(column)
            ? LibraryListColumns.DefaultWidth(column) ?? LibraryListColumns.MinimumWidth(column)
            : _logicalWidths.TryGetValue(column, out double stored)
            ? LibraryListColumns.ClampWidth(column, stored)
            : LibraryListColumns.DefaultWidth(column) ?? 240;

    private int IndexOf(LibraryListColumn column) => Array.IndexOf(_order, column);
    private Visibility VisibilityOf(LibraryListColumn column) =>
        IsVisible(column) ? Visibility.Visible : Visibility.Collapsed;

    private GridLength WidthAt(int index)
    {
        LibraryListColumn column = _order[index];
        if (!IsVisible(column))
            return new GridLength(0);

        if (!_logicalWidths.ContainsKey(column) && LibraryListColumns.DefaultWidth(column) is null)
            return new GridLength(1, GridUnitType.Star);

        return new GridLength(LogicalWidth(column) * _scale);
    }
}
