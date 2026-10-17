using FatzBomb.MediaControl.Core.Files;
using ManagedBass;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MBass = ManagedBass.Bass;

namespace FatzBomb.MediaControl.Core.Audio;

/// <summary>
/// An audio file decoded by FFmpeg into a 32-bit float WAV that BASS plays natively, held in
/// memory or, above <see cref="FfmpegAudioFallback.MaxInMemoryBytes"/>, in a temp file deleted
/// on dispose. Disposing must follow freeing every stream created from it.
/// </summary>
internal sealed class ConvertedAudio(string sourceIdentity, byte[]? data, long length, string? tempPath) : IDisposable
{
    public string SourceIdentity { get; } = sourceIdentity;

    public long Length { get; } = length;

    internal bool IsInMemory => data is not null;

    internal ReadOnlyMemory<byte> InMemoryData => data is null ? ReadOnlyMemory<byte>.Empty : data.AsMemory(0, (int)Length);

    internal string? TempPath => tempPath;

    /// <summary>Creates a BASS stream over the converted WAV.</summary>
    public int CreateStream(BassFlags flags) => data is not null
        ? MBass.CreateStream(data, 0, Length, flags)
        : MBass.CreateStream(tempPath!, Flags: flags);

    public void Dispose()
    {
        if (tempPath is not null)
            TempMediaFile.TryDelete(tempPath);
    }
}

/// <summary>
/// Plays audio formats no BASS plugin claims (Stage 033A slice 2): FFmpeg decodes the file to a
/// float WAV, which opens through <see cref="AudioEngine"/>'s normal stream path, so the file
/// gets every plain-audio feature (seek, loop, tempo, equalizer, fades, normalization,
/// trimming, prepared-next). One conversion made for the prepared-next item is kept so the
/// transition does not convert again.
/// </summary>
internal static class FfmpegAudioFallback
{
    internal const long DefaultMaxInMemoryBytes = 160L * 1024 * 1024;

    // RIFF sizes are 32-bit; the header below is 58 bytes.
    private const long MaxWavDataBytes = uint.MaxValue - FloatWav.HeaderBytes;

    private static readonly Lock Gate = new();
    private static ConvertedAudio? _prepared;

    /// <summary>Converted WAVs larger than this go to a temp file instead of memory.</summary>
    internal static long MaxInMemoryBytes { get; set; } = DefaultMaxInMemoryBytes;

    /// <summary>True when no BASS plugin claims the file's extension but FFmpeg can decode it.</summary>
    public static bool RequiresConversion(string path)
    {
        var extension = Path.GetExtension(path);
        return !FileTypes.Audio.Contains(extension) && FileTypes.FfmpegAudio.Contains(extension);
    }

    /// <summary>True when FFmpeg can try a file BASS failed to open (for example ALAC in .m4a).</summary>
    public static bool CanRetryWithFfmpeg(string path) => FileTypes.FfmpegAudio.Contains(Path.GetExtension(path));

    /// <summary>
    /// Converts <paramref name="path"/> ahead of time and keeps the result for
    /// <see cref="Take"/>, replacing any earlier prepared conversion. The returned object stays
    /// owned by the fallback; callers must not dispose it.
    /// </summary>
    public static ConvertedAudio? Prepare(string path, CancellationToken cancellationToken)
    {
        var identity = CreateIdentity(path);
        lock (Gate)
        {
            if (_prepared?.SourceIdentity == identity)
                return _prepared;
        }

        ConvertedAudio? converted = Convert(path, identity, cancellationToken);
        if (converted is null)
            return null;

        ConvertedAudio? replaced;
        lock (Gate)
        {
            replaced = _prepared;
            _prepared = converted;
        }

        replaced?.Dispose();
        return converted;
    }

    /// <summary>
    /// Returns the prepared conversion of <paramref name="path"/> when it is still current,
    /// otherwise converts now. The caller owns and disposes the result.
    /// </summary>
    public static ConvertedAudio? Take(string path, CancellationToken cancellationToken)
    {
        var identity = CreateIdentity(path);
        lock (Gate)
        {
            if (_prepared?.SourceIdentity == identity)
            {
                ConvertedAudio prepared = _prepared;
                _prepared = null;
                return prepared;
            }
        }

        return Convert(path, identity, cancellationToken);
    }

