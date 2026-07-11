using FatzBombMedia.MediaControl.Core.Files;

namespace FatzBombMedia.MediaControl.Tests;

public class FileTypesTests
{
    [Theory]
    [InlineData("archive.zip", true)]
    [InlineData("archive.ZIP", true)]
    [InlineData("archive.rar", true)]
    [InlineData("archive.7z", true)]
    [InlineData("archive.tar", true)]
    [InlineData("archive.gz", true)]
    [InlineData("archive.bz2", true)]
    [InlineData("archive.xz", true)]
    [InlineData("archive.tgz", true)]
    [InlineData("archive.tbz2", true)]
    [InlineData("archive.txt", false)]
    [InlineData("noext", false)]
    public void IsArchive_MatchesArchiveExtensions(string filename, bool expected) =>
        Assert.Equal(expected, FileTypes.IsArchive(filename));

    [Theory]
    [InlineData("photo.png", true)]
    [InlineData("photo.PNG", true)]
    [InlineData("photo.jpg", true)]
    [InlineData("photo.jpeg", true)]
    [InlineData("photo.bmp", true)]
    [InlineData("photo.gif", true)]
    [InlineData("photo.tif", true)]
    [InlineData("photo.tiff", true)]
    [InlineData("photo.webp", false)] // intentionally excluded per source comment
    [InlineData("photo.txt", false)]
    public void IsImage_MatchesImageExtensions(string filename, bool expected) =>
        Assert.Equal(expected, FileTypes.IsImage(filename));

    [Theory]
    [InlineData("track.cdg", true)]
    [InlineData("track.CDG", true)]
    [InlineData("track.mp3", false)]
    [InlineData("noext", false)]
    public void IsCdg_MatchesCdgExtension(string filename, bool expected) =>
        Assert.Equal(expected, FileTypes.IsCdg(filename));

    [Theory]
    [InlineData("captions.srt", true)]
    [InlineData("captions.SRT", true)]
    [InlineData("captions.ass", true)]
    [InlineData("captions.ssa", true)]
    [InlineData("captions.txt", false)]
    public void IsSubtitle_MatchesSrtExtension(string filename, bool expected) =>
        Assert.Equal(expected, FileTypes.IsSubtitle(filename));

    [Fact]
    public void ArchiveSet_IsCaseInsensitive()
    {
        Assert.Contains(".zip", FileTypes.Archive);
        Assert.Contains(".ZIP", FileTypes.Archive);
    }

    [Fact]
    public void ImageSet_IsCaseInsensitive()
    {
        Assert.Contains(".png", FileTypes.Image);
        Assert.Contains(".PNG", FileTypes.Image);
    }

    [Fact]
    public void CdgSet_IsCaseInsensitive()
    {
        Assert.Contains(".cdg", FileTypes.Cdg);
        Assert.Contains(".CDG", FileTypes.Cdg);
    }

}
