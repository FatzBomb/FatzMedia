using System.Runtime.InteropServices;
using FatzAudio.Native;

namespace FatzAudio.Archives;

/// <summary>The kind of archive file (<c>fa_archive_format</c>).</summary>
public enum ArchiveFormat
{
    /// <summary>Not an archive.</summary>
    None = 0,
    /// <summary>ZIP (including ZIP64).</summary>
    Zip = 1,
    /// <summary>A tar file, plain or inside gzip, bzip2 or xz (<see cref="Archive.Filter"/> says which).</summary>
    Tar = 2,
    /// <summary>One file in a gzip file.</summary>
    GZip = 3,
    /// <summary>One file in a bzip2 file.</summary>
    BZip2 = 4,
    /// <summary>One file in an xz file.</summary>
    Xz = 5,
    /// <summary>A 7-Zip archive (Copy, LZMA and LZMA2 folders are read).</summary>
    SevenZip = 6,
    /// <summary>A RAR archive (recognized only: extract it yourself, then open the song from memory).</summary>
    Rar = 7,
}

/// <summary>What the files of an archive are wrapped in (<c>fa_archive_filter</c>).</summary>
public enum ArchiveFilter
{
    /// <summary>Nothing: a ZIP or a plain tar.</summary>
    None = 0,
    /// <summary>gzip (a .tar.gz, or the single file of a .gz).</summary>
    GZip = 1,
    /// <summary>bzip2 (a .tar.bz2, or the single file of a .bz2).</summary>
    BZip2 = 2,
    /// <summary>xz (a .tar.xz, or the single file of an .xz).</summary>
    Xz = 3,
}

/// <summary>How an archive entry is stored (<c>fa_archive_method</c>).</summary>
public enum ArchiveMethod
{
    /// <summary>Not compressed: it plays straight from the archive.</summary>
    Stored = 0,
    /// <summary>DEFLATE.</summary>
    Deflate = 1,
    /// <summary>Deflate64 (what Windows Explorer writes for ZIPs over 2 GB).</summary>
    Deflate64 = 2,
    /// <summary>bzip2.</summary>
    BZip2 = 3,
    /// <summary>LZMA.</summary>
    Lzma = 4,
    /// <summary>LZMA2.</summary>
    Lzma2 = 5,
    /// <summary>PPMd.</summary>
    PpMd = 6,
    /// <summary>A method this build cannot unpack.</summary>
    Other = 255,
}

/// <summary>What an entry's name says it is (<c>fa_entry_kind</c>); the pairing also looks at the bytes.</summary>
public enum ArchiveEntryKind
{
    /// <summary>Anything else.</summary>
    Other = 0,
    /// <summary>A common audio, tracker or MIDI extension.</summary>
    Audio = 1,
    /// <summary>CD+G graphics (<c>.cdg</c>).</summary>
    Cdg = 2,
    /// <summary>A video extension.</summary>
    Video = 3,
    /// <summary>An image extension.</summary>
    Image = 4,
    /// <summary>A text, lyrics, subtitle or playlist extension.</summary>
    Text = 5,
}

