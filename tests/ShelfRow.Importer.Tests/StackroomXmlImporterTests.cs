using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using ShelfRow.Core.Models;
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

    [Fact]
    public async Task ImportAsync_MergesExistingBooksShelvesAndVolumes()
    {
        const string sampleXml = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<plist version=""1.0""><dict>
<key>Books</key><dict>
  <key>101</key><dict><key>ID</key><integer>101</integer><key>Title</key><string>Legacy match</string><key>Path</key><string>/Volumes/Books/Changed.zip</string></dict>
  <key>202</key><dict><key>ID</key><integer>202</integer><key>Title</key><string>Path match</string><key>Path</key><string>/Volumes/Books/SciFi/Second.zip</string></dict>
  <key>303</key><dict><key>ID</key><integer>303</integer><key>Title</key><string>New book</string><key>Path</key><string>/Volumes/Books/SciFi/Third.zip</string></dict>
</dict>
<key>Playlists</key><array>
  <dict><key>Title</key><string>Favorites</string><key>Type</key><integer>0</integer></dict>
  <dict><key>Title</key><string>New collection</string><key>Type</key><integer>0</integer><key>Items</key><array><integer>101</integer><integer>202</integer><integer>303</integer></array></dict>
</array>
</dict></plist>";

        var volume = new Volume { Name = "Books", LastKnownPath = "/Volumes/Books" };
        var legacyMatch = new Item { LegacyId = 101, RelativePath = "Original.zip", Title = "Existing legacy item", VolumeId = volume.Id };
        var pathMatch = new Item { RelativePath = "SciFi/Second.zip", Title = "Existing path item", VolumeId = volume.Id };
        var existingShelf = new Shelf { Title = "Favorites", Type = 0 };
        var context = new StackroomImportMergeContext(
            new[] { legacyMatch, pathMatch },
            new[] { existingShelf },
            new[] { volume });

        var importer = new StackroomXmlImporter();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sampleXml));
        var result = await importer.ImportAsync(stream, context);

        var importedBook = Assert.Single(result.ImportedBooks);
        Assert.Equal(303, importedBook.LegacyId);
        Assert.Equal(volume.Id, importedBook.VolumeId);
        Assert.Equal(2, result.SkippedBooks);
        Assert.Empty(result.DiscoveredVolumes);

        var importedShelf = Assert.Single(result.ImportedShelves);
        Assert.Equal("New collection", importedShelf.Title);
        Assert.Equal(1, result.SkippedShelves);
        Assert.Equal(3, importedShelf.ItemIds.Count);
        Assert.Contains(legacyMatch.Id, importedShelf.ItemIds);
        Assert.Contains(pathMatch.Id, importedShelf.ItemIds);
        Assert.Contains(importedBook.Id, importedShelf.ItemIds);

        Assert.Equal(2, result.ItemsWithUpdatedShelfMembership.Count);
        Assert.Contains(importedShelf.Id, legacyMatch.ShelfIds);
        Assert.Contains(importedShelf.Id, pathMatch.ShelfIds);
    }
}
