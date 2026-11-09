#pragma warning disable IDE0130
namespace FatzMedia.Video;

public partial class PlayerCore
{
    public bool ResetPitchOnMediaLoad
    {
        get => File.ResetPitchOnMediaLoad;
        set => File.ResetPitchOnMediaLoad = value;
    }

    public bool ResetTempoOnMediaLoad
    {
        get => File.ResetTempoOnMediaLoad;
        set => File.ResetTempoOnMediaLoad = value;
    }
}
