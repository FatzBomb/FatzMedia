using System.Collections.Concurrent;
using System.Reflection;
using FatzBomb.MediaControl.Core.Abstractions;
using FatzBomb.MediaControl.Core.Configuration;
using WinFormsMediaSurface = FatzBomb.MediaControl.WinForms.MediaSurface;

namespace FatzBomb.MediaControl.Tests;

public sealed class WinFormsMediaSurfaceTests
{
    [Fact]
    public void HandleCreation_AttachesItsChildWindowAsThePrimaryWin32Surface() => RunSta(() =>
    {
        (IMediaControlSession session, SurfaceRecorder recorder) = SurfaceRecorder.Create();
        using var form = new System.Windows.Forms.Form { ClientSize = new System.Drawing.Size(320, 180) };
        using var surface = new WinFormsMediaSurface { Dock = System.Windows.Forms.DockStyle.Fill, Player = session };
        form.Controls.Add(surface);

        form.CreateControl();
        _ = surface.Handle;

        Assert.True(surface.IsPrimarySurfaceAttached);
        MediaPresentationSurfaceDescriptor attached = recorder.LastAttached!;
        Assert.Equal(MediaPresentationSurfaceRole.Primary, attached.Role);
        Assert.Equal(MediaNativeSurfaceTypeIds.Win32Hwnd, attached.NativeSurface.SurfaceTypeId);
        Assert.True(attached.NativeSurface.TryGetHandle(MediaNativeHandleIds.Window, out nint hwnd));
        // Stage 046A: the renderer presents into a child window the surface can hide.
        Assert.Equal(surface.PresentationHandle, hwnd);
        Assert.NotEqual(surface.Handle, hwnd);
        Assert.Equal((surface.ClientSize.Width, surface.ClientSize.Height), (attached.PixelWidth, attached.PixelHeight));
        Assert.Equal(MediaSurfacePresentationPath.NativeWindow, surface.PresentationPath);
    });

    [Fact]
    public void FormResizeLoop_SendsTheLiveResizeHint() => RunSta(() =>
    {
        (IMediaControlSession session, SurfaceRecorder recorder) = SurfaceRecorder.Create();
        using var form = new ResizeLoopForm { ClientSize = new System.Drawing.Size(320, 180) };
        using var surface = new WinFormsMediaSurface { Dock = System.Windows.Forms.DockStyle.Fill, Player = session };
        form.Controls.Add(surface);
        form.CreateControl();
        _ = surface.Handle;

        form.BeginResizeLoop();
        form.EndResizeLoop();

        Assert.Equal(["primary on", "primary off"], recorder.LiveResizeCalls);
    });

    [Fact]
    public void AudioMode_HidesThePresentationWindowUntilTheSessionLeavesIt() => RunSta(() =>
    {
        (IMediaControlSession session, SurfaceRecorder recorder) = SurfaceRecorder.Create();
        using var surface = new WinFormsMediaSurface { Size = new System.Drawing.Size(64, 64), Player = session };
        _ = surface.Handle;
        Assert.True(surface.IsPresentationWindowVisible);

        recorder.SwitchMode(MediaPlayerMode.Audio);
        Assert.True(surface.IsPresentationSuspended);
        Assert.False(surface.IsPresentationWindowVisible);
        Assert.True(surface.IsPrimarySurfaceAttached);

        recorder.SwitchMode(MediaPlayerMode.Full);
        Assert.False(surface.IsPresentationSuspended);
        Assert.True(surface.IsPresentationWindowVisible);
    });

    [Fact]
    public void AutoHide_ShowsFromTheFirstFrameUntilStop_AndOffShowsAlways() => RunSta(() =>
    {
        (IMediaControlSession session, SurfaceRecorder recorder) = SurfaceRecorder.Create();
        recorder.IsOpen = false;
        recorder.HasVisualContent = false;
        using var surface = new WinFormsMediaSurface { Size = new System.Drawing.Size(64, 64), Player = session };
        var changes = 0;
        surface.ContentVisibilityChanged += (_, _) => changes++;
        _ = surface.Handle;
        Assert.False(surface.IsPresentationWindowVisible);

        recorder.IsOpen = true;
        recorder.HasVisualContent = true;
        recorder.Raise(nameof(IMediaControlSession.MediaLoaded), new MediaLoadedEventArgs(new MediaSourceInfo(MediaInputSource.FromFile("clip.mp4"), MediaContent.Video), TimeSpan.Zero));
        recorder.Raise(nameof(IMediaControlSession.PresentationFramePresented), EventArgs.Empty);
        Assert.True(surface.IsContentVisible);
        Assert.True(surface.IsPresentationWindowVisible);

        recorder.Raise(nameof(IMediaControlSession.MediaStopped), EventArgs.Empty);
        Assert.False(surface.IsContentVisible);
        Assert.False(surface.IsPresentationWindowVisible);
        Assert.Equal(2, changes);

        surface.AutoHide = false;
        Assert.True(surface.IsPresentationWindowVisible);
    });

