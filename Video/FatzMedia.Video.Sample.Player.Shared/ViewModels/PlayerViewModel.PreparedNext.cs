using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;


namespace FatzMedia.Video.Sample.Player.Shared.ViewModels;

/// <summary>
/// Validation-host wiring for the Core prepared-next slot (Stage 031A) and its fades (Stage
/// 031B), driven from the Next flyout. <c>--prepare-next=&lt;path&gt;</c>, <c>--auto-next</c>,
/// and <c>--fade-next=&lt;seconds&gt;</c> pre-set the same controls at startup.
/// </summary>
public partial class PlayerViewModel
{
    /// <summary>Manual fade length used when the fade slider is at 0 (transitions unfaded).</summary>
    private static readonly TimeSpan DefaultManualFade = TimeSpan.FromSeconds(3);

    public AsyncRelayCommand PlayNextNowCommand { get; private set; } = null!;

    public RelayCommand ClearNextCommand { get; private set; } = null!;

    public AsyncRelayCommand FadeOutCommand { get; private set; } = null!;

    public AsyncRelayCommand FadeInCommand { get; private set; } = null!;

    public RelayCommand CancelFadeCommand { get; private set; } = null!;

    /// <summary>Asks the host's file picker for the next item and prepares it.</summary>
    public AsyncRelayCommand ChooseNextCommand { get; private set; } = null!;

    [ObservableProperty]
    public partial string PreparedNextText { get; private set; } = "Nothing prepared";

