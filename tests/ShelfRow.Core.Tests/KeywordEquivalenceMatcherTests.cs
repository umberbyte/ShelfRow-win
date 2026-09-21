using ShelfRow.Core.Models;

namespace ShelfRow.Core.Tests;

public class KeywordEquivalenceMatcherTests
{
    [Fact]
    public void Matches_ExpandsEquivalentTermsOnlyInConfiguredField()
    {
        var rule = new KeywordEquivalenceRule
        {
            Field = "keywordA",
            Terms = { "SF", "Science Fiction", "Sci-Fi" }
        };
        var matching = new Item { Title = "Solaris", KeywordA = "Science Fiction" };
        var wrongField = new Item { Title = "Solaris", Memo = "Science Fiction" };

        Assert.True(KeywordEquivalenceMatcher.Matches(matching, "SF", new[] { rule }));
        Assert.False(KeywordEquivalenceMatcher.Matches(wrongField, "SF", new[] { rule }));
    }

    [Fact]
    public void Normalize_IgnoresCaseWidthAndDiacritics()
    {
        Assert.Equal(
            KeywordEquivalenceMatcher.Normalize("ＣＡＦÉ"),
            KeywordEquivalenceMatcher.Normalize("cafe"));
    }
}
