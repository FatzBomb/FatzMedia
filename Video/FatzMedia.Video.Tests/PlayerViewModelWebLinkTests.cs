using FatzMedia.Video.Sample.Player.Shared.Hosting;

namespace FatzMedia.Video.Tests;

/// <summary>
/// Stage 060A: the test players' web link box. The shared view model attaches the host's web player to the engine,
/// shows the web view while a web item lasts and gives the media area back when a local file loads.
/// </summary>
public sealed class PlayerViewModelWebLinkTests
{
    const string Video = "https://youtu.be/dQw4w9WgXcQ";

    [Fact]
    public async Task WithoutAHostWebView_TheWebLinkCommandsAreOff()
    {
        await using var harness = await PlayerViewModelHarness.StartAsync();
        var viewModel = harness.ViewModel;

        viewModel.WebLink = Video;

        Assert.False(viewModel.HasWebView);
        Assert.False(viewModel.OpenWebLinkCommand.CanExecute(null));
        Assert.False(viewModel.WebSignInCommand.CanExecute(null));
        Assert.False(viewModel.WebSignOutCommand.CanExecute(null));
    }

    [Fact]
    public async Task OpenNeedsATextAndAStartedPlayer()
    {
        var webView = new TestWebView();
        var harness = new PlayerViewModelHarness(webView: webView);
        await using var _ = harness;
        harness.ViewModel.WebLink = Video;
        Assert.False(harness.ViewModel.OpenWebLinkCommand.CanExecute(null));

        await harness.ViewModel.StartAsync();
        Assert.True(harness.ViewModel.OpenWebLinkCommand.CanExecute(null));

        harness.ViewModel.WebLink = "   ";
        Assert.False(harness.ViewModel.OpenWebLinkCommand.CanExecute(null));
    }

    [Fact]
    public async Task OpeningAYouTubeLink_StartsTheWebViewAttachesItShowsItAndLoadsTheLink()
    {
        var webView = new TestWebView();
        await using var harness = await PlayerViewModelHarness.StartAsync(webView: webView);
        var viewModel = harness.ViewModel;

        viewModel.WebLink = $" {Video} ";
        await viewModel.OpenWebLinkCommand.ExecuteAsync(null);

        Assert.Equal(1, webView.ReadyCount);
        Assert.True(webView.Visible);
        Assert.True(harness.Player.IsWebMedia);
        Assert.Equal(1, webView.Player.LoadCount);
        Assert.Equal("dQw4w9WgXcQ", webView.Player.Current?.ItemId);
        Assert.True(webView.Player.LastAutoplay);
        Assert.Empty(harness.Engine.Engine.OpenedSources);
        Assert.Equal(Video, viewModel.LoadedMediaText);
        Assert.False(viewModel.IsBusy);
        Assert.StartsWith("Loaded web link", viewModel.StatusText);
    }

    [Fact]
    public async Task OpeningASecondLink_ReusesTheWebView()
    {
        var webView = new TestWebView();
        await using var harness = await PlayerViewModelHarness.StartAsync(webView: webView);

        harness.ViewModel.WebLink = Video;
        await harness.ViewModel.OpenWebLinkCommand.ExecuteAsync(null);
        harness.ViewModel.WebLink = "https://www.youtube.com/watch?v=aqz-KE-bpKQ";
        await harness.ViewModel.OpenWebLinkCommand.ExecuteAsync(null);

        Assert.Equal(2, webView.Player.LoadCount);
        Assert.Equal("aqz-KE-bpKQ", webView.Player.Current?.ItemId);
        Assert.True(harness.Player.IsWebMedia);
        Assert.True(webView.Visible);
    }

    [Theory]
    [InlineData("song.mp3")]
    [InlineData(@"C:\music\song.mp3")]
    [InlineData("https://example.com/clip.mp4")]
    public async Task ATextThatIsNotAYouTubeLink_IsRefusedWithoutTouchingTheEngineOrTheWebView(string text)
    {
        var webView = new TestWebView();
        await using var harness = await PlayerViewModelHarness.StartAsync(webView: webView);

        harness.ViewModel.WebLink = text;
        await harness.ViewModel.OpenWebLinkCommand.ExecuteAsync(null);

        Assert.Equal("Not a YouTube link.", harness.ViewModel.WebInfoText);
        Assert.Equal(0, webView.ReadyCount);
        Assert.False(webView.Visible);
        Assert.Empty(harness.Engine.Engine.OpenedSources);
        Assert.False(harness.Player.IsOpen);
    }