/// <summary>Limits of an <see cref="Archive"/>. All properties are optional.</summary>
public sealed record ArchiveOptions
{
    /// <summary>Compressed entries are unpacked into memory up to this size in MB (default 256 MB). Bigger ones go to
    /// <see cref="TempDirectory"/>, else fail with <see cref="ErrorCode.TooLarge"/>. Stored entries are never copied.</summary>
    public int MemoryLimitMb { get; init; }
    /// <summary>A folder for the files of entries too big for memory (a <c>fatzaudio-tmp</c> folder is made in it; the
    /// files are deleted when closed). Null = no temp files.</summary>
    public string? TempDirectory { get; init; }
    /// <summary>Stops an unpack in progress (<see cref="ErrorCode.Cancelled"/>). Read until the archive is disposed.</summary>
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>One entry of an <see cref="Archive"/>.</summary>
/// <param name="Index">Its place in the archive's list.</param>
/// <param name="Name">UTF-8, '/'-separated, as the archive stores it (a code page 437 name is converted).</param>
/// <param name="Size">Unpacked bytes; -1 when not known before unpacking.</param>
/// <param name="CompressedSize">Packed bytes; -1 inside a solid block.</param>
/// <param name="Method">How it is stored.</param>
/// <param name="Crc32">The archive's own checksum of the entry, if it has one.</param>
/// <param name="Modified">Last write time (UTC); a DOS time is read as UTC. Null when unknown.</param>
/// <param name="Created">Creation time, when the archive stores one.</param>
/// <param name="Accessed">Last access time, when the archive stores one.</param>
/// <param name="IsEncrypted">Encrypted: reading it throws <see cref="ErrorCode.ProtectedContent"/> (no passwords).</param>
/// <param name="IsSolid">Shares a compressed block with other entries.</param>
/// <param name="SolidOffset">Bytes that must be unpacked before this entry can be read (0 if not solid).</param>
/// <param name="IsReadable">This build can unpack it (a supported method, not encrypted).</param>
/// <param name="Kind">What the name says it is.</param>
public sealed record ArchiveEntry(int Index, string Name, long Size, long CompressedSize, ArchiveMethod Method, uint? Crc32,
    DateTimeOffset? Modified, DateTimeOffset? Created, DateTimeOffset? Accessed, bool IsEncrypted, bool IsSolid,
    long SolidOffset, bool IsReadable, ArchiveEntryKind Kind);

/// <summary>
/// An archive (ZIP with ZIP64 and its stored, DEFLATE, Deflate64, bzip2, LZMA and xz entries; tar; gzip, bzip2 or xz around a tar
/// or one file): its list, and its entries read or played
/// without unpacking the others. No engine is needed. The same code opens karaoke downloads (<c>song.mp3</c> +
/// <c>song.cdg</c>) when you play them with <see cref="AudioEngine.OpenFile"/>, so <see cref="Pick"/> says which song
/// they play and which graphics go with it.
/// </summary>
/// <remarks>
/// <para>Only the end of the file and the directory are read when an archive opens. A stored entry is read straight from
/// the archive; a compressed one is unpacked once (an entry of a .tar.gz, .tar.bz2 or .tar.xz is decoded out of the compressed stream:
/// <see cref="ArchiveEntry.SolidOffset"/> says how far back it starts), into memory up to <see cref="ArchiveOptions.MemoryLimitMb"/> and past it
/// into <see cref="ArchiveOptions.TempDirectory"/>, else <see cref="ErrorCode.TooLarge"/>. Every unpack checks the entry's
/// CRC-32 (a mismatch is <see cref="ErrorCode.UnsupportedFormat"/>). Encrypted entries are listed and
/// <see cref="ErrorCode.ProtectedContent"/> when read. Multi-part archives are refused.</para>
/// <para>Which entry plays (<see cref="Pick"/>, and every open, tags read and fingerprint of an archive without a named
/// entry). Names compare with '/' and '\' alike and ignoring case (simple Unicode folding). Ignored: folders, empty
/// entries, <c>__MACOSX/</c>, <c>._*</c> files, <c>Thumbs.db</c> and <c>desktop.ini</c>. A <c>.cdg</c> name is graphics;
/// every other entry whose first 64 KB a decoder recognizes is audio. A song's graphics are the <c>.cdg</c> in the same
/// folder with the same name before the last dot (<c>song.cdg</c> for <c>song.mp3</c>, and <c>song.mp3.cdg</c> too).</para>
/// <list type="number">
/// <item>Audio that has a same-name <c>.cdg</c> comes first, in archive order; the first plays with it.</item>
/// <item>Otherwise the first audio entry plays. Its graphics are the archive's only <c>.cdg</c> when it holds exactly one
/// song and one <c>.cdg</c> (<c>Track01.mp3</c> + <c>SC8125-01.cdg</c>), else none.</item>
/// <item>Only graphics: the first <c>.cdg</c> plays as silence for its length.</item>
/// </list>
/// <para>With a named entry: the exact name first, then ignoring case; the same-name <c>.cdg</c> is attached, else the
/// one-and-one pair of rule 2. "Archive order" is the order of the archive's directory.</para>
/// <para>An <see cref="Archive"/> may be used from several threads; a call waits for the one before it. Dispose it when
/// done; streams opened from it keep what they need.</para>
/// </remarks>
/// <example>
/// <code>
/// using var archive = Archive.Open("Artist - Song.zip");
/// foreach (var e in archive.Entries) Console.WriteLine($"{e.Name} {e.Size} {e.Method}");
/// var (audio, cdg) = archive.Pick();
/// using var song = engine.OpenArchiveEntry(archive, audio);   // its .cdg comes with it
/// byte[] lyrics = archive.ReadAllBytes(archive.Find("lyrics.txt")!);
/// </code>
/// </example>
public sealed unsafe class Archive : IDisposable
{
    private readonly ArchiveHandle _handle;
    private readonly ArchiveEntry[] _entries;

    private Archive(ArchiveHandle handle, ArchiveEntry[] entries)
    {
        _handle = handle;
        _entries = entries;
    }

