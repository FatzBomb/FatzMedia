using FatzBomb.MediaControl.Core.Threading;

#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

public partial class MediaControlPlayer
{
    public event EventHandler<MediaValueChangedEventArgs<BaseVisualTransform>>? BaseVisualTransformChanged;

    public BaseVisualTransform BaseVisualTransform
    {
        get => _presentationCoordinator.BaseVisualTransform;
        set
        {
            BaseVisualTransform previous = _presentationCoordinator.BaseVisualTransform;
            if (previous == value)
            {
                return;
            }

            _presentationCoordinator.BaseVisualTransform = value;
            RequestPresentationRefresh();
            HostEventDispatcher.Raise(
                this,
                BaseVisualTransformChanged,
                new MediaValueChangedEventArgs<BaseVisualTransform>(previous, value),
                nameof(BaseVisualTransformChanged));
        }
    }
}
