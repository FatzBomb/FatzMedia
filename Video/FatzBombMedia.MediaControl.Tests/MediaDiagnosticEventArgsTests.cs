namespace FatzBombMedia.MediaControl.Tests;

public class MediaDiagnosticEventArgsTests
{
    [Fact]
    public void Constructor_AssignsAllProperties()
    {
        var inner = new InvalidOperationException("boom");
        var args = new MediaDiagnosticEventArgs(MediaDiagnosticLevel.Error, "D3D11", "Failed to create device", "device0", inner);

        Assert.Equal(MediaDiagnosticLevel.Error, args.Level);
        Assert.Equal("D3D11", args.Category);
        Assert.Equal("Failed to create device", args.Message);
        Assert.Equal("device0", args.SourcePath);
        Assert.Same(inner, args.InnerException);
    }

    [Fact]
    public void Constructor_AllowsNullOptionalValues()
    {
        var args = new MediaDiagnosticEventArgs(MediaDiagnosticLevel.Info, "Bootstrap", "Native libraries loaded");

        Assert.Equal(MediaDiagnosticLevel.Info, args.Level);
        Assert.Equal("Bootstrap", args.Category);
        Assert.Equal("Native libraries loaded", args.Message);
        Assert.Null(args.SourcePath);
        Assert.Null(args.InnerException);
    }

    [Fact]
    public void Constructor_ConvertsNullStringsToEmpty()
    {
        var args = new MediaDiagnosticEventArgs(MediaDiagnosticLevel.Warning, null!, null!);

        Assert.Equal(MediaDiagnosticLevel.Warning, args.Level);
        Assert.Equal(string.Empty, args.Category);
        Assert.Equal(string.Empty, args.Message);
    }

    [Theory]
    [InlineData(MediaDiagnosticLevel.Info)]
    [InlineData(MediaDiagnosticLevel.Warning)]
    [InlineData(MediaDiagnosticLevel.Error)]
    public void AllDefinedLevels_AreRoundTrippable(MediaDiagnosticLevel level)
    {
        var args = new MediaDiagnosticEventArgs(level, "test", "test");
        Assert.Equal(level, args.Level);
    }
}
