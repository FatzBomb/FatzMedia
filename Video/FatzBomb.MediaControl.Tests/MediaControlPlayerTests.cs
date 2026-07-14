using FatzBomb.MediaControl.Core.Configuration;

namespace FatzBomb.MediaControl.Tests;

public class MediaControlPlayerTests
{
    [Fact]
    public void Constructor_NullPlatformFactory_Throws()
    {
        var exception = Assert.Throws<ArgumentNullException>(() =>
            new MediaControlPlayer(null!, MediaPlayerConfiguration.Auto, Array.Empty<string>()));

        Assert.Equal("platformFactory", exception.ParamName);
    }
}