    internal ArchiveHandle Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            return _handle;
        }
    }

    /// <summary>Which archive format <paramref name="data"/> starts like, from its first bytes (at least 8; a tar file needs 512).</summary>
    /// <param name="data">The start of the file.</param>
    /// <returns>The format; <see cref="ArchiveFormat.None"/> when it is none of them.</returns>
    public static ArchiveFormat Detect(ReadOnlySpan<byte> data)
    {
        NativeLoader.EnsureInitialized();
        fixed (byte* p = data) return (ArchiveFormat)NativeMethods.fa_archive_detect(p, (nuint)data.Length);
    }

    /// <summary>Opens an archive file.</summary>
    /// <param name="path">The file.</param>
    /// <param name="options">Memory limit, temp folder and cancellation; null = defaults.</param>
    /// <returns>The archive; dispose it when done.</returns>
    /// <exception cref="FatzAudioException">The file is missing (<see cref="ErrorCode.NotFound"/>) or not a ZIP this build reads
    /// (<see cref="ErrorCode.UnsupportedFormat"/>; <see cref="Detect"/> tells a RAR from a damaged ZIP).</exception>
    public static Archive Open(string path, ArchiveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        NativeLoader.EnsureInitialized();
        var o = NativeArchiveOptions.Create(options);
        try
        {
            int rc = NativeMethods.fa_archive_open_path(path, o.Pointer, out nint native);
            return Finish(rc, native, null, options, ref o);
        }
        finally { o.Dispose(); }
    }

    /// <summary>Opens an archive held in memory. The bytes are copied.</summary>
    /// <param name="data">The archive's bytes.</param>
    /// <param name="options">Memory limit, temp folder and cancellation; null = defaults.</param>
    /// <returns>The archive; dispose it when done.</returns>
    /// <exception cref="ArgumentException"><paramref name="data"/> is empty.</exception>
    /// <exception cref="FatzAudioException">Not a ZIP this build reads (<see cref="ErrorCode.UnsupportedFormat"/>).</exception>
    public static Archive Open(ReadOnlyMemory<byte> data, ArchiveOptions? options = null)
    {
        if (data.IsEmpty) throw new ArgumentException("No data.", nameof(data));
        NativeLoader.EnsureInitialized();
        var o = NativeArchiveOptions.Create(options);
        try
        {
            using var pin = data.Pin();
            int rc = NativeMethods.fa_archive_open_memory(pin.Pointer, (nuint)data.Length, o.Pointer, out nint native);
            return Finish(rc, native, null, options, ref o);
        }
        finally { o.Dispose(); }
    }

    /// <summary>Opens an archive from a seekable <see cref="Stream"/>.</summary>
    /// <param name="source">A readable, seekable stream. Reads are serialized.</param>
    /// <param name="options">Memory limit, temp folder and cancellation; null = defaults.</param>
    /// <param name="leaveOpen">If false (default), <paramref name="source"/> is disposed with the archive.</param>
    /// <returns>The archive; dispose it when done.</returns>
    /// <exception cref="ArgumentException"><paramref name="source"/> cannot read or seek.</exception>
    /// <exception cref="FatzAudioException">Not a ZIP this build reads (<see cref="ErrorCode.UnsupportedFormat"/>).</exception>
    public static Archive Open(Stream source, ArchiveOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek) throw new ArgumentException("The stream must be readable and seekable.", nameof(source));
        NativeLoader.EnsureInitialized();
        var o = NativeArchiveOptions.Create(options);
        try
        {
            var gc = GCHandle.Alloc(new IoState(source, leaveOpen));
            FaIoCallbacks io = default;
            io.Read = &IoState.Read;
            io.Seek = &IoState.Seek;
            io.Length = &IoState.Length;
            io.Close = &IoState.Close;
            int rc = NativeMethods.fa_archive_open_io(&io, (void*)GCHandle.ToIntPtr(gc), o.Pointer, out nint native);
            if (rc < 0) gc.Free();   // the native call has already closed the stream through the callbacks
            return Finish(rc, native, rc < 0 ? null : gc, options, ref o);
        }
        finally { o.Dispose(); }
    }

    private static Archive Finish(int rc, nint native, GCHandle? io, ArchiveOptions? options, ref NativeArchiveOptions o)
    {
        FatzAudioException.ThrowIfFailed(rc);
        var handle = new ArchiveHandle();
        handle.Initialize(native, io);
        ArchiveEntry[] entries;
        try { entries = ReadEntries(handle); }
        catch { handle.Dispose(); throw; }
        // The native archive reads the cancel flag until it closes (when the last stream read from it does).
        int* flag = o.TakeCancel(out var registration);
        nint flagAddress = (nint)flag;
        if (flag != null || registration != default)
            handle.OnRelease = () => { registration.Dispose(); if (flagAddress != 0) NativeMemory.Free((void*)flagAddress); };
        return new Archive(handle, entries);
    }

    private static ArchiveEntry[] ReadEntries(ArchiveHandle handle)
    {
        uint count = NativeMethods.fa_archive_get_entry_count(handle);
        var entries = new ArchiveEntry[count];
        for (uint i = 0; i < count; i++)
        {
            FaArchiveEntry n = default;
            n.StructSize = (uint)sizeof(FaArchiveEntry);
            FatzAudioException.ThrowIfFailed(NativeMethods.fa_archive_get_entry(handle, i, &n));
            entries[i] = new ArchiveEntry((int)n.Index, NativeText.Read((nint)n.Name), n.Size, n.CompressedSize, (ArchiveMethod)n.Method,
                (n.Flags & 1) != 0 ? n.Crc32 : null, Time(n.ModifiedNs), Time(n.CreatedNs), Time(n.AccessedNs),
                (n.Flags & 2) != 0, (n.Flags & 4) != 0, n.SolidOffset, (n.Flags & 8) != 0, (ArchiveEntryKind)n.Kind);
        }
        return entries;
    }

    private static DateTimeOffset? Time(long ns) =>
        ns == long.MinValue ? null : DateTimeOffset.UnixEpoch.AddTicks(ns / 100);

    /// <summary>The kind of archive this is.</summary>
    public ArchiveFormat Format
    {
        get
        {
            FatzAudioException.ThrowIfFailed(NativeMethods.fa_archive_get_format(Handle, out int f));
            return (ArchiveFormat)f;
        }
    }

    /// <summary>What the files are wrapped in: <see cref="ArchiveFilter.None"/> for a ZIP and a plain tar, else the compression of
    /// a .tar.gz, .tar.bz2 or .tar.xz (<see cref="Format"/> is <see cref="ArchiveFormat.Tar"/>) or of the single file (gz, bz2, xz).</summary>
    public ArchiveFilter Filter
    {
        get
        {
            FatzAudioException.ThrowIfFailed(NativeMethods.fa_archive_get_filter(Handle, out int f));
            return (ArchiveFilter)f;
        }
    }

    /// <summary>Every entry, in archive order (folders are left out).</summary>
    public IReadOnlyList<ArchiveEntry> Entries
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            return _entries;
        }
    }

    /// <summary>The entry with this name: the exact name first, then ignoring case and the kind of slash.</summary>
    /// <param name="name">The entry's name, '/' or '\'-separated.</param>
    /// <returns>The entry, or null when there is none.</returns>
    /// <exception cref="FatzAudioException">Two entries match only by case (<see cref="ErrorCode.InvalidArgument"/>).</exception>
    public ArchiveEntry? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        int rc = NativeMethods.fa_archive_find(Handle, name, out uint index);
        if (rc == (int)ErrorCode.NotFound) return null;
        FatzAudioException.ThrowIfFailed(rc);
        return _entries[index];
    }

    /// <summary>The song an open plays and its graphics, by the rules in the remarks.</summary>
    /// <returns><c>Audio</c> is the song; <c>Cdg</c> its graphics, or null for none. With only graphics, <c>Audio</c> is the
    /// <c>.cdg</c> entry and <c>Cdg</c> is null.</returns>
    /// <exception cref="FatzAudioException">Nothing to play (<see cref="ErrorCode.NotFound"/>), or only encrypted songs
    /// (<see cref="ErrorCode.ProtectedContent"/>).</exception>
    public (ArchiveEntry Audio, ArchiveEntry? Cdg) Pick()
    {
        FatzAudioException.ThrowIfFailed(NativeMethods.fa_archive_pick(Handle, out uint audio, out int cdg));
        return (_entries[audio], cdg >= 0 ? _entries[cdg] : null);
    }

    /// <summary>Reads a whole entry into memory, its CRC checked.</summary>
    /// <param name="entry">An entry of this archive.</param>
    /// <returns>The entry's bytes.</returns>
    /// <exception cref="FatzAudioException">Too big for memory without a temp folder (<see cref="ErrorCode.TooLarge"/>), encrypted
    /// (<see cref="ErrorCode.ProtectedContent"/>), damaged (<see cref="ErrorCode.UnsupportedFormat"/>) or cancelled.</exception>
    public byte[] ReadAllBytes(ArchiveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        int rc = NativeMethods.fa_archive_read(Handle, (uint)entry.Index, out void* data, out nuint size);
        FatzAudioException.ThrowIfFailed(rc);
        try { return new ReadOnlySpan<byte>(data, checked((int)size)).ToArray(); }
        finally { NativeMethods.fa_free(data); }
    }

    /// <summary>
    /// Reads an entry on demand like a file. A stored entry is read straight from the archive; a compressed one is unpacked
    /// on the first read (into memory, or the temp folder), so seeking is free.
    /// </summary>
    /// <param name="entry">An entry of this archive.</param>
    /// <returns>A read-only, seekable stream; dispose it before the archive.</returns>
    /// <exception cref="FatzAudioException">Encrypted or unreadable (<see cref="ErrorCode.ProtectedContent"/>, <see cref="ErrorCode.UnsupportedFormat"/>).</exception>
    public Stream OpenRead(ArchiveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var archive = Handle;
        FatzAudioException.ThrowIfFailed(NativeMethods.fa_archive_open_reader(archive, (uint)entry.Index, out nint native));
        var reader = new ArchiveReaderHandle();
        reader.Initialize(native, archive);
        return new ArchiveEntryStream(reader);
    }

    /// <summary>Releases the archive. Streams read from it with <see cref="OpenRead"/> keep it open until they are disposed.</summary>
    public void Dispose() => _handle.Dispose();
}

