using System;
using ShelfRow.Core.Models;
using Xunit;

namespace ShelfRow.Core.Tests;

public class ModelTests
{
    [Fact]
    public void Item_DefaultInitialization_SetsExpectedValues()
    {
        var item = new Item();
        Assert.NotEqual(Guid.Empty, item.Id);
        Assert.True(item.IsUnread);
        Assert.Equal(0, item.Rating);
        Assert.Equal(0, item.CoverVersion);
        Assert.Empty(item.ShelfIds);
    }

    [Fact]
    public void Shelf_SmartShelf_PropertyDetection()
    {
        var manualShelf = new Shelf { Type = 0 };
        var smartShelf = new Shelf { Type = 1, SmartConditionsJson = "{\"field\":\"rating\",\"op\":\">=\",\"value\":4}" };

        Assert.False(manualShelf.IsSmart);
        Assert.True(smartShelf.IsSmart);
        Assert.NotNull(smartShelf.SmartConditionsJson);
    }

    [Fact]
    public void Volume_InitializesCorrectly()
    {
        var vol = new Volume
        {
            Name = "NAS Books",
            LastKnownPath = "/Volumes/Books",
            WindowsMountPath = @"\\NAS\Books"
        };

        Assert.Equal("NAS Books", vol.Name);
        Assert.Equal("/Volumes/Books", vol.LastKnownPath);
        Assert.Equal(@"\\NAS\Books", vol.WindowsMountPath);
    }
}
