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
        using var cleanStream = new XmlControlCharacterFilteringStream(stream);
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
        byte[] output = new byte[bytes.Length];
        int written = 0;
        foreach (byte b in bytes)
        {
            if (IsIllegalXmlControl(b))
                continue;
            output[written++] = b;
        }
        return written == output.Length ? output : output[..written];
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
            "date" => DateTime.TryParse(element.Value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime dt) ? dt : null,
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

    private static bool IsIllegalXmlControl(byte value) =>
        value <= 0x08 || value is 0x0B or 0x0C || value is >= 0x0E and <= 0x1F;

    /// <summary>Filters illegal XML 1.0 bytes while XDocument pulls from the source.</summary>
    private sealed class XmlControlCharacterFilteringStream : Stream
    {
        private readonly Stream _source;
        public XmlControlCharacterFilteringStream(Stream source) => _source = source;
        public override bool CanRead => _source.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int written = 0;
            while (written == 0)
            {
                int read = _source.Read(buffer, offset, count);
                if (read == 0)
                    return 0;
                for (int i = 0; i < read; i++)
                {
                    byte value = buffer[offset + i];
                    if (!IsIllegalXmlControl(value))
                        buffer[offset + written++] = value;
                }
            }
            return written;
        }

        public override int Read(Span<byte> buffer)
        {
            byte[] temporary = new byte[Math.Min(buffer.Length, 81920)];
            int read = Read(temporary, 0, temporary.Length);
            temporary.AsSpan(0, read).CopyTo(buffer);
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
