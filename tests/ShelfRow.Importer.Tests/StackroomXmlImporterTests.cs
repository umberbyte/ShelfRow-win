using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using ShelfRow.Importer;
using Xunit;

namespace ShelfRow.Importer.Tests;

public class StackroomXmlImporterTests
{
    [Fact]
    public void CleanXmlBytes_RemovesIllegalControlCharacters()
    {
        // 0x01, 0x02 are forbidden in XML 1.0; 0x09 (tab), 0x0A (LF), 0x0D (CR) are allowed.
        byte[] input = new byte[] { (byte)'A', 0x01, (byte)'B', 0x0C, (byte)'C', 0x0A };
        byte[] cleaned = ApplePlistParser.CleanXmlBytes(input);

        Assert.Equal(new byte[] { (byte)'A', (byte)'B', (byte)'C', 0x0A }, cleaned);
    }

    [Fact]
    public async Task ImportAsync_ParsesBooksAndShelvesCorrectly()
    {
        string sampleXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
    <key>Books</key>
    <dict>
        <key>101</key>
        <dict>
            <key>ID</key><integer>101</integer>
            <key>Title</key><string>Test Adventure</string>
            <key>Author</key><string>Arthur C.</string>
            <key>Path</key><string>/Volumes/Books/SciFi/Adventure.zip</string>
            <key>My Rate</key><integer>5</integer>
            <key>Unseen</key><false/>
            <key>Pages</key><integer>320</integer>
            <key>Neta</key><string>Classic space story</string>
            <key>Keyword A</key><string>Space</string>
        </dict>
    </dict>
    <key>Playlists</key>
    <array>
        <dict>
            <key>Title</key><string>Favorites</string>
            <key>Type</key><integer>0</integer>
            <key>Icon</key><integer>2</integer>
            <key>Items</key>
            <array>
                <integer>101</integer>
            </array>
        </dict>
        <dict>
            <key>Title</key><string>High Rated Smart Shelf</string>
            <key>Type</key><integer>1</integer>
            <key>Icon</key><integer>5</integer>
            <key>Conditions</key>
            <dict>
                <key>rate</key><integer>5</integer>
            </dict>
        </dict>
    </array>
</dict>
</plist>";

        var importer = new StackroomXmlImporter();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sampleXml));

        var result = await importer.ImportAsync(stream);

        // Books assertion
        Assert.Single(result.ImportedBooks);
        var book = result.ImportedBooks[0];
        Assert.Equal(101, book.LegacyId);
        Assert.Equal("Test Adventure", book.Title);
        Assert.Equal("Arthur C.", book.Author);
        Assert.Equal("SciFi/Adventure.zip", book.RelativePath);
        Assert.Equal(5, book.Rating);
        Assert.False(book.IsUnread);
        Assert.Equal(320, book.Pages);
        Assert.Equal("Classic space story", book.Memo);
        Assert.Equal("Space", book.KeywordA);

        // Volume assertion
        Assert.Single(result.DiscoveredVolumes);
        Assert.Equal("/Volumes/Books", result.DiscoveredVolumes[0].LastKnownPath);
        Assert.Equal("Books", result.DiscoveredVolumes[0].Name);

        // Shelves assertion
        Assert.Equal(2, result.ImportedShelves.Count);
        var favShelf = result.ImportedShelves[0];
        Assert.Equal("Favorites", favShelf.Title);
        Assert.Equal(0, favShelf.Type);
        Assert.Single(favShelf.ItemIds);
        Assert.Equal(book.Id, favShelf.ItemIds[0]);

        var smartShelf = result.ImportedShelves[1];
        Assert.Equal("High Rated Smart Shelf", smartShelf.Title);
        Assert.Equal(1, smartShelf.Type);
        Assert.True(smartShelf.IsSmart);
        Assert.NotNull(smartShelf.SmartConditionsJson);
    }
}
