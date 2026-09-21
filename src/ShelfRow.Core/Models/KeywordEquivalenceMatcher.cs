using System.Globalization;
using System.Text;

namespace ShelfRow.Core.Models;

public static class KeywordEquivalenceMatcher
{
    public static bool Matches(Item item, string query, IEnumerable<KeywordEquivalenceRule> rules)
    {
        string normalizedQuery = Normalize(query);
        if (normalizedQuery.Length == 0)
            return true;

        string[] baseValues =
        [
            item.Title, item.Author, item.Genre, item.Relation,
            item.KeywordA, item.KeywordB, item.Memo
        ];
        if (baseValues.Any(value => Normalize(value).Contains(normalizedQuery, StringComparison.Ordinal)))
            return true;

        foreach (var rule in rules)
        {
            var terms = rule.Terms.Select(Normalize).Where(term => term.Length > 0).ToArray();
            if (!terms.Any(term => normalizedQuery.Contains(term, StringComparison.Ordinal)
                                   || term.Contains(normalizedQuery, StringComparison.Ordinal)))
                continue;

            string fieldValue = rule.Field.ToLowerInvariant() switch
            {
                "author" => item.Author,
                "genre" => item.Genre,
                "relation" => item.Relation,
                "keyworda" => item.KeywordA,
                "keywordb" => item.KeywordB,
                "memo" => item.Memo,
                _ => string.Empty
            };
            string normalizedValue = Normalize(fieldValue);
            if (terms.Any(normalizedValue.Contains))
                return true;
        }
        return false;
    }

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;
        string decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (char character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(character);
        }
        return builder.ToString().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
    }
}

