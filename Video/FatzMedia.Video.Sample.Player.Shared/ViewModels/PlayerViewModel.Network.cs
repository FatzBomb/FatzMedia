using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FatzMedia.Video.Sample.Player.Shared.ViewModels;

/// <summary>
/// Validation-host wiring for network sources (Stage 014A), driven from the Next flyout's URL
/// box. <c>--url=&lt;http(s)://...&gt;</c> loads a stream after startup and
/// <c>--net-retries=&lt;n&gt;</c> opts into bounded retries. <c>--net-ca-file=&lt;pem&gt;</c> and
/// <c>--net-allow-invalid-certs</c> set the https certificate policy (ENG-034). Buffering, stream titles, and
/// capabilities are shown in the flyout and logged to the Log panel.
/// </summary>
public partial class PlayerViewModel
{
    string _lastStreamTitle = string.Empty;

    public AsyncRelayCommand OpenUrlCommand { get; private set; } = null!;

    public string NetworkUrl
    {
        get;
        set
        {
            if (SetProperty(ref field, value ?? string.Empty))
                OpenUrlCommand.NotifyCanExecuteChanged();
        }
    } = string.Empty;

    [ObservableProperty]
    public partial string StreamInfoText { get; private set; } = string.Empty;

    void InitializeNetworkControls()
    {
        OpenUrlCommand = new AsyncRelayCommand(OpenUrlAsync, () => CanUsePlayer() && TryParseNetworkUrl(NetworkUrl, out _));
        NetworkUrl = Options.NetworkUri?.AbsoluteUri ?? string.Empty;
    }

    void StartNetworkValidation(PlayerCore player)
    {
        player.BufferingChanged += OnPlayerBufferingChanged;
        player.StreamMetadataChanged += OnPlayerStreamMetadataChanged;
        if (Options.NetworkUri is { } uri)
            _ = LoadNetworkAsync(player, uri);
    }

    void StopNetworkValidation(PlayerCore player)
    {
        player.BufferingChanged -= OnPlayerBufferingChanged;
        player.StreamMetadataChanged -= OnPlayerStreamMetadataChanged;
    }

    async Task OpenUrlAsync()
    {
        if (Player is not { } player)
            return;

        if (!TryParseNetworkUrl(NetworkUrl, out var uri))
        {
            StatusText = "Enter an http:// or https:// URL.";
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = $"Connecting: {uri}";
            await LoadNetworkAsync(player, uri);
            RefreshPlaybackPosition();
        }
        finally
        {
            IsBusy = false;
        }
    }

    async Task LoadNetworkAsync(PlayerCore player, Uri uri)
    {
        RunOnUiThread(() =>
        {
            _lastStreamTitle = string.Empty;
            LoadingMediaText = uri.ToString();
            StreamInfoText = "Connecting...";
        });
        try
        {
            var loaded = await player.LoadAsync(MediaInputSource.FromUri(uri)).ConfigureAwait(false);
            var capabilities = player.CurrentSourceCapabilities;
            var summary = loaded
                ? $"{capabilities?.SourceClass}, seekable={capabilities?.IsSeekable}, duration={player.Duration}"
                : $"Could not load {uri}";
            ReportNetwork(loaded ? $"Network source loaded: {summary}" : summary);
            RunOnUiThread(() =>
            {
                if (loaded)
                    LoadedMediaText = uri.ToString();
                StreamInfoText = loaded ? $"{capabilities?.SourceClass}" : "Not connected";
                StatusText = loaded ? $"Streaming: {uri}" : $"Could not load: {uri}";
            });
        }
        catch (Exception ex)
        {
            ReportNetwork($"Network load failed: {ex.Message}");
            RunOnUiThread(() => StreamInfoText = "Not connected");
        }
    }

    void OnPlayerBufferingChanged(object? sender, MediaBufferingChangedEventArgs e)
    {
        ReportNetwork($"Buffering={e.IsBuffering} fill={e.BufferFill:P0}");
        RunOnUiThread(() => StreamInfoText = e.IsBuffering
            ? $"Buffering {e.BufferFill:P0}"
            : _lastStreamTitle.Length > 0 ? _lastStreamTitle : "Streaming");
    }

    void OnPlayerStreamMetadataChanged(object? sender, MediaStreamMetadataChangedEventArgs e)
    {
        ReportNetwork($"Stream metadata: artist='{e.Artist}' title='{e.Title}'");
        RunOnUiThread(() =>
        {
            _lastStreamTitle = e.RawTitle ?? string.Empty;
            StreamInfoText = _lastStreamTitle;
        });
    }

    /// <summary>Gets whether <paramref name="text"/> is an absolute http or https URL.</summary>
    public static bool TryParseNetworkUrl(string text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? uri) =>
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    void ReportNetwork(string message) =>
        RunOnUiThread(() => HandleDiagnostic(MediaDiagnosticLevel.Info, "Network", message));
}
