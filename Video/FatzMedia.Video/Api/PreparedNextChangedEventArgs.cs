#pragma warning disable IDE0130
namespace FatzMedia.Video;

/// <summary>
/// Describes a prepared-next slot transition.
/// </summary>
public sealed class PreparedNextChangedEventArgs(PreparedNextInfo previous, PreparedNextInfo current) : EventArgs
{
    /// <summary>Gets the slot snapshot before the change.</summary>
    public PreparedNextInfo Previous { get; } = previous ?? throw new ArgumentNullException(nameof(previous));

    /// <summary>Gets the slot snapshot after the change.</summary>
    public PreparedNextInfo Current { get; } = current ?? throw new ArgumentNullException(nameof(current));
}
