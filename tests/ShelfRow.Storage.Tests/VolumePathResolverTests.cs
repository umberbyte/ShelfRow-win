using System;
using System.IO;
using ShelfRow.Core.Models;
using ShelfRow.Storage;
using Xunit;

namespace ShelfRow.Storage.Tests;

public class VolumePathResolverTests
{
    [Fact]
    public void SplitPosixPath_WithVolumesPrefix_ReturnsComponents()
    {
        string posix = "/Volumes/Manga/SeriesA/Vol1.zip";
        var (volumePath, volumeName, relativePath) = VolumePathResolver.SplitPosixPath(posix);

        Assert.Equal("/Volumes/Manga", volumePath);
        Assert.Equal("Manga", volumeName);
        Assert.Equal("SeriesA/Vol1.zip", relativePath);
    }

    [Fact]
    public void ConvertPosixToWindows_ReturnsUncPath()
    {
        string posix = "/Volumes/Manga/SeriesA/Vol1.zip";
        string win = VolumePathResolver.ConvertPosixToWindows(posix);

        Assert.Equal(@"\\Manga\SeriesA\Vol1.zip", win);
    }

    [Fact]
    public void ConvertWindowsToPosix_Unc_ReturnsPosix()
    {
        string unc = @"\\Manga\SeriesA\Vol1.zip";
        string posix = VolumePathResolver.ConvertWindowsToPosix(unc);

        Assert.Equal("/Volumes/Manga/SeriesA/Vol1.zip", posix);
    }

    [Fact]
    public void ConvertWindowsToPosix_DriveLetter_ReturnsPosix()
    {
        string drivePath = @"Z:\SeriesA\Vol1.zip";
        string posix = VolumePathResolver.ConvertWindowsToPosix(drivePath);

        Assert.Equal("/Volumes/Z/SeriesA/Vol1.zip", posix);
    }

    [Fact]
    public void ResolveToWindowsPath_WithConfiguredWindowsMountPath_ResolvesProperly()
    {
        var resolver = new VolumePathResolver();
        var volume = new Volume
        {
            LastKnownPath = "/Volumes/Books",
            WindowsMountPath = @"\\NAS\Books"
        };
        var item = new Item
        {
            VolumeId = volume.Id,
            RelativePath = "Novels/Scifi/Story.zip"
        };

        string resolved = resolver.ResolveToWindowsPath(item, volume);
        Assert.Equal(Path.Combine(@"\\NAS\Books", "Novels", "Scifi", "Story.zip"), resolved);
    }
}
