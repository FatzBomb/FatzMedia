namespace FatzBomb.MediaControl.Core.Graphics.Composition.D3D11;

/// <summary>
/// Stable description of a compositor output target. This intentionally stores
/// target metadata rather than owning the backend texture so Stage 10L can reason
/// about future multiple outputs without changing current presentation ownership.
/// </summary>
internal readonly record struct D3D11CompositionTargetDescriptor(
    string Id,
    D3D11CompositionTargetKind Kind,
    int Width,
    int Height,
    string BackendName,
    long Revision)
{
    public const string PrimaryFinalTargetId = "primary-final";

    public bool IsValid => !string.IsNullOrWhiteSpace(Id)
        && Kind != D3D11CompositionTargetKind.None
        && Width > 0
        && Height > 0;

    public override string ToString()
        => IsValid
            ? $"{Id}/{Kind}/{BackendName} {Width}x{Height} rev={Revision}"
            : $"{Id}/{Kind}/invalid";
}
