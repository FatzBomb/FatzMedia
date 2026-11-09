using System.Collections.ObjectModel;
using FatzMedia.Video.Core.Subtitle;

namespace FatzMedia.Video.Sample.Player.Shared.ViewModels;

/// <summary>One entry of the subtitle picker; a null track id turns subtitles off.</summary>
public sealed record SubtitleChoice(string? TrackId, string Label);

/// <summary>
/// Subtitle picker (Sync flyout): Off plus the loaded media's embedded and sidecar tracks,
/// refreshed whenever media loads or closes, whichever path loaded it, and when embedded tracks
/// arrive after the load.
/// </summary>
public partial class PlayerViewModel
{
    private static readonly SubtitleChoice SubtitlesOff = new(null, "Off");
    private bool _refreshingSubtitles;

    public ObservableCollection<SubtitleChoice> SubtitleChoices { get; } = [SubtitlesOff];

    /// <summary>Gets whether the loaded media has any subtitle track to choose.</summary>
    public bool HasSubtitleChoices => SubtitleChoices.Count > 1;

    public SubtitleChoice? SelectedSubtitle
    {
        get;
        set
        {
            if (!SetProperty(ref field, value) || _refreshingSubtitles || value is null || Player is not { } player)
                return;

            if (!player.SelectSubtitleTrack(value.TrackId))
            {
                StatusText = $"Could not select subtitles: {value.Label}";
                RefreshSubtitleChoices();
            }
        }
    } = SubtitlesOff;

    private void StartSubtitleTracking(PlayerCore player)
    {
        player.MediaLoaded += OnSubtitleMediaLoaded;
        player.MediaClosed += OnSubtitleMediaClosed;
        player.SubtitleTracksChanged += OnSubtitleTracksChanged;
    }

    private void StopSubtitleTracking(PlayerCore player)
    {
        player.MediaLoaded -= OnSubtitleMediaLoaded;
        player.MediaClosed -= OnSubtitleMediaClosed;
        player.SubtitleTracksChanged -= OnSubtitleTracksChanged;
    }

    private void OnSubtitleMediaLoaded(object? sender, MediaLoadedEventArgs e) => RunOnUiThread(RefreshSubtitleChoices);

    private void OnSubtitleMediaClosed(object? sender, MediaClosedEventArgs e) => RunOnUiThread(RefreshSubtitleChoices);

    // Embedded tracks are decoded in the background and arrive after MediaLoaded (ENG-039 P5-1).
    private void OnSubtitleTracksChanged(object? sender, SubtitleTracksChangedEventArgs e) => RunOnUiThread(RefreshSubtitleChoices);

    private void RefreshSubtitleChoices()
    {
        _refreshingSubtitles = true;
        try
        {
            SubtitleChoices.Clear();
            SubtitleChoices.Add(SubtitlesOff);
            SubtitleChoice selected = SubtitlesOff;
            if (Player is { IsOpen: true } player)
            {
                var selectedId = player.SelectedSubtitleTrackId;
                foreach (SubtitleTrack track in player.SubtitleTracks)
                {
                    var choice = new SubtitleChoice(track.Id, $"{track.DisplayName} ({(track.IsEmbedded ? "embedded" : "file")})");
                    SubtitleChoices.Add(choice);
                    if (string.Equals(track.Id, selectedId, StringComparison.Ordinal))
                        selected = choice;
                }
            }

            SelectedSubtitle = selected;
        }
        finally
        {
            _refreshingSubtitles = false;
        }

        OnPropertyChanged(nameof(HasSubtitleChoices));
    }
}
