using FatzBombMedia.MediaControl;

namespace FatzBombMedia.MediaControl.Tests;

public class MediaErrorEventArgsTests
{
    [Fact]
    public void Constructor_AssignsAllProperties()
    {
        var inner = new InvalidOperationException("boom");
        var args = new MediaErrorEventArgs(MediaErrorCode.OpenFailed, "failed to open", "C:\\media.mp3", inner);

        Assert.Equal(MediaErrorCode.OpenFailed, args.Code);
        Assert.Equal("failed to open", args.Message);
        Assert.Equal("C:\\media.mp3", args.SourcePath);
        Assert.Same(inner, args.InnerException);
    }

    [Fact]
    public void Constructor_AllowsNullOptionalValues()
    {
        var args = new MediaErrorEventArgs(MediaErrorCode.Unknown, "something went wrong");

        Assert.Equal(MediaErrorCode.Unknown, args.Code);
        Assert.Equal("something went wrong", args.Message);
        Assert.Null(args.SourcePath);
        Assert.Null(args.InnerException);
    }

    [Fact]
    public void Constructor_ConvertsNullMessageToEmpty()
    {
        var args = new MediaErrorEventArgs(MediaErrorCode.None, null!);

        Assert.Equal(MediaErrorCode.None, args.Code);
        Assert.Equal(string.Empty, args.Message);
    }

    [Theory]
    [InlineData(MediaErrorCode.None)]
    [InlineData(MediaErrorCode.Unknown)]
    [InlineData(MediaErrorCode.FileNotFound)]
    [InlineData(MediaErrorCode.NativeLibraryMissing)]
    [InlineData(MediaErrorCode.UnsupportedFormat)]
    [InlineData(MediaErrorCode.OpenFailed)]
    [InlineData(MediaErrorCode.DecodeFailed)]
    [InlineData(MediaErrorCode.InternalError)]
    public void AllDefinedCodes_AreRoundTrippable(MediaErrorCode code)
    {
        var args = new MediaErrorEventArgs(code, "test");
        Assert.Equal(code, args.Code);
    }
}