    [Fact]
    public void RefusedAttach_RaisesOneWarningOwnedByTheSession() => RunSta(() =>
    {
        (IMediaControlSession session, SurfaceRecorder recorder) = SurfaceRecorder.Create();
        recorder.AttachResult = false;
        var warnings = new ConcurrentQueue<MediaDiagnosticInfo>();
        void Record(MediaDiagnosticInfo info)
        {
            if (info.Level == MediaDiagnosticLevel.Warning && info.Category == WinFormsMediaSurface.DiagnosticCategory && ReferenceEquals(info.Owner, session))
                warnings.Enqueue(info);
        }

        DiagnosticRouter.DiagnosticOccurred += Record;
        try
        {
            using var surface = new WinFormsMediaSurface { Size = new System.Drawing.Size(64, 64), Player = session };
            _ = surface.Handle;
            surface.Size = new System.Drawing.Size(80, 64);

            Assert.False(surface.IsSurfaceAttached);
            Assert.Equal(MediaSurfacePresentationPath.None, surface.PresentationPath);
            Assert.Single(warnings);
        }
        finally
        {
            DiagnosticRouter.DiagnosticOccurred -= Record;
        }
    });

    [Fact]
    public void Resize_ThenDispose_ResizesAndDetaches() => RunSta(() =>
    {
        (IMediaControlSession session, SurfaceRecorder recorder) = SurfaceRecorder.Create();
        var surface = new WinFormsMediaSurface { Size = new System.Drawing.Size(200, 100), Player = session };
        _ = surface.Handle;

        surface.Size = new System.Drawing.Size(400, 300);
        surface.Dispose();

        Assert.Equal(["attach 200x100", "resize 400x300", "detach"], recorder.Calls);
    });

    [Fact]
    public void PlayerChange_DetachesOldSession() => RunSta(() =>
    {
        (IMediaControlSession first, SurfaceRecorder firstRecorder) = SurfaceRecorder.Create();
        (IMediaControlSession second, SurfaceRecorder secondRecorder) = SurfaceRecorder.Create();
        using var surface = new WinFormsMediaSurface { Size = new System.Drawing.Size(64, 64), Player = first };
        _ = surface.Handle;

        surface.Player = second;

        Assert.Equal(["attach 64x64", "detach"], firstRecorder.Calls);
        Assert.Equal(["attach 64x64"], secondRecorder.Calls);
    });

    [Fact]
    public void ProjectorRole_AttachesAsTheSecondarySurfaceWithoutTouchingTheDisplayMode() => RunSta(() =>
    {
        (IMediaControlSession session, SurfaceRecorder recorder) = SurfaceRecorder.Create();
        using var surface = new WinFormsMediaSurface
        {
            Size = new System.Drawing.Size(96, 54),
            Role = MediaPresentationSurfaceRole.Projector,
            VideoDisplayMode = VideoDisplayMode.Stretch,
            Player = session,
        };
        _ = surface.Handle;

        Assert.True(surface.IsSurfaceAttached);
        Assert.False(surface.IsPrimarySurfaceAttached);
        MediaPresentationSurfaceDescriptor attached = recorder.LastAttached!;
        Assert.Equal(MediaPresentationSurfaceRole.Projector, attached.Role);
        Assert.Equal(WinFormsMediaSurface.DefaultSecondarySurfaceId, attached.Id);
        Assert.Equal(0, recorder.VideoDisplayModeSets);
    });

    [Fact]
    public void RoleChangeToPrimary_ReattachesAsPrimaryAndSendsTheDisplayMode() => RunSta(() =>
    {
        (IMediaControlSession session, SurfaceRecorder recorder) = SurfaceRecorder.Create();
        using var surface = new WinFormsMediaSurface
        {
            Size = new System.Drawing.Size(64, 64),
            Role = MediaPresentationSurfaceRole.Projector,
            VideoDisplayMode = VideoDisplayMode.Stretch,
            Player = session,
        };
        _ = surface.Handle;

        surface.Role = MediaPresentationSurfaceRole.Primary;

        Assert.True(surface.IsPrimarySurfaceAttached);
        Assert.Equal(MediaPresentationSurfaceDescriptor.PrimarySurfaceId, recorder.LastAttached!.Id);
        Assert.Equal(1, recorder.VideoDisplayModeSets);
    });

