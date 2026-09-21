using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml.Linq;

namespace ShelfRow.Importer;

/// <summary>
/// Lightweight Apple XML Property List (plist) reader.
/// Cleans invalid XML 1.0 control characters before parsing.
/// </summary>
public static class ApplePlistParser
{
    public static object? Parse(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        byte[] rawBytes = memory.ToArray();
        byte[] cleanBytes = CleanXmlBytes(rawBytes);

        using var cleanStream = new MemoryStream(cleanBytes);
        var doc = XDocument.Load(cleanStream);
        var plist = doc.Root;
        if (plist == null || plist.Name.LocalName != "plist")
            throw new InvalidDataException("Not a valid Apple property list.");

        var firstChild = plist.Elements().GetEnumerator();
        if (!firstChild.MoveNext()) return null;

        return ParseElement(firstChild.Current);
    }

    public static byte[] CleanXmlBytes(byte[] bytes)
    {
        var output = new List<byte>(bytes.Length);
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            // Skip invalid XML 1.0 control bytes: 0x00-0x08, 0x0B-0x0C, 0x0E-0x1F
            if ((b >= 0x00 && b <= 0x08) || (b == 0x0B || b == 0x0C) || (b >= 0x0E && b <= 0x1F))
            {
                continue;
            }
            output.Add(b);
        }
        return output.ToArray();
    }

    private static object? ParseElement(XElement element)
    {
        return element.Name.LocalName switch
        {
            "dict" => ParseDict(element),
            "array" => ParseArray(element),
            "string" => element.Value,
            "integer" => long.TryParse(element.Value, out long val) ? val : 0L,
            "real" => double.TryParse(element.Value, System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : 0.0,
            "true" => true,
            "false" => false,
            "date" => DateTime.TryParse(element.Value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime dt) ? dt : DateTime.UtcNow,
            "data" => Convert.FromBase64String(element.Value.Trim()),
            _ => null
        };
    }

    private static Dictionary<string, object?> ParseDict(XElement dictElement)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        string? currentKey = null;

        foreach (var el in dictElement.Elements())
        {
            if (el.Name.LocalName == "key")
            {
                currentKey = el.Value;
            }
            else if (currentKey != null)
            {
                dict[currentKey] = ParseElement(el);
                currentKey = null;
            }
        }
        return dict;
    }

    private static List<object?> ParseArray(XElement arrayElement)
    {
        var list = new List<object?>();
        foreach (var el in arrayElement.Elements())
        {
            list.Add(ParseElement(el));
        }
        return list;
    }
}
