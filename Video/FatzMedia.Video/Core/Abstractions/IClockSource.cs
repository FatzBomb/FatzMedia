namespace FatzMedia.Video.Core.Abstractions;

/// <summary>
/// Internal transport clock for media that has no audio stream to provide timing.
/// </summary>
interface IClockSource
{
    double DurationSeconds { get; set; }
    double PositionSeconds { get; }
    bool IsRunning { get; }
    int TempoPercent { get; }

    void Start(double? positionSeconds = null);
    void Pause();
    void Stop();
    void Seek(double positionSeconds);
    void SetTempo(int tempoPercent);
}

/// <summary>
/// Exposes the clock-backed transport to internal controllers without treating it as audio.
/// </summary>
interface IClockBackedMediaEngine
{
    IClockSource ClockSource { get; }
}