    [Fact]
    public void SurfaceIdChange_ReattachesAndDetachesTheIdItAttachedWith() => RunSta(() =>
    {
        (IMediaControlSession session, SurfaceRecorder recorder) = SurfaceRecorder.Create();
        using var surface = new WinFormsMediaSurface { Size = new System.Drawing.Size(64, 64), Role = MediaPresentationSurfaceRole.Projector, Player = session };
        _ = surface.Handle;

        surface.SurfaceId = "screen-2";
        surface.Player = null;

        Assert.Equal(["attach 64x64", "detach", "attach 64x64", "detach"], recorder.Calls);
        Assert.Equal([WinFormsMediaSurface.DefaultSecondarySurfaceId, "screen-2"], recorder.DetachedIds);
        Assert.Equal("screen-2", surface.EffectiveSurfaceId);
    });

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA WinForms test thread did not complete.");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class ResizeLoopForm : System.Windows.Forms.Form
    {
        public void BeginResizeLoop() => OnResizeBegin(EventArgs.Empty);

        public void EndResizeLoop() => OnResizeEnd(EventArgs.Empty);
    }

    public class SurfaceRecorder : DispatchProxy
    {
        private readonly ConcurrentDictionary<string, Delegate?> _handlers = new();

        public ConcurrentQueue<string> Calls { get; } = new();

        public ConcurrentQueue<string> LiveResizeCalls { get; } = new();

        public bool AttachResult { get; set; } = true;

        public bool IsOpen { get; set; } = true;

        public bool HasVisualContent { get; set; } = true;

        public MediaPlayerMode Mode { get; set; }

        public void SwitchMode(MediaPlayerMode mode)
        {
            MediaPlayerMode previous = Mode;
            Mode = mode;
            Raise(nameof(IMediaControlSession.ModeChanged), new MediaValueChangedEventArgs<MediaPlayerMode>(previous, mode));
        }

        public void Raise(string eventName, object args)
        {
            if (_handlers.TryGetValue(eventName, out Delegate? handler) && handler is not null)
                _ = handler.DynamicInvoke(this, args);
        }

        public MediaPresentationSurfaceDescriptor? LastAttached { get; private set; }

        public ConcurrentQueue<string> DetachedIds { get; } = new();

        public int VideoDisplayModeSets;

        public static (IMediaControlSession Session, SurfaceRecorder Recorder) Create()
        {
            IMediaControlSession session = Create<IMediaControlSession, SurfaceRecorder>();
            return (session, (SurfaceRecorder)(object)session);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            switch (targetMethod.Name)
            {
                case nameof(IMediaControlSession.AttachPresentationSurface):
                    LastAttached = (MediaPresentationSurfaceDescriptor)args![0]!;
                    Calls.Enqueue($"attach {LastAttached.PixelWidth}x{LastAttached.PixelHeight}");
                    return AttachResult;
                case nameof(IMediaControlSession.SetPresentationSurfaceLiveResize):
                    LiveResizeCalls.Enqueue($"{args![0]} {((bool)args[1]! ? "on" : "off")}");
                    break;
                case "get_IsOpen":
                    return IsOpen;
                case "get_HasVisualContent":
                    return HasVisualContent;
                case "get_Mode":
                    return Mode;
                case "get_HasPresentation":
                    return Mode != MediaPlayerMode.Audio;
                case var add when add.StartsWith("add_", StringComparison.Ordinal):
                    _ = _handlers.AddOrUpdate(add[4..], _ => (Delegate)args![0]!, (_, existing) => Delegate.Combine(existing, (Delegate)args![0]!));
                    return null;
                case var remove when remove.StartsWith("remove_", StringComparison.Ordinal):
                    _ = _handlers.AddOrUpdate(remove[7..], _ => null, (_, existing) => Delegate.Remove(existing, (Delegate)args![0]!));
                    return null;
                case nameof(IMediaControlSession.ResizePresentationSurface):
                    Calls.Enqueue($"resize {args![1]}x{args[2]}");
                    break;
                case nameof(IMediaControlSession.DetachPresentationSurface):
                    Calls.Enqueue("detach");
                    DetachedIds.Enqueue((string)args![0]!);
                    break;
                case "set_" + nameof(IMediaControlSession.VideoDisplayMode):
                    _ = Interlocked.Increment(ref VideoDisplayModeSets);
                    break;
            }

            Type returnType = targetMethod.ReturnType;
            return returnType == typeof(bool)
                ? true
                : returnType == typeof(void) || !returnType.IsValueType ? null : Activator.CreateInstance(returnType);
        }
    }
}
