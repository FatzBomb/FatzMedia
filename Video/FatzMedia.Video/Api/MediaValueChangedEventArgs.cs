#pragma warning disable IDE0130
namespace FatzMedia.Video;

public sealed class MediaValueChangedEventArgs<T> : EventArgs
{
    public MediaValueChangedEventArgs(T previousValue, T currentValue)
        : this(previousValue, currentValue, MediaChangeReason.User)
    {
    }

    public MediaValueChangedEventArgs(T previousValue, T currentValue, MediaChangeReason reason)
    {
        PreviousValue = previousValue;
        CurrentValue = currentValue;
        Reason = reason;
    }

    public T PreviousValue { get; }
    public T CurrentValue { get; }
    public MediaChangeReason Reason { get; }
}
