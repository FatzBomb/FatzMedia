namespace FatzMedia.Video.Core.Audio;

/// <summary>
/// Where <see cref="VideoAudioEngine"/>'s decoded audio plays (037A slice 5): FFmpeg demuxes and decodes,
/// the output plays the interleaved float PCM it is given and owns everything after that (tempo, pitch,
/// equalizer, fades, volume, normalization, the device). The FatzAudio engine supplies FatzAudio push and packet
/// streams through <see cref="IPushAudioOutputSource"/>.
/// Only plain PCM crosses: no FFmpeg object reaches the output.
/// </summary>
/// <remarks>
/// The engine calls every member under its own lock, except the normalization event, which the output
/// raises from its own thread. Positions are on the engine's continuous timeline: the media time of the
/// first lap, growing on across gapless loop wraps.
/// </remarks>
interface IPushAudioOutput : IDisposable
{
    /// <summary>Whether a stream exists (between <see cref="Create"/> and <see cref="Release"/>).</summary>
    bool IsCreated { get; }

    /// <summary>Creates the stream for one open, with the current volume, pitch, tempo and equalizer.</summary>
    /// <param name="sampleRate">Rate of the PCM that will be pushed.</param>
    /// <param name="channels">Interleaved channels of that PCM.</param>
    /// <param name="sourcePath">The local file the audio comes from, so the output can recognize the song
    /// (FatzAudio's loudness cache); null for a network source.</param>
    bool Create(int sampleRate, int channels, string? sourcePath);

    /// <summary>
    /// Creates the stream for compressed packets instead of PCM, when the output decodes <paramref name="codec"/>
    /// itself (FatzAudio, when enabled); false otherwise, and the engine decodes with FFmpeg and uses
    /// <see cref="Create"/>. Only plain data crosses: the codec's name, format and configuration bytes.
    /// </summary>
    /// <param name="codec">The track's codec, as FFmpeg's demuxer describes it.</param>
    /// <param name="sourcePath">As for <see cref="Create"/>.</param>
    bool CreateForPackets(PacketCodecInfo codec, string? sourcePath) => false;

    /// <summary>Queues one compressed packet (after <see cref="CreateForPackets"/>).</summary>
    /// <param name="data">The packet's bytes.</param>
    /// <param name="continuousStartSeconds">Where the packet starts on the continuous timeline.</param>
    /// <param name="durationSeconds">How long it plays; 0 when unknown.</param>
    bool PushPacket(ReadOnlySpan<byte> data, double continuousStartSeconds, double durationSeconds) => false;

    /// <summary>Stops and frees the stream and ends any normalization work for it.</summary>
    void Release();

    /// <summary>Queues interleaved float PCM.</summary>
    /// <param name="interleavedFloat">The samples' bytes.</param>
    /// <param name="continuousStartSeconds">Where the first frame sits on the continuous timeline.</param>
    /// <returns>Whether anything was queued.</returns>
    bool Push(ReadOnlySpan<byte> interleavedFloat, double continuousStartSeconds);

    /// <summary>Starts or resumes playback.</summary>
    bool Play();

    /// <summary>Resumes after <see cref="Restart"/> of a playing stream.</summary>
    bool Resume();

    bool Pause();

    /// <summary>
    /// Drops everything queued: the timeline starts again at <paramref name="positionBaseSeconds"/>, where
    /// the next pushed audio begins. <paramref name="stop"/> stops (Stop) instead of pausing (a seek).
    /// </summary>
    void Restart(double positionBaseSeconds, bool stop);

    /// <summary>
    /// Where playback is on the continuous timeline, before output latency (<see cref="ToHeardPosition"/>);
    /// <paramref name="positionBaseSeconds"/> is the timeline's start since the last <see cref="Restart"/>.
    /// </summary>
    double GetContinuousPosition(double positionBaseSeconds);

    /// <summary>A position as heard now: less the device's output latency where the output does not include it.</summary>
    double ToHeardPosition(double seconds);

    void SetVolume(double volume01);

    void SlideVolume(double volume01, TimeSpan duration);

    void SetPitch(int semitones);

    void SetTempo(int percent);

    AudioEqualizerSettings Equalizer { get; }

    bool SetEqualizer(AudioEqualizerSettings settings);

    int BeginFadeSession();

    bool SetFadeGain(int session, double gain, TimeSpan ramp);

    /// <summary>Ends the fade session and restores full gain (a natural end, a loop wrap, a stop).</summary>
    void EndFadeSession();

    bool Normalize { get; set; }

    double NormalizationGain { get; }

    bool IsNormalizationReady { get; }

    /// <summary>Raised when the normalization gain is known or changes; never on the engine's lock.</summary>
    event EventHandler<double>? NormalizationGainChanged;

    /// <summary>Starts measuring a local file's loudness for normalization (after <see cref="Create"/>).</summary>
    /// <param name="filePath">The file the audio comes from (a temp copy for an archive entry).</param>
    /// <param name="cacheKey">The key of the file's measured gain, or null.</param>
    /// <param name="durationSeconds">The file's duration, 0 when unknown.</param>
    /// <param name="temporaryCopy">The file is a copy the engine deletes when the song closes.</param>
    void StartNormalization(string filePath, string? cacheKey, double durationSeconds, bool temporaryCopy);

}

/// <summary>A compressed audio track as FFmpeg's demuxer describes it: plain data, no FFmpeg object.</summary>
/// <param name="Codec">FFmpeg's codec name (<c>aac</c>, <c>mp3</c>, <c>ac3</c>, ...).</param>
/// <param name="SampleRate">Samples per second.</param>
/// <param name="Channels">Channel count.</param>
/// <param name="ExtraData">The codec configuration (an AAC config, an Opus header), or null.</param>
/// <param name="BlockAlign">Block alignment, 0 when none.</param>
/// <param name="BitsPerCodedSample">Bits per coded sample, 0 when unknown.</param>
/// <param name="BitRate">Bits per second, 0 when unknown.</param>
sealed record PacketCodecInfo(string Codec, int SampleRate, int Channels, byte[]? ExtraData, int BlockAlign, int BitsPerCodedSample, long BitRate);

/// <summary>
/// An audio engine whose device also plays video soundtracks (037A slice 5): video audio, Audio-mode
/// soundtracks and network audio through FFmpeg go to its outputs.
/// </summary>
interface IPushAudioOutputSource
{
    /// <summary>A new output on the engine's device; the caller owns it.</summary>
    IPushAudioOutput CreatePushAudioOutput();
}
