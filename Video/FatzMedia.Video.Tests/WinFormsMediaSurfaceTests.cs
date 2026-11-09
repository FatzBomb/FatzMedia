using System.Collections.Concurrent;
using System.Reflection;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;
using WinFormsMediaSurface = FatzMedia.Video.WinForms.MediaSurface;

namespace FatzMedia.Video.Tests;

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
        recorder.Raise(nameof(IMediaControlSession.PresentationFramePresented), EventArgs.Empty);
        Assert.True(surface.IsPresentationWindowVisible);

        recorder.SwitchMode(MediaPlayerMode.Audio);
        Assert.True(surface.IsPresentationSuspended);
        Assert.False(surface.IsPresentationWindowVisible);
        Assert.True(surface.IsPrimarySurfaceAttached);

        // Back in Full mode the new renderer's first frame shows the child window again.
        recorder.SwitchMode(MediaPlayerMode.Full);
        Assert.False(surface.IsPresentationSuspended);
        Assert.False(surface.IsPresentationWindowVisible);
        recorder.Raise(nameof(IMediaControlSession.PresentationFramePresented), EventArgs.Empty);
        Assert.True(surface.IsPresentationWindowVisible);
    });

    [Fact]
    public void AutoHideOff_KeepsTheChildWindowHiddenUntilTheFirstFrame() => RunSta(() =>
    {
        // The child window has no pixels until the renderer presents: BackColor shows meanwhile.
        (IMediaControlSession session, SurfaceRecorder recorder) = SurfaceRecorder.Create();
        using var surface = new WinFormsMediaSurface { Size = new System.Drawing.Size(64, 64), AutoHide = false, Player = session };
        _ = surface.Handle;
        Assert.True(surface.IsPrimarySurfaceAttached);
        Assert.False(surface.IsPresentationWindowVisible);

        recorder.Raise(nameof(IMediaControlSession.PresentationFramePresented), EventArgs.Empty);
        Assert.True(surface.IsPresentationWindowVisible);

        // Stop keeps the last frame with auto-hide off.
        recorder.Raise(nameof(IMediaControlSession.MediaStopped), EventArgs.Empty);
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

    [Fact]
    public void RendererWithoutWindowSupport_GetsAFrameSink_AndItsFramesArePainted() => RunSta(() =>
    {
        (IMediaControlSession session, SurfaceRecorder recorder) = SurfaceRecorder.Create();
        recorder.AttachFilter = static surface => surface.NativeSurface.SurfaceTypeId == MediaNativeSurfaceTypeIds.ManagedBgraFrameSink;
        using var surface = new WinFormsMediaSurface { Size = new System.Drawing.Size(4, 2), AutoHide = false, Player = session };
        _ = surface.Handle;

        Assert.Equal(["attach 4x2", "attach 4x2"], recorder.Calls);
        Assert.Equal(MediaSurfacePresentationPath.FrameSink, surface.PresentationPath);
        Assert.Contains("did not accept the window handle", surface.PresentationPathReason, StringComparison.Ordinal);
        Assert.False(surface.IsPresentationWindowVisible);
        Assert.True(surface.AreFramesShown);

        // A frame from the render thread is published on the UI thread.
        byte[] pixels = [.. Enumerable.Repeat<byte[]>([0x10, 0x20, 0x30, 0xFF], 8).SelectMany(static pixel => pixel)];
        System.Threading.Tasks.Task.Run(() => surface.FrameSink!.PresentFrame(pixels, 4, 2, 16)).Wait();
        PumpUntil(() => surface.FrameSink!.PublishedFrames == 1);
        System.Drawing.Color pixel = surface.FrameSink!.Bitmap!.GetPixel(3, 1);
        Assert.Equal((0x30, 0x20, 0x10), (pixel.R, pixel.G, pixel.B));

        // Audio mode drops the frame; stop detaches and frees the sink.
        recorder.SwitchMode(MediaPlayerMode.Audio);
        Assert.Null(surface.FrameSink!.Bitmap);
        Assert.False(surface.AreFramesShown);
        surface.Player = null;
        Assert.Null(surface.FrameSink);
        Assert.Equal(MediaSurfacePresentationPath.None, surface.PresentationPath);
    });

    [Fact]
    public void FrameSink_FirstFrameReady_RaisesOncePerLoadWithTheFramePublished() => RunSta(() =>
    {
        // Stage 051A: on the UI thread, once per load, after the frame is in the sink's bitmap.
        (IMediaControlSession session, SurfaceRecorder recorder) = SurfaceRecorder.Create();
        using var surface = new WinFormsMediaSurface { Size = new System.Drawing.Size(4, 2), PreferNativeWindow = false, Player = session };
        _ = surface.Handle;
        var published = new List<long>();
        surface.FirstFrameReady += (_, _) =>
        {
            Assert.False(surface.InvokeRequired);
            published.Add(surface.FrameSink!.PublishedFrames);
        };
        byte[] pixels = new byte[4 * 2 * 4];
        void Present()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                surface.FrameSink!.PresentFrame(pixels, 4, 2, 16);
                recorder.Raise(nameof(IMediaControlSession.PresentationFramePresented), EventArgs.Empty);
            }).Wait();
            var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(300);
            while (DateTime.UtcNow < deadline)
            {
                System.Windows.Forms.Application.DoEvents();
                Thread.Sleep(5);
            }
        }

        Present();
        Present();
        Assert.Single(published);
        Assert.True(published[0] >= 1);

        var source = new MediaSourceInfo(MediaInputSource.FromFile("clip.mp4"), MediaContent.Video);
        recorder.Raise(nameof(IMediaControlSession.MediaClosed), new MediaClosedEventArgs(source));
        recorder.Raise(nameof(IMediaControlSession.MediaLoaded), new MediaLoadedEventArgs(source, TimeSpan.Zero));
        Present();
        Assert.Equal(2, published.Count);
        surface.Player = null;
    });

    [Fact]
    public void PreferNativeWindowOff_AttachesOnlyAFrameSink() => RunSta(() =>
    {
        (IMediaControlSession session, SurfaceRecorder recorder) = SurfaceRecorder.Create();
        using var surface = new WinFormsMediaSurface { Size = new System.Drawing.Size(64, 64), PreferNativeWindow = false, Player = session };
        _ = surface.Handle;

        Assert.Equal(["attach 64x64"], recorder.Calls);
        Assert.Equal(MediaNativeSurfaceTypeIds.ManagedBgraFrameSink, recorder.LastAttached!.NativeSurface.SurfaceTypeId);
        Assert.Equal(MediaSurfacePresentationPath.FrameSink, surface.PresentationPath);
        Assert.Equal("PreferNativeWindow is off", surface.PresentationPathReason);

        // Turning it on re-attaches the window.
        surface.PreferNativeWindow = true;
        Assert.Equal(MediaSurfacePresentationPath.NativeWindow, surface.PresentationPath);
        Assert.Equal(MediaNativeSurfaceTypeIds.Win32Hwnd, recorder.LastAttached!.NativeSurface.SurfaceTypeId);
    });

    [Fact]
    public void OverlayContent_ShowsInALayeredWindowOwnedByTheForm_AndIsNeverDisposed() => RunSta(() =>
    {
        (IMediaControlSession session, SurfaceRecorder _) = SurfaceRecorder.Create();
        using var form = new System.Windows.Forms.Form { ClientSize = new System.Drawing.Size(320, 180), ShowInTaskbar = false };
        using var surface = new WinFormsMediaSurface { Dock = System.Windows.Forms.DockStyle.Fill, Player = session };
        var overlay = new System.Windows.Forms.Label { Text = "overlay", BackColor = System.Drawing.Color.Transparent };
        form.Controls.Add(surface);
        form.Show();
        surface.OverlayContent = overlay;

        System.Windows.Forms.Form window = surface.OverlayWindow!;
        Assert.Same(form, window.Owner);
        Assert.True(window.Visible);
        Assert.Same(window, overlay.Parent);
        Assert.Equal(surface.RectangleToScreen(surface.ClientRectangle), window.Bounds);
        Assert.Equal(window.BackColor, window.TransparencyKey);

        // It follows the form and hides with the surface.
        form.Location = new System.Drawing.Point(form.Location.X + 40, form.Location.Y + 20);
        Assert.Equal(surface.RectangleToScreen(surface.ClientRectangle), window.Bounds);
        surface.Visible = false;
        Assert.False(window.Visible);
        surface.Visible = true;
        Assert.True(window.Visible);

        surface.OverlayContent = null;
        Assert.Null(surface.OverlayWindow);
        Assert.Null(overlay.Parent);
        Assert.False(overlay.IsDisposed);

        surface.OverlayContent = overlay;
        form.Close();
        Assert.False(overlay.IsDisposed);
        overlay.Dispose();
    });

    [Fact]
    public void FromBitmap_CopiesStraightAlphaBgra() => RunSta(() =>
    {
        using var bitmap = new System.Drawing.Bitmap(2, 1, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        bitmap.SetPixel(0, 0, System.Drawing.Color.FromArgb(0x80, 0x10, 0x20, 0x30));
        bitmap.SetPixel(1, 0, System.Drawing.Color.FromArgb(0xFF, 0x40, 0x50, 0x60));

        MediaInputSource source = FatzMedia.Video.WinForms.WinFormsMediaInputSources.FromBitmap(bitmap, "test");

        Assert.True(source.IsBgraImage);
        Assert.Equal((2, 1, 8), (source.Width, source.Height, source.Stride));
        Assert.Equal([0x30, 0x20, 0x10, 0x80, 0x60, 0x50, 0x40, 0xFF], source.BgraPixels);
        Assert.Equal("test", source.DisplayName);
    });

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not met in time.");
            System.Windows.Forms.Application.DoEvents();
            Thread.Sleep(5);
        }
    }

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

        /// <summary>Decides attach per descriptor when set (overrides <see cref="AttachResult"/>).</summary>
        public Func<MediaPresentationSurfaceDescriptor, bool>? AttachFilter { get; set; }

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
                    return AttachFilter?.Invoke(LastAttached) ?? AttachResult;
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
