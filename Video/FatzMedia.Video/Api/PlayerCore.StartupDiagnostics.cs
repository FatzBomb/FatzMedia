using FatzMedia.Video.Core.Threading;

#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// Diagnostics raised while the player builds its engine (native loading, the audio engine and its output device)
/// come before a host can subscribe to <see cref="MediaDiagnostic"/>. They are kept and handed to each subscriber
/// when it subscribes; after a <c>Reset</c>, the new ones also go to the subscribers already there.
/// </summary>
public partial class PlayerCore
{
    readonly Lock _diagnosticSubscribersGate = new();
    EventHandler<MediaDiagnosticEventArgs>? _mediaDiagnostic;
    MediaDiagnosticEventArgs[] _startupDiagnostics = [];

    public event EventHandler<MediaDiagnosticEventArgs>? MediaDiagnostic
    {
        add
        {
            if (value is null)
                return;

            MediaDiagnosticEventArgs[] startup;
            lock (_diagnosticSubscribersGate)
            {
                _mediaDiagnostic += value;
                startup = _startupDiagnostics;
            }

            PostDiagnostics(value, startup);
        }
        remove
        {
            lock (_diagnosticSubscribersGate)
                _mediaDiagnostic -= value;
        }
    }

    /// <summary>Keeps this player's diagnostics raised until the returned scope ends (building the engine).</summary>
    StartupDiagnosticsCapture CaptureStartupDiagnostics() => new(this);

    void KeepStartupDiagnostics(MediaDiagnosticEventArgs[] startup)
    {
        EventHandler<MediaDiagnosticEventArgs>? subscribers;
        lock (_diagnosticSubscribersGate)
        {
            _startupDiagnostics = startup;
            subscribers = _mediaDiagnostic;
        }

        if (subscribers is not null)
            PostDiagnostics(subscribers, startup);
    }

    sealed class StartupDiagnosticsCapture : IDisposable
    {
        readonly PlayerCore _player;
        readonly Lock _gate = new();
        readonly List<MediaDiagnosticEventArgs> _captured = [];

        public StartupDiagnosticsCapture(PlayerCore player)
        {
            _player = player;
            DiagnosticRouter.DiagnosticOccurred += Collect;
        }

        public void Dispose()
        {
            DiagnosticRouter.DiagnosticOccurred -= Collect;
            MediaDiagnosticEventArgs[] startup;
            lock (_gate)
                startup = [.. _captured];
            _player.KeepStartupDiagnostics(startup);
        }

        void Collect(MediaDiagnosticInfo info)
        {
            if (!ReferenceEquals(info.Owner, _player))
                return;

            lock (_gate)
                _captured.Add(ToEventArgs(info));
        }
    }

    static MediaDiagnosticEventArgs ToEventArgs(MediaDiagnosticInfo info) =>
        new(info.Level, info.Category, info.Message, info.SourcePath, info.InnerException);

    // In order on the player's diagnostic worker, like live diagnostics; a failing handler is not reported as a
    // diagnostic (that would raise it again).
    void PostDiagnostics(EventHandler<MediaDiagnosticEventArgs> handler, IEnumerable<MediaDiagnosticEventArgs> diagnostics)
    {
        foreach (var args in diagnostics)
            _ = _diagnosticEvents.Post(() => HostEventDispatcher.Raise(this, handler, args, nameof(MediaDiagnostic), reportFailures: false));
    }
}
