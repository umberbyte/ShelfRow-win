using System.Collections.Generic;
using System.Text.Json;

namespace ShelfRow.Core.Models;

/// <summary>
/// A smart shelf's filter, as stored in <see cref="Shelf.SmartConditionsJson"/>.
///
/// The JSON keeps the legacy Stackroom XML's top-level keys ("Keyword Condition",
/// "Date Condition", ...) so shelves imported years ago keep working, and so the
/// Mac app and this one read the same definitions.
/// </summary>
public class SmartConditions
{
    public KeywordCondition? Keyword { get; set; }
    public DateCondition? Date { get; set; }

    /// <summary>Selected book type indices. Null means every type.</summary>
    public HashSet<int>? Types { get; set; }

    /// <summary>Selected ratings 1..5. Null means every rating.</summary>
    public HashSet<int>? Rates { get; set; }

    public bool UnreadOnly { get; set; }

    public bool IsEmpty => Keyword == null && Date == null && Types == null && Rates == null && !UnreadOnly;

    public class KeywordCondition
    {
        /// <summary>Title / Author / Genre / Relation / Keyword A / Keyword B / Neta.</summary>
        public string Field { get; set; } = "Title";

        public string Text { get; set; } = string.Empty;

        /// <summary>0 = contains, 1 = does not contain, 2 = equals.</summary>
        public int Mode { get; set; }
    }

    public class DateCondition
    {
        /// <summary>0 = date added, 1 = date last read.</summary>
        public int Field { get; set; }

        public int Days { get; set; } = 30;

        /// <summary>0 = within the last N days, 1 = longer ago than N days.</summary>
        public int Mode { get; set; }
    }
}

public static class SmartConditionsCodec
{
    public static SmartConditions Decode(string? json)
    {
        var result = new SmartConditions();
        if (string.IsNullOrWhiteSpace(json))
            return result;

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return result;
        }

        if (root.ValueKind != JsonValueKind.Object)
            return result;

        if (root.TryGetProperty("Keyword Condition", out var keywordElement))
        {
            var keyword = new SmartConditions.KeywordCondition
            {
                Field = ReadString(keywordElement, "Condition") ?? "Title",
                Text = ReadString(keywordElement, "Key") ?? string.Empty,
                Mode = ReadInt(keywordElement, "Option") ?? 0
            };

            if (keyword.Text.Length > 0)
                result.Keyword = keyword;
        }

        if (root.TryGetProperty("Date Condition", out var dateElement))
        {
            result.Date = new SmartConditions.DateCondition
            {
                // The legacy format stores the day count in "Key", sometimes as a string.
                Days = ReadInt(dateElement, "Key") ?? 30,
                Field = ReadInt(dateElement, "Condition") ?? 0,
                Mode = ReadInt(dateElement, "Option") ?? 0
            };
        }

        result.Types = ReadIntSet(root, "Type Condition");
        result.Rates = ReadIntSet(root, "Rate Condition");

        if (root.TryGetProperty("Unseen Condition", out var unseenElement) &&
            unseenElement.TryGetProperty("Key", out var unseenKey))
        {
            result.UnreadOnly = unseenKey.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => unseenKey.GetInt32() != 0,
                _ => false
            };
        }

        return result;
    }

    private static string? ReadString(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out int number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out int parsed) => parsed,
            _ => null
        };
    }

    private static HashSet<int>? ReadIntSet(JsonElement root, string conditionName)
    {
        if (!root.TryGetProperty(conditionName, out var condition) ||
            !condition.TryGetProperty("Key", out var key) ||
            key.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = new HashSet<int>();
        foreach (var element in key.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int number))
                values.Add(number);
        }

        return values.Count > 0 ? values : null;
    }
}
