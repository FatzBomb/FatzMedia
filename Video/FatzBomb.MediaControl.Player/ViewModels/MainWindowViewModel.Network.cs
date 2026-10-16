using FatzMediaPlayer = FatzBomb.MediaControl.Wpf.Player;

namespace FatzBomb.MediaControl.Player.ViewModels;

/// <summary>
/// Validation-host wiring for network sources (Stage 014A): <c>--url=&lt;http(s)://...&gt;</c>
/// loads a network stream after startup and <c>--net-retries=&lt;n&gt;</c> opts into bounded
/// retries. Buffering, metadata, and capabilities are logged to the diagnostics panel.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private void StartNetworkValidation(FatzMediaPlayer player)
    {
        player.BufferingChanged += OnPlayerBufferingChanged;
        player.StreamMetadataChanged += OnPlayerStreamMetadataChanged;
        if (App.StartupNetworkUri is { } uri)
            _ = LoadNetworkForValidationAsync(player, uri);
    }

    private void StopNetworkValidation(FatzMediaPlayer player)
    {
        player.BufferingChanged -= OnPlayerBufferingChanged;
        player.StreamMetadataChanged -= OnPlayerStreamMetadataChanged;
    }

    private async Task LoadNetworkForValidationAsync(FatzMediaPlayer player, Uri uri)
    {
        try
        {
            var loaded = await player.LoadAsync(MediaInputSource.FromUri(uri)).ConfigureAwait(false);
            MediaSourceCapabilities? capabilities = player.CurrentSourceCapabilities;
            ReportNetwork(loaded
                ? $"Network source loaded: {capabilities?.SourceClass}, seekable={capabilities?.IsSeekable}, duration={player.Duration}"
                : $"Could not load network source: {uri}");
        }
        catch (Exception ex)
        {
            ReportNetwork($"Network load failed: {ex.Message}");
        }
    }

    private void OnPlayerBufferingChanged(object? sender, MediaBufferingChangedEventArgs e) =>
        ReportNetwork($"Buffering={e.IsBuffering} fill={e.BufferFill:P0}");

    private void OnPlayerStreamMetadataChanged(object? sender, MediaStreamMetadataChangedEventArgs e) =>
        ReportNetwork($"Stream metadata: artist='{e.Artist}' title='{e.Title}'");

    private void ReportNetwork(string message) =>
        UpdateEffectOnUiThread(() => HandleDiagnostic(MediaDiagnosticLevel.Info, "Network", message));
}
