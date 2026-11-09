namespace FatzMedia.Video.Core.Abstractions;

/// <summary>
/// Optional audio-engine capability: moves the channel volume to a new level with a short native
/// linear ramp instead of a step, as <see cref="IAudioEngine.SetVolume"/> does. Like
/// <see cref="IAudioEngine.SetVolume"/>, the level replaces the user volume the engine holds.
/// </summary>
internal interface IAudioVolumeSlide
{
    void SlideVolume(double volume01, TimeSpan duration);
}
