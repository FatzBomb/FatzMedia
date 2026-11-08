namespace FatzBomb.MediaControl.Core.Graphics.Composition.Planning;

public readonly record struct CompositionTargetDescriptor(
    string Id,
    CompositionTargetKind Kind,
    int Width,
    int Height,
    string BackendName,
    long Revision)
{
    public const string PrimaryFinalTargetId = "primary-final";

    public bool IsValid => !string.IsNullOrWhiteSpace(Id)
        && Kind != CompositionTargetKind.None
        && Width > 0
        && Height > 0;

    public override string ToString()
        => IsValid
            ? $"{Id}/{Kind}/{BackendName} {Width}x{Height} rev={Revision}"
            : $"{Id}/{Kind}/invalid";
}
