using System.Collections.Concurrent;
using System.Reflection;

namespace FatzBomb.MediaControl.Avalonia.Tests;

/// <summary>
/// Records adapter calls on <see cref="IMediaControlSession"/> without a media engine. Methods
/// returning <see cref="bool"/> succeed; everything else returns its default.
/// </summary>
public class RecordingSession : DispatchProxy
{
    public ConcurrentQueue<string> Calls { get; } = new();

    public MediaPresentationSurfaceDescriptor? LastAttached { get; private set; }

    public (int Width, int Height)? LastResize { get; private set; }

    public VideoDisplayMode? LastDisplayMode { get; private set; }

    public static (IMediaControlSession Session, RecordingSession Recorder) Create()
    {
        IMediaControlSession session = Create<IMediaControlSession, RecordingSession>();
        return (session, (RecordingSession)(object)session);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        switch (targetMethod.Name)
        {
            case nameof(IMediaControlSession.AttachPresentationSurface):
                LastAttached = (MediaPresentationSurfaceDescriptor)args![0]!;
                Calls.Enqueue($"attach {LastAttached.PixelWidth}x{LastAttached.PixelHeight}");
                break;
            case nameof(IMediaControlSession.ResizePresentationSurface):
                LastResize = ((int)args![1]!, (int)args[2]!);
                Calls.Enqueue($"resize {LastResize.Value.Width}x{LastResize.Value.Height}");
                break;
            case nameof(IMediaControlSession.DetachPresentationSurface):
                Calls.Enqueue("detach");
                break;
            case "set_VideoDisplayMode":
                LastDisplayMode = (VideoDisplayMode)args![0]!;
                break;
        }

        Type returnType = targetMethod.ReturnType;
        return returnType == typeof(bool)
            ? true
            : returnType == typeof(void) || !returnType.IsValueType ? null : Activator.CreateInstance(returnType);
    }
}