    public bool AutoNext
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;
            if (Player is { } player)
                player.AutoTransitionToPreparedNext = field;
        }
    }

    /// <summary>Fade length for transitions into the prepared item; 0 switches instantly.</summary>
    public double FadeSeconds
    {
        get;
        set
        {
            var clamped = Math.Round(Math.Clamp(value, 0, 10), 1);
            if (!SetProperty(ref field, clamped)) return;
            OnPropertyChanged(nameof(FadeOutToolTip));
            OnPropertyChanged(nameof(FadeInToolTip));
            if (Player is { } player)
                player.PreparedNextTransitionFade = new AudioCrossfadeOptions { Duration = TimeSpan.FromSeconds(field) };
        }
    }

    public string FadeOutToolTip => $"Fade the current item to silence over {ManualFadeDuration.TotalSeconds:0.#}s (3s when FADE is 0)";

    public string FadeInToolTip => $"Fade the current item up from silence over {ManualFadeDuration.TotalSeconds:0.#}s (3s when FADE is 0)";

    private TimeSpan ManualFadeDuration => FadeSeconds > 0 ? TimeSpan.FromSeconds(FadeSeconds) : DefaultManualFade;

    private void InitializePreparedNextControls()
    {
        PlayNextNowCommand = new AsyncRelayCommand(PlayNextNowAsync, () => Player?.PreparedNext.State == PreparedNextState.Ready);
        ClearNextCommand = new RelayCommand(ClearNext, () => Player?.PreparedNext.State is PreparedNextState.Analyzing or PreparedNextState.Ready or PreparedNextState.Stale);
        FadeOutCommand = new AsyncRelayCommand(() => RunManualFadeAsync(fadeOut: true), CanUsePlayer);
        FadeInCommand = new AsyncRelayCommand(() => RunManualFadeAsync(fadeOut: false), CanUsePlayer);
        CancelFadeCommand = new RelayCommand(CancelFade, CanUsePlayer);
        ChooseNextCommand = new AsyncRelayCommand(ChooseNextAsync);
        AutoNext = Options.AutoNext;
        FadeSeconds = Options.FadeNext.TotalSeconds;
    }

    private void RefreshPreparedNextCommandStates()
    {
        PlayNextNowCommand.NotifyCanExecuteChanged();
        ClearNextCommand.NotifyCanExecuteChanged();
        FadeOutCommand.NotifyCanExecuteChanged();
        FadeInCommand.NotifyCanExecuteChanged();
        CancelFadeCommand.NotifyCanExecuteChanged();
    }

    private void StartPreparedNextValidation(PlayerCore player)
    {
        player.PreparedNextChanged += OnPreparedNextChanged;
        player.PreparedNextTransitioned += OnPreparedNextTransitioned;
        player.AutoTransitionToPreparedNext = AutoNext;
        player.PreparedNextTransitionFade = new AudioCrossfadeOptions { Duration = TimeSpan.FromSeconds(FadeSeconds) };
        if (Options.PrepareNextPath is { } path)
            _ = PrepareNextAsync(path);
    }

    private void StopPreparedNextValidation(PlayerCore player)
    {
        player.PreparedNextChanged -= OnPreparedNextChanged;
        player.PreparedNextTransitioned -= OnPreparedNextTransitioned;
    }

    /// <summary>Prepares <paramref name="path"/> as the next item (Next flyout or <c>--prepare-next</c>).</summary>
    public async Task PrepareNextAsync(string path)
    {
        if (Player is not { } player || string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var prepared = await player.PrepareNextAsync(path).ConfigureAwait(false);
            ReportPreparedNext(prepared
                ? $"Prepared next: {path} (auto-next={AutoNext}, fade={FadeSeconds:0.#}s)"
                : $"Could not prepare next: {path}");
        }
        catch (Exception ex)
        {
            ReportPreparedNext($"Prepare next failed: {ex.Message}");
        }
    }

    private async Task ChooseNextAsync()
    {
        if (_host.FilePicker is not { } picker)
        {
            StatusText = "No file picker is available.";
            return;
        }

        string? path;
        try
        {
            path = await picker.PickFileAsync("Choose the next item", Player?.OpenFileDialogFilter ?? "All files|*.*");
        }
        catch (Exception ex)
        {
            StatusText = $"Choose next failed: {ex.Message}";
            return;
        }

        if (path is not null)
            await PrepareNextAsync(path);
    }

    private async Task PlayNextNowAsync()
    {
        if (Player is not { } player)
            return;

        try
        {
            StatusText = FadeSeconds > 0 ? $"Fading to next ({FadeSeconds:0.#}s)..." : "Switching to next...";
            var transitioned = await player.TransitionToPreparedNextAsync();
            StatusText = transitioned ? "Playing next item." : "Transition to next did not happen (see diagnostics).";
            RefreshPlaybackPosition();
        }
        catch (Exception ex)
        {
            StatusText = $"Transition failed: {ex.Message}";
        }
    }

    private void ClearNext()
    {
        if (Player?.ClearPreparedNext() == true)
            StatusText = "Prepared next cleared.";
    }

    private async Task RunManualFadeAsync(bool fadeOut)
    {
        if (Player is not { } player)
            return;

        TimeSpan duration = ManualFadeDuration;
        var label = fadeOut ? "Fade out" : "Fade in";
        try
        {
            StatusText = $"{label} ({duration.TotalSeconds:0.#}s)...";
            var completed = fadeOut
                ? await player.FadeOutAsync(duration)
                : await player.FadeInAsync(duration);
            StatusText = completed
                ? $"{label} complete."
                : $"{label} did not complete (cancelled, superseded, or no fade stage for this media).";
        }
        catch (Exception ex)
        {
            StatusText = $"{label} failed: {ex.Message}";
        }
    }

    private void CancelFade()
    {
        Player?.CancelFade();
        StatusText = "Fade cancelled; the volume stays where the fade reached.";
    }

    private void OnPreparedNextChanged(object? sender, PreparedNextChangedEventArgs e)
    {
        MediaAnalysisResult? analysis = e.Current.Analysis;
        var analysisText = analysis is null
            ? "no analysis"
            : $"effective={analysis.EffectiveDuration:F2}s, gain={analysis.NormalizationGain:F3}";
        ReportPreparedNext($"Prepared next {e.Previous.State} -> {e.Current.State} ({analysisText})");
        RunOnUiThread(() =>
        {
            PreparedNextText = FormatPreparedNext(e.Current);
            RefreshPreparedNextCommandStates();
        });
    }

    private void OnPreparedNextTransitioned(object? sender, PreparedNextTransitionedEventArgs e) =>
        ReportPreparedNext($"Transitioned to prepared next ({(e.IsAutomatic ? "automatic" : "manual")}): {e.Source.DisplayName}");

    private static string FormatPreparedNext(PreparedNextInfo info)
    {
        if (info.State == PreparedNextState.Empty || info.Source is null)
            return "Nothing prepared";

        var name = info.Source.DisplayName;
        return info.State switch
        {
            PreparedNextState.Analyzing => $"{name} · analyzing...",
            PreparedNextState.Ready when info.Analysis is { } analysis =>
                $"{name} · ready ({FormatTime(analysis.EffectiveDuration)}, gain {analysis.NormalizationGain:0.00})",
            PreparedNextState.Ready => $"{name} · ready",
            PreparedNextState.Transitioning => $"{name} · starting...",
            PreparedNextState.Stale => $"{name} · changed on disk (prepare again)",
            _ => name,
        };
    }

    private void ReportPreparedNext(string message) =>
        RunOnUiThread(() => HandleDiagnostic(MediaDiagnosticLevel.Info, "PreparedNext", message));
}
