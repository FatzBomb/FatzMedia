using System.Collections.Concurrent;
using System.Reflection;
using WinFormsMediaSurface = FatzBomb.MediaControl.WinForms.MediaSurface;

namespace FatzBomb.MediaControl.Tests;

public sealed class WinFormsMediaSurfaceTests
{
    [Fact]
    public void HandleCreation_AttachesPrimaryWin32Surface() => RunSta(() =>
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
        Assert.Equal(surface.Handle, hwnd);
        Assert.Equal((surface.ClientSize.Width, surface.ClientSize.Height), (attached.PixelWidth, attached.PixelHeight));
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

    public class SurfaceRecorder : DispatchProxy
    {
        public ConcurrentQueue<string> Calls { get; } = new();

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
                    break;
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
