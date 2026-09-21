using ShelfRow.Core.Models;
using Xunit;

namespace ShelfRow.Core.Tests;

public class SmartConditionsTests
{
    [Fact]
    public void EncodeDecode_RoundTripsEditableConditions()
    {
        var original = new SmartConditions
        {
            Keyword = new() { Field = "Genre", Text = "SF", Mode = 2 },
            Date = new() { Field = 1, Days = 14, Mode = 1 },
            Types = new() { 0, 3 },
            Rates = new() { 4, 5 },
            UnreadOnly = true
        };

        var decoded = SmartConditionsCodec.Decode(SmartConditionsCodec.Encode(original));

        Assert.Equal("Genre", decoded.Keyword!.Field);
        Assert.Equal("SF", decoded.Keyword.Text);
        Assert.Equal(2, decoded.Keyword.Mode);
        Assert.Equal(14, decoded.Date!.Days);
        Assert.Equal(new[] { 0, 3 }, decoded.Types!.OrderBy(x => x));
        Assert.Equal(new[] { 4, 5 }, decoded.Rates!.OrderBy(x => x));
        Assert.True(decoded.UnreadOnly);
    }
    /// <summary>
    /// Taken verbatim from the live library's "Bavel" shelf.
    /// </summary>
    [Fact]
    public void Decode_ReadsKeywordConditionFromLiveShelf()
    {
        var conditions = SmartConditionsCodec.Decode(
            """{"Keyword Condition":{"Key":"Bavel","Condition":"Title","Option":0}}""");

        Assert.NotNull(conditions.Keyword);
        Assert.Equal("Bavel", conditions.Keyword.Text);
        Assert.Equal("Title", conditions.Keyword.Field);
        Assert.Equal(0, conditions.Keyword.Mode);
        Assert.Null(conditions.Date);
        Assert.False(conditions.UnreadOnly);
    }

    /// <summary>
    /// Also verbatim from the live library. Note "Condition" holds the string
    /// "Date Added" where the format otherwise uses an integer; the Mac app's decoder
    /// fails that cast and falls back to 0, which means date added. This port matches
    /// that rather than reinterpreting the string, so both apps show the same shelf.
    /// </summary>
    [Fact]
    public void Decode_DateConditionWithStringField_FallsBackToDateAdded()
    {
        var conditions = SmartConditionsCodec.Decode(
            """{"Date Condition":{"Key":30,"Condition":"Date Added","Option":0}}""");

        Assert.NotNull(conditions.Date);
        Assert.Equal(30, conditions.Date.Days);
        Assert.Equal(0, conditions.Date.Field);
        Assert.Equal(0, conditions.Date.Mode);
    }

    [Fact]
    public void Decode_ReadsTypeRateAndUnseenConditions()
    {
        var conditions = SmartConditionsCodec.Decode(
            """{"Type Condition":{"Key":[0,2]},"Rate Condition":{"Key":[4,5]},"Unseen Condition":{"Key":true}}""");

        Assert.Equal(new[] { 0, 2 }, conditions.Types!.Order());
        Assert.Equal(new[] { 4, 5 }, conditions.Rates!.Order());
        Assert.True(conditions.UnreadOnly);
    }

    [Fact]
    public void Decode_EmptyKeywordText_IsIgnored()
    {
        var conditions = SmartConditionsCodec.Decode(
            """{"Keyword Condition":{"Key":"","Condition":"Title","Option":0}}""");

        Assert.Null(conditions.Keyword);
        Assert.True(conditions.IsEmpty);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    public void Decode_Garbage_YieldsNoConditions(string? json)
    {
        Assert.True(SmartConditionsCodec.Decode(json).IsEmpty);
    }
}
