using FatzBomb.MediaControl.Controllers;
using FatzBomb.MediaControl.Core.Abstractions;

namespace FatzBomb.MediaControl.Tests;

/// <summary>Archive service for tests that never open archives.</summary>
internal sealed class NotSupportedArchiveService : IArchiveService
{
    public IArchiveHandle Open(string path) => throw new NotSupportedException();
}

/// <summary>Presentation coordinator that accepts every call and attaches nothing.</summary>
internal sealed class NullPresentationCoordinator : IMediaPresentationCoordinator
{
    public event EventHandler? FramePresented { add { } remove { } }

    public VideoDisplayMode VideoDisplayMode { get; set; }
    public BaseVisualTransform BaseVisualTransform { get; set; }

    public bool AttachPresentationSurface(MediaPresentationSurfaceDescriptor surface) => false;
    public bool ResizePresentationSurface(string surfaceId, int pixelWidth, int pixelHeight) => false;
    public bool SetPresentationSurfaceLiveResize(string surfaceId, bool isResizing) => false;
    public bool DetachPresentationSurface(string surfaceId) => false;
    public void RefreshCurrentPosition() { }
    public void RunOnRenderThread(Action action) => action();
    public void StartRendering() { }
    public void StopRendering() { }
    public void RefreshAttachments() { }
    public void Dispose() { }
}

/// <summary>Service provider that resolves a <see cref="TestMediaEngine"/> and a not-supported archive service.</summary>
internal sealed class TestServiceProvider(TestMediaEngine engine) : IServiceProvider
{
    private readonly IArchiveService _archiveService = new NotSupportedArchiveService();

    public object? GetService(Type serviceType) => serviceType == typeof(IMediaEngine)
        ? engine
        : serviceType == typeof(IArchiveService)
            ? _archiveService
            : serviceType == typeof(IMediaFileAnalyzer)
                ? engine.Analyzer
                : null;
}
