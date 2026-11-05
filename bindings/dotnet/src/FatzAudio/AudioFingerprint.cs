using System.Globalization;
using FatzAudio.Native;

namespace FatzAudio;

/// <summary>
/// A key for a file's audio that leaves out its tags and its name (<c>fa_fingerprint</c>): the key the
/// loudness cache files songs by. A renamed, moved, copied or retagged file keeps its fingerprint; audio
/// that is encoded again or edited gets a new one, so a library can tell "same song, new tags" from
/// "the audio changed". Two fingerprints are equal when <see cref="Key"/> and <see cref="AudioBytes"/> both are.
/// </summary>
/// <remarks>
/// Coded audio (MP3, AAC, FLAC, Ogg, Opus, M4A, ...) is sampled at 16 points plus its length; WAV and AIFF
/// up to 64 MB of samples are hashed whole. WAV and AIFF over 64 MB are sampled at 64 points plus the
/// file's last-write time, so any write to such a file (a tag edit too) gives a new fingerprint, and the
/// same bytes in memory give another one than the file. A ZIP gives the fingerprint of its audio entry,
/// the same as that entry's bytes. No engine is needed and nothing is decoded; safe from any thread.
/// </remarks>
/// <param name="Key">The hash.</param>
/// <param name="AudioBytes">Bytes of audio it covers (tags and other chunks left out).</param>
public readonly record struct AudioFingerprint(ulong Key, long AudioBytes)
{
    /// <summary>The fingerprint of a file.</summary>
    /// <param name="path">The file.</param>
    /// <returns>Its fingerprint.</returns>
    /// <exception cref="FatzAudioException">The file is missing (<see cref="ErrorCode.NotFound"/>), empty or a ZIP
    /// without audio (<see cref="ErrorCode.UnsupportedFormat"/>), or cannot be read (<see cref="ErrorCode.Io"/>).</exception>
    /// <example>
    /// <code>
    /// var before = AudioFingerprint.Compute(path);
    /// // ... the user edits the tags ...
    /// bool sameAudio = AudioFingerprint.Compute(path) == before;
    /// </code>
    /// </example>
    public static unsafe AudioFingerprint Compute(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        NativeLoader.EnsureInitialized();
        var native = new FaFingerprint { StructSize = (uint)sizeof(FaFingerprint) };
        FatzAudioException.ThrowIfFailed(NativeMethods.fa_fingerprint_path(path, ref native));
        return new AudioFingerprint(native.Key, native.AudioBytes);
    }

    /// <summary>The fingerprint of a file in memory, such as an entry your app unpacked from an archive.</summary>
    /// <param name="data">The file's bytes (only used during the call).</param>
    /// <returns>Its fingerprint.</returns>
    /// <exception cref="ArgumentException"><paramref name="data"/> is empty.</exception>
    /// <exception cref="FatzAudioException">The bytes are a ZIP without audio (<see cref="ErrorCode.UnsupportedFormat"/>).</exception>
    public static unsafe AudioFingerprint Compute(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) throw new ArgumentException("No data.", nameof(data));
        NativeLoader.EnsureInitialized();
        var native = new FaFingerprint { StructSize = (uint)sizeof(FaFingerprint) };
        int rc;
        fixed (byte* p = data) rc = NativeMethods.fa_fingerprint_memory(p, (nuint)data.Length, ref native);
        FatzAudioException.ThrowIfFailed(rc);
        return new AudioFingerprint(native.Key, native.AudioBytes);
    }

    /// <summary>A fixed text form to store: the key as 16 hex digits, a dash, the audio bytes (<c>"00ab...ef-4123456"</c>).</summary>
    /// <returns>The text form.</returns>
    public override string ToString() => Key.ToString("x16", CultureInfo.InvariantCulture) + "-" + AudioBytes.ToString(CultureInfo.InvariantCulture);
}
