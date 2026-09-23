using ShelfRow.Core.Models;

namespace ShelfRow.Core.Tests;

public class LibraryListColumnsTests
{
    [Fact]
    public void DecodeOrder_RepairsDuplicatesUnknownAndMissingColumns()
    {
        var order = LibraryListColumns.DecodeOrder("rating,title,rating,unknown");

        Assert.Equal(LibraryListColumn.Rating, order[0]);
        Assert.Equal(LibraryListColumn.Title, order[1]);
        Assert.Equal(LibraryListColumns.Canonical.Count, order.Count);
        Assert.Equal(order.Count, order.Distinct().Count());
    }

    [Fact]
    public void Move_PreservesCompleteOrderIncludingHiddenColumns()
    {
        var moved = LibraryListColumns.Move(
            LibraryListColumn.Author,
            LibraryListColumn.BookType,
            LibraryListColumns.Canonical);

        Assert.Equal(LibraryListColumn.Author, moved[1]);
        Assert.Equal(LibraryListColumn.BookType, moved[2]);
        Assert.Equal(LibraryListColumns.Canonical.Count, moved.Count);
    }

    [Theory]
    [InlineData(10, 60)]
    [InlineData(500, 500)]
    [InlineData(5000, 1200)]
    [InlineData(double.NaN, 60)]
    public void ClampWidth_UsesMacCompatibleLimits(double input, double expected)
    {
        Assert.Equal(expected, LibraryListColumns.ClampWidth(LibraryListColumn.Author, input));
    }
}
