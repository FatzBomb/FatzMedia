namespace FatzMedia.Video.Core.Graphics.D3D.Presentation;

/// <summary>
/// Lightweight view of a live presentation backend participating in the current
/// compositor target fan-out.
/// </summary>
readonly record struct D3D11PresentationBackendTarget(
    string TargetId,
    ID3D11PresentationBackend Backend,
    IntPtr NativeHwnd,
    string BackendName);