/// <summary>A read-only view of one archive entry (<see cref="Archive.OpenRead"/>).</summary>
internal sealed unsafe class ArchiveEntryStream : Stream
{
    private readonly ArchiveReaderHandle _reader;
    private long _position;
    private readonly long _length;

    public ArchiveEntryStream(ArchiveReaderHandle reader)
    {
        _reader = reader;
        _length = NativeMethods.fa_archive_reader_get_size(reader.DangerousGetHandle());
    }

    public override bool CanRead => !_reader.IsClosed;
    public override bool CanSeek => !_reader.IsClosed;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_reader.IsClosed, this);
        if (buffer.IsEmpty) return 0;
        long got;
        bool ok = false;
        _reader.DangerousAddRef(ref ok);
        try
        {
            NativeMethods.fa_archive_reader_seek(_reader.DangerousGetHandle(), _position);
            fixed (byte* p = buffer) got = NativeMethods.fa_archive_reader_read(_reader.DangerousGetHandle(), p, (nuint)buffer.Length);
        }
        finally { _reader.DangerousRelease(); }
        if (got < 0) throw new FatzAudioException((ErrorCode)got);
        _position += got;
        return (int)got;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };
        if (target < 0) throw new IOException("Seek before the start of the entry.");
        _position = Math.Min(target, _length);
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _reader.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Holds the native <c>fa_archive_options</c> for the duration of an open call.</summary>
internal unsafe ref struct NativeArchiveOptions
{
    private FaArchiveOptions* _ptr;
    private byte* _temp;
    private int* _cancel;
    private CancellationTokenRegistration _registration;

    public readonly FaArchiveOptions* Pointer => _ptr;

    public static NativeArchiveOptions Create(ArchiveOptions? options)
    {
        var o = new NativeArchiveOptions();
        if (options is null) return o;
        o._ptr = (FaArchiveOptions*)NativeMemory.AllocZeroed((nuint)sizeof(FaArchiveOptions));
        NativeMethods.fa_archive_options_init(o._ptr);
        o._ptr->MemoryLimitMb = (uint)Math.Max(0, options.MemoryLimitMb);
        if (options.TempDirectory is { } temp)
        {
            o._temp = (byte*)Marshal.StringToCoTaskMemUTF8(temp);
            o._ptr->TempDirectory = o._temp;
        }
        if (options.CancellationToken.CanBeCanceled)
        {
            // The native archive reads this flag until it is closed: the Archive takes it over (TakeCancel).
            o._cancel = (int*)NativeMemory.AllocZeroed(sizeof(int));
            o._ptr->Cancel = o._cancel;
            o._registration = options.CancellationToken.Register(
                static state => Volatile.Write(ref *(int*)(nint)state!, 1), (nint)o._cancel);
        }
        return o;
    }

    /// <summary>The cancel flag and its registration now belong to the caller (the open succeeded).</summary>
    public int* TakeCancel(out CancellationTokenRegistration registration)
    {
        int* flag = _cancel;
        registration = _registration;
        _cancel = null;
        _registration = default;
        return flag;
    }

    public void Dispose()
    {
        _registration.Dispose();
        if (_cancel != null) NativeMemory.Free(_cancel);
        if (_temp != null) Marshal.FreeCoTaskMem((nint)_temp);
        if (_ptr != null) NativeMemory.Free(_ptr);
        _cancel = null;
        _temp = null;
        _ptr = null;
    }
}
