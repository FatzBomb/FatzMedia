using System.Collections.Concurrent;
using System.Reflection;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;

namespace FatzMedia.Video.Avalonia.Tests;

/// <summary>
/// Records adapter calls on <see cref="IPlayerSession"/> without a media engine. Methods
/// returning <see cref="bool"/> succeed; everything else returns its default.
/// </summary>
public class RecordingSession : DispatchProxy
{
    public ConcurrentQueue<string> Calls { get; } = new();

    public MediaPresentationSurfaceDescriptor? LastAttached { get; private set; }

    /// <summary>Gets every attached descriptor, in order.</summary>
    public ConcurrentQueue<MediaPresentationSurfaceDescriptor> Attached { get; } = new();

    /// <summary>Decides what attach returns (default: accept).</summary>
    public Func<MediaPresentationSurfaceDescriptor, bool>? AttachResult { get; set; }

    public (int Width, int Height)? LastResize { get; private set; }

    public VideoDisplayMode? LastDisplayMode { get; private set; }

    /// <summary>Gets every attach, resize and detach with its surface id ("attach secondary 640x360").</summary>
    public ConcurrentQueue<string> SurfaceCalls { get; } = new();

    /// <summary>Gets or sets what <see cref="IPlayerSession.Mode"/> returns.</summary>
    public MediaPlayerMode Mode { get; set; }

    /// <summary>
    /// Gets or sets what <see cref="IPlayerSession.IsOpen"/> returns (default true: media with
    /// visual content is loaded, so an auto-hiding surface shows the frames).
    /// </summary>
    public bool IsOpen { get; set; } = true;

    /// <summary>Gets or sets what <see cref="IPlayerSession.HasVisualContent"/> returns (default true).</summary>
    public bool HasVisualContent { get; set; } = true;

    /// <summary>Gets or sets what <see cref="IPlayerSession.State"/> returns.</summary>
    public MediaPlayerState State { get; set; } = MediaPlayerState.Stopped;

    readonly ConcurrentDictionary<string, Delegate?> _handlers = new();

    /// <summary>Raises a session event (by name) to its subscribers, as the session would.</summary>
    public void Raise(string eventName, object args)
    {
        if (_handlers.TryGetValue(eventName, out var handler) && handler is not null)
            _ = handler.DynamicInvoke(this, args);
    }

    EventHandler<MediaValueChangedEventArgs<MediaPlayerMode>>? _modeChanged;

    /// <summary>Gets how many <see cref="IPlayerSession.ModeChanged"/> handlers are subscribed.</summary>
    public int ModeChangedSubscribers => _modeChanged?.GetInvocationList().Length ?? 0;

    /// <summary>Sets <see cref="Mode"/> and raises ModeChanged, as a session's control queue would.</summary>
    public void SwitchMode(MediaPlayerMode mode)
    {
        var old = Mode;
        Mode = mode;
        _modeChanged?.Invoke(this, new MediaValueChangedEventArgs<MediaPlayerMode>(old, mode));
    }

    public static (IPlayerSession Session, RecordingSession Recorder) Create()
    {
        var session = Create<IPlayerSession, RecordingSession>();
        return (session, (RecordingSession)(object)session);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        switch (targetMethod.Name)
        {
            case nameof(IPlayerSession.AttachPresentationSurface):
                LastAttached = (MediaPresentationSurfaceDescriptor)args![0]!;
                Attached.Enqueue(LastAttached);
                Calls.Enqueue($"attach {LastAttached.PixelWidth}x{LastAttached.PixelHeight}");
                SurfaceCalls.Enqueue($"attach {LastAttached.Id} {LastAttached.PixelWidth}x{LastAttached.PixelHeight}");
                if (AttachResult is { } attachResult)
                    return attachResult(LastAttached);
                break;
            case nameof(IPlayerSession.ResizePresentationSurface):
                LastResize = ((int)args![1]!, (int)args[2]!);
                Calls.Enqueue($"resize {LastResize.Value.Width}x{LastResize.Value.Height}");
                SurfaceCalls.Enqueue($"resize {args[0]} {LastResize.Value.Width}x{LastResize.Value.Height}");
                break;
            case nameof(IPlayerSession.DetachPresentationSurface):
                Calls.Enqueue("detach");
                SurfaceCalls.Enqueue($"detach {args![0]}");
                break;
            case "get_Mode":
                return Mode;
            case "add_ModeChanged":
                _modeChanged += (EventHandler<MediaValueChangedEventArgs<MediaPlayerMode>>)args![0]!;
                return null;
            case "remove_ModeChanged":
                _modeChanged -= (EventHandler<MediaValueChangedEventArgs<MediaPlayerMode>>)args![0]!;
                return null;
            case "set_VideoDisplayMode":
                LastDisplayMode = (VideoDisplayMode)args![0]!;
                break;
            case "get_IsOpen":
                return IsOpen;
            case "get_HasVisualContent":
                return HasVisualContent;
            case "get_State":
                return State;
            case "get_HasPresentation":
                return Mode != MediaPlayerMode.Audio;
            case var add when add.StartsWith("add_", StringComparison.Ordinal):
                _ = _handlers.AddOrUpdate(add[4..], _ => (Delegate)args![0]!, (_, existing) => Delegate.Combine(existing, (Delegate)args![0]!));
                return null;
            case var remove when remove.StartsWith("remove_", StringComparison.Ordinal):
                _ = _handlers.AddOrUpdate(remove[7..], _ => null, (_, existing) => Delegate.Remove(existing, (Delegate)args![0]!));
                return null;
        }

        var returnType = targetMethod.ReturnType;
        return returnType == typeof(bool)
            ? true
            : returnType == typeof(void) || !returnType.IsValueType ? null : Activator.CreateInstance(returnType);
    }
}
