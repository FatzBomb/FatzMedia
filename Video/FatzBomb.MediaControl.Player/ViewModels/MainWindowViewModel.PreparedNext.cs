using FatzMediaPlayer = FatzBomb.MediaControl.Wpf.Player;

namespace FatzBomb.MediaControl.Player.ViewModels;

/// <summary>
/// Validation-host wiring for the Core prepared-next slot (Stage 031A). Started with
/// <c>--prepare-next=&lt;path&gt;</c> and optionally <c>--auto-next</c>.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private void StartPreparedNextValidation(FatzMediaPlayer player)
    {
        player.PreparedNextChanged += OnPreparedNextChanged;
        player.PreparedNextTransitioned += OnPreparedNextTransitioned;
        player.AutoTransitionToPreparedNext = App.AutoTransitionToPreparedNext;
        if (App.PrepareNextPath is { } path)
            _ = PrepareNextForValidationAsync(player, path);
    }

    private void StopPreparedNextValidation(FatzMediaPlayer player)
    {
        player.PreparedNextChanged -= OnPreparedNextChanged;
        player.PreparedNextTransitioned -= OnPreparedNextTransitioned;
    }

    private async Task PrepareNextForValidationAsync(FatzMediaPlayer player, string path)
    {
        try
        {
            var prepared = await player.PrepareNextAsync(path).ConfigureAwait(false);
            ReportPreparedNext(prepared
                ? $"Prepared next: {path} (auto-next={App.AutoTransitionToPreparedNext})"
                : $"Could not prepare next: {path}");
        }
        catch (Exception ex)
        {
            ReportPreparedNext($"Prepare next failed: {ex.Message}");
        }
    }

    private void OnPreparedNextChanged(object? sender, PreparedNextChangedEventArgs e)
    {
        MediaAnalysisResult? analysis = e.Current.Analysis;
        var analysisText = analysis is null
            ? "no analysis"
            : $"effective={analysis.EffectiveDuration:F2}s, gain={analysis.NormalizationGain:F3}";
        ReportPreparedNext($"Prepared next {e.Previous.State} -> {e.Current.State} ({analysisText})");
    }

    private void OnPreparedNextTransitioned(object? sender, PreparedNextTransitionedEventArgs e) =>
        ReportPreparedNext($"Transitioned to prepared next ({(e.IsAutomatic ? "automatic" : "manual")}): {e.Source.DisplayName}");

    private void ReportPreparedNext(string message) =>
        UpdateEffectOnUiThread(() => HandleDiagnostic(MediaDiagnosticLevel.Info, "PreparedNext", message));
}
