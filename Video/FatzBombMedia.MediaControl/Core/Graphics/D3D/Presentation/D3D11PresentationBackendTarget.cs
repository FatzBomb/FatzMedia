namespace FatzBombMedia.MediaControl.Core.Graphics.D3D.Presentation;

/// <summary>
/// Lightweight view of a live presentation backend participating in the current
/// compositor target fan-out.
/// </summary>
internal readonly record struct D3D11PresentationBackendTarget(
    string TargetId,
    ID3D11PresentationBackend Backend,
    IntPtr NativeHwnd,
    string BackendName);