    [Fact]
    public async Task AYouTubePlaylist_ReachesTheEngineAsAnErrorAndTheWebViewStaysHidden()
    {
        var webView = new TestWebView();
        await using var harness = await PlayerViewModelHarness.StartAsync(webView: webView);

        harness.ViewModel.WebLink = "https://www.youtube.com/playlist?list=PL1";
        await harness.ViewModel.OpenWebLinkCommand.ExecuteAsync(null);
        _ = harness.Dispatcher.RunPending();

        Assert.Equal(0, webView.ReadyCount);
        Assert.False(webView.Visible);
        Assert.Equal(0, webView.Player.LoadCount);
        Assert.Contains(harness.ViewModel.DiagnosticLog, static entry => entry.Message.Contains("playlist", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("playlist", harness.ViewModel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ALocalFileAfterAWebLink_PlaysInTheEngineAndHidesTheWebView()
    {
        var webView = new TestWebView();
        await using var harness = await PlayerViewModelHarness.StartAsync(webView: webView);
        harness.ViewModel.WebLink = Video;
        await harness.ViewModel.OpenWebLinkCommand.ExecuteAsync(null);
        Assert.True(webView.Visible);

        await harness.LoadAsync("song.mp3");
        _ = harness.Dispatcher.RunPending();

        Assert.False(harness.Player.IsWebMedia);
        Assert.True(harness.Player.IsOpen);
        Assert.False(webView.Visible);
        Assert.Contains("pause", webView.Player.Commands.ToArray());
    }

    [Fact]
    public async Task AWebLinkAfterALocalFile_ShowsTheWebViewAgain()
    {
        var webView = new TestWebView();
        await using var harness = await PlayerViewModelHarness.StartAsync(webView: webView);
        await harness.LoadAsync("song.mp3");

        harness.ViewModel.WebLink = Video;
        await harness.ViewModel.OpenWebLinkCommand.ExecuteAsync(null);
        _ = harness.Dispatcher.RunPending();

        Assert.True(harness.Player.IsWebMedia);
        Assert.True(webView.Visible);
        Assert.False(harness.Engine.Engine.IsOpen);
    }

    [Fact]
    public async Task AWebViewThatCannotStart_ReportsItAndStaysHidden()
    {
        var webView = new TestWebView { StartFailure = new InvalidOperationException("WebView2 runtime missing") };
        await using var harness = await PlayerViewModelHarness.StartAsync(webView: webView);

        harness.ViewModel.WebLink = Video;
        await harness.ViewModel.OpenWebLinkCommand.ExecuteAsync(null);

        Assert.False(webView.Visible);
        Assert.False(harness.Player.IsWebMedia);
        Assert.Contains("WebView2 runtime missing", harness.ViewModel.StatusText);
        Assert.False(harness.ViewModel.IsBusy);
    }

    [Fact]
    public async Task SignIn_ShowsTheWebViewAndOpensTheSignInPage_SignOutClearsTheProfile()
    {
        var webView = new TestWebView();
        await using var harness = await PlayerViewModelHarness.StartAsync(webView: webView);

        await harness.ViewModel.WebSignInCommand.ExecuteAsync(null);
        Assert.True(webView.Visible);
        Assert.Contains("signin", webView.Player.Commands.ToArray());

        await harness.ViewModel.WebSignOutCommand.ExecuteAsync(null);
        Assert.Contains("signout", webView.Player.Commands.ToArray());
        Assert.False(harness.ViewModel.IsBusy);
    }

    [Fact]
    public async Task DisposingTheViewModel_DetachesTheWebPlayerFromTheEngine()
    {
        var webView = new TestWebView();
        var harness = await PlayerViewModelHarness.StartAsync(webView: webView);
        harness.ViewModel.WebLink = Video;
        await harness.ViewModel.OpenWebLinkCommand.ExecuteAsync(null);
        Assert.True(webView.Player.HasSubscribers);

        await harness.DisposeAsync();

        Assert.False(webView.Player.HasSubscribers);
    }

    sealed class TestWebView : IPlayerWebView
    {
        public PlayerCoreWebMediaTests.FakeWebPlayer Player { get; } = new();

        public Exception? StartFailure { get; init; }

        public int ReadyCount { get; private set; }

        public bool Visible { get; private set; }

        public Task<IWebMediaPlayer> EnsureReadyAsync()
        {
            if (StartFailure is { } failure)
                return Task.FromException<IWebMediaPlayer>(failure);

            ReadyCount++;
            return Task.FromResult<IWebMediaPlayer>(Player);
        }

        public void SetVisible(bool visible) => Visible = visible;
    }
}
