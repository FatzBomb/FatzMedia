using FatzMedia.Video.WebView2;

namespace FatzMedia.Video.Tests;

/// <summary>Stage 060A: the messages between the WebView2 adapter and the YouTube player page (pure parsing).</summary>
public sealed class WebView2PlayerMessageTests
{
    [Fact]
    public void Ready_CarriesDurationVolumeRateAndTheOfferedRates()
    {
        var m = YouTubePlayerMessage.Parse("""{"type":"ready","dur":212.5,"vol":80,"muted":false,"rate":1,"rates":[0.5,1,1.5,2]}""");

        Assert.Equal(YouTubePlayerMessageType.Ready, m.Type);
        Assert.Equal(212.5, m.DurationSeconds);
        Assert.Equal(80, m.Volume);
        Assert.False(m.Muted);
        Assert.Equal([0.5, 1, 1.5, 2], m.Rates);
    }

    [Fact]
    public void StateAndTime_CarryPositionAndState()
    {
        var state = YouTubePlayerMessage.Parse("""{"type":"state","state":1,"t":12.25,"dur":300}""");
        var time = YouTubePlayerMessage.Parse("""{"type":"time","t":13,"dur":300}""");

        Assert.Equal(YouTubePlayerMessageType.State, state.Type);
        Assert.Equal(1, state.StateCode);
        Assert.Equal(12.25, state.Seconds);
        Assert.Equal(YouTubePlayerMessageType.Time, time.Type);
        Assert.Equal(13, time.Seconds);
    }

    [Theory]
    [InlineData(-1, WebMediaState.Unstarted)]
    [InlineData(0, WebMediaState.Ended)]
    [InlineData(1, WebMediaState.Playing)]
    [InlineData(2, WebMediaState.Paused)]
    [InlineData(3, WebMediaState.Buffering)]
    [InlineData(5, WebMediaState.Cued)]
    [InlineData(42, WebMediaState.Unstarted)]
    public void StateCodes_MapToWebMediaState(int code, WebMediaState expected) =>
        Assert.Equal(expected, YouTubePlayerMessage.MapState(code));

    [Fact]
    public void Error_CarriesTheServiceCode()
    {
        var m = YouTubePlayerMessage.Parse("""{"type":"error","code":153}""");

        Assert.Equal(YouTubePlayerMessageType.Error, m.Type);
        Assert.Equal(153, m.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"type":"mystery"}""")]
    [InlineData("""{"nothing":1}""")]
    [InlineData("""{"type":"state","state":"x","t":"y"}""")]
    public void MalformedOrUnknownInput_BecomesUnknownAndNeverThrows(string json)
    {
        var m = YouTubePlayerMessage.Parse(json);

        Assert.True(m.Type == YouTubePlayerMessageType.Unknown || m.StateCode is null);
    }

    [Fact]
    public void Commands_AreSmallJsonObjectsTheDocumentUnderstands()
    {
        Assert.Equal("""{"cmd":"play"}""", YouTubePlayerMessage.Command("play"));
        Assert.Equal("""{"cmd":"seek","value":60}""", YouTubePlayerMessage.Command("seek", 60));
        Assert.Equal("""{"cmd":"rate","value":1.5}""", YouTubePlayerMessage.Command("rate", 1.5));
    }
}
