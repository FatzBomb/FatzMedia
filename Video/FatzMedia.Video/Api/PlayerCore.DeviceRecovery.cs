using System.Diagnostics;
using FatzMedia.Video.Controllers;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Threading;

#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// ENG-039: after the presentation coordinator recovered a lost GPU device, video decoded on the
/// lost device is gone (a hardware decoder, or the device the renderer adopted from it), so the
/// open video is reopened once, at its position and in its transport state, as a mode switch does.
/// </summary>
public partial class PlayerCore
{
    const string DeviceRecoveryDiagnosticCategory = "DeviceRecovery";

    // Set while a reopen is queued: several recoveries before it runs reopen once.
    int _deviceRecoveryReopenQueued;

    // Raised on the coordinator's recovery thread.
    void OnPresentationDeviceRecovered(object? sender, EventArgs e)
    {
        if (_disposed || Interlocked.Exchange(ref _deviceRecoveryReopenQueued, 1) != 0)
            return;

        var queued = _controlOperations.Enqueue(ReopenAfterDeviceRecovery);
        if (queued.IsFaulted)
            Volatile.Write(ref _deviceRecoveryReopenQueued, 0);
    }

    // Raised on the coordinator's recovery thread, once per failed recovery (ENG-050): the host decides
    // whether to recreate the player with another renderer.
    void OnPresentationDeviceRecoveryFailed(object? sender, EventArgs e)
    {
        if (_disposed || MediaError is not { } handler)
            return;

        var args = new MediaErrorEventArgs(
            MediaErrorCode.RendererDeviceLost,
            "The GPU device was lost and the renderer could not recover it; no picture until the player is recreated with another renderer (sound continues).");
        _ = _controlOperations.Enqueue(() => HostEventDispatcher.Raise(this, handler, args, nameof(MediaError)));
    }

    /// <summary>On the control queue: reopens the open media when it shows video (its own, or a video background).</summary>
    void ReopenAfterDeviceRecovery()
    {
        Volatile.Write(ref _deviceRecoveryReopenQueued, 0);
        if (_disposed || _sessionGraphFreed || File.CurrentLoad is not { } load || !ShowsVideo())
            return;

        using var diagnosticOwner = DiagnosticRouter.EnterOwner(this);
        try
        {
            DiagnosticRouter.Raise(
                MediaDiagnosticLevel.Info,
                DeviceRecoveryDiagnosticCategory,
                $"Reopening {load.Source.DisplayName} after the GPU device was recovered.");
            ReopenMedia(CaptureModeSwitchState(), DeviceRecoveryDiagnosticCategory, "The GPU device recovery");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlayerCore] Reopening after a device recovery failed: {ex.Message}");
            DiagnosticRouter.Raise(
                MediaDiagnosticLevel.Warning,
                DeviceRecoveryDiagnosticCategory,
                $"Reopening {load.Source.DisplayName} after the GPU device recovery failed: {ex.Message}",
                innerException: ex);
        }
    }

    bool ShowsVideo() =>
        _mediaEngine.Content.HasFlag(MediaContent.Video)
        || _mediaEngine.Background is { Kind: MediaBackgroundKind.Video };
}