    private static ConvertedAudio? Convert(string path, string identity, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using FfmpegAudioReader? reader = FfmpegAudioReader.Open(path);
        if (reader is null)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "AudioFallback", $"FFmpeg found no decodable audio in '{Path.GetFileName(path)}'.", sourcePath: path);
            return null;
        }

        Stream output = new MemoryStream();
        string? tempPath = null;
        try
        {
            output.Write(new byte[FloatWav.HeaderBytes]);
            long dataBytes = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadOnlySpan<float> samples = reader.ReadFrame();
                if (samples.IsEmpty)
                    break;

                // WAV is little-endian, as is every platform the engine runs on.
                ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(samples);
                if (dataBytes + bytes.Length > MaxWavDataBytes)
                {
                    DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "AudioFallback", $"'{Path.GetFileName(path)}' is too long to convert (WAV is limited to 4 GB).", sourcePath: path);
                    return Discard(output, tempPath);
                }

                if (output is MemoryStream memory && memory.Length + bytes.Length > MaxInMemoryBytes)
                {
                    tempPath = TempMediaFile.NewPath(".wav");
                    var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                    memory.WriteTo(file);
                    memory.Dispose();
                    output = file;
                }

                output.Write(bytes);
                dataBytes += bytes.Length;
            }

            if (dataBytes == 0)
                return Discard(output, tempPath);

            Span<byte> header = stackalloc byte[FloatWav.HeaderBytes];
            FloatWav.WriteHeader(header, reader.SampleRate, reader.Channels, dataBytes);
            output.Position = 0;
            output.Write(header);

            ConvertedAudio converted = output is MemoryStream buffer
                ? new ConvertedAudio(identity, buffer.GetBuffer(), buffer.Length, tempPath: null)
                : new ConvertedAudio(identity, data: null, output.Length, tempPath);
            output.Dispose();
            DiagnosticRouter.Raise(
                MediaDiagnosticLevel.Info,
                "AudioFallback",
                $"Decoded '{Path.GetFileName(path)}' with FFmpeg in {stopwatch.ElapsedMilliseconds} ms ({converted.Length / (1024 * 1024)} MB, {(converted.IsInMemory ? "memory" : "temp file")}).",
                sourcePath: path);
            return converted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "AudioFallback", $"Converting '{Path.GetFileName(path)}' failed: {ex.Message}", sourcePath: path, innerException: ex);
            return Discard(output, tempPath);
        }
        catch
        {
            _ = Discard(output, tempPath);
            throw;
        }
    }

    private static ConvertedAudio? Discard(Stream output, string? tempPath)
    {
        output.Dispose();
        if (tempPath is not null)
            TempMediaFile.TryDelete(tempPath);
        return null;
    }

    private static string CreateIdentity(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return path;
        }
    }
}

/// <summary>
/// Header of a 32-bit IEEE float WAV: RIFF, an 18-byte <c>fmt </c> chunk
/// (<c>WAVE_FORMAT_IEEE_FLOAT</c>), a <c>fact</c> chunk, then the <c>data</c> chunk header.
/// </summary>
internal static class FloatWav
{
    public const int HeaderBytes = 12 + 8 + 18 + 8 + 4 + 8;

    private const ushort WaveFormatIeeeFloat = 3;
    private const int BytesPerSample = sizeof(float);

    public static void WriteHeader(Span<byte> header, int sampleRate, int channels, long dataBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(header.Length, HeaderBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfNegative(dataBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dataBytes, uint.MaxValue - HeaderBytes);

        var blockAlign = channels * BytesPerSample;
        var offset = 0;
        WriteTag(header, ref offset, "RIFF");
        WriteUInt32(header, ref offset, (uint)(HeaderBytes - 8 + dataBytes));
        WriteTag(header, ref offset, "WAVE");

        WriteTag(header, ref offset, "fmt ");
        WriteUInt32(header, ref offset, 18);
        WriteUInt16(header, ref offset, WaveFormatIeeeFloat);
        WriteUInt16(header, ref offset, (ushort)channels);
        WriteUInt32(header, ref offset, (uint)sampleRate);
        WriteUInt32(header, ref offset, (uint)(sampleRate * blockAlign));
        WriteUInt16(header, ref offset, (ushort)blockAlign);
        WriteUInt16(header, ref offset, BytesPerSample * 8);
        WriteUInt16(header, ref offset, 0);

        WriteTag(header, ref offset, "fact");
        WriteUInt32(header, ref offset, 4);
        WriteUInt32(header, ref offset, (uint)(dataBytes / blockAlign));

        WriteTag(header, ref offset, "data");
        WriteUInt32(header, ref offset, (uint)dataBytes);
    }

    private static void WriteTag(Span<byte> header, ref int offset, string tag)
    {
        foreach (var character in tag)
            header[offset++] = (byte)character;
    }

    private static void WriteUInt16(Span<byte> header, ref int offset, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(header[offset..], value);
        offset += sizeof(ushort);
    }

    private static void WriteUInt32(Span<byte> header, ref int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(header[offset..], value);
        offset += sizeof(uint);
    }
}
