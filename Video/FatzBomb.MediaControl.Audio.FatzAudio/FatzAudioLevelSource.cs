using FatzBomb.MediaControl.Core.Audio;
using Fa = global::FatzAudio;

namespace FatzBomb.MediaControl.Audio.FatzAudio;

/// <summary>
/// <see cref="IAudioLevelSource"/> on a FatzAudio decode-only stream (037A slice 2): the sample peak
/// of what <c>fa_stream_read</c> returns, so normalization measures FatzAudio's decode with our peak
/// rule (<see cref="AudioNormalizer"/>). FatzAudio reports no levels for decode-only streams, so the
/// peak is computed here. The source owns the stream.
/// </summary>
internal sealed class FatzAudioLevelSource : IAudioLevelSource, IDisposable
{
    private readonly Fa.AudioStream _stream;
    private readonly int _sampleRate;
    private readonly int _channels;
    private float[] _buffer = [];

    public FatzAudioLevelSource(Fa.AudioStream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        Fa.AudioFormat format = stream.Info.Format;
        _sampleRate = Math.Max(1, format.SampleRate);
        _channels = Math.Max(1, format.Channels);
    }

    public string LastError { get; private set; } = string.Empty;

    public bool TryReadPeak(float seconds, out double peak)
    {
        peak = 0;
        var wanted = (int)Math.Clamp(Math.Ceiling(seconds * (double)_sampleRate), 1, _sampleRate * 10);
        if (_buffer.Length < wanted * _channels)
            _buffer = new float[wanted * _channels];

        try
        {
            var total = 0;
            while (total < wanted)
            {
                var frames = _stream.Read(_buffer.AsSpan(0, (wanted - total) * _channels));
                if (frames <= 0)
                    break;

                foreach (var sample in _buffer.AsSpan(0, frames * _channels))
                    peak = Math.Max(peak, Math.Abs(sample));
                total += frames;
            }

            return total > 0;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    public bool TryGetDuration(out double seconds)
    {
        try
        {
            seconds = _stream.Info.Duration?.TotalSeconds ?? 0;
            return seconds > 0 && double.IsFinite(seconds);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            seconds = 0;
            return false;
        }
    }

    public bool TryGetPosition(out double seconds)
    {
        try
        {
            seconds = _stream.Position.TotalSeconds;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            seconds = double.NaN;
            return false;
        }
    }

    public bool TrySeek(double seconds)
    {
        try
        {
            _stream.Position = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    public bool TrySeekToStart() => TrySeek(0);

    public void Dispose() => _stream.Dispose();
}
