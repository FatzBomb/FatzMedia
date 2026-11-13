using System.Runtime.InteropServices;

namespace FatzMedia.Audio.Native;

/// <summary>Owns an <c>fa_engine*</c>. Children (devices, streams) add a reference so the engine outlives them.</summary>
sealed class EngineHandle : SafeHandle
{
    public EngineHandle() : base(0, true) { }
    public override bool IsInvalid => handle == 0;

    protected override bool ReleaseHandle()
    {
        NativeMethods.fa_engine_destroy(handle);
        return true;
    }
}

/// <summary>Owns an <c>fa_device*</c> and keeps its engine alive.</summary>
sealed class DeviceHandle : SafeHandle
{
    EngineHandle? _engine;
    readonly List<GCHandle> _pins = [];

    public DeviceHandle() : base(0, true) { }
    public override bool IsInvalid => handle == 0;

    public void Initialize(nint native, EngineHandle engine)
    {
        _engine = HandleRef.AddRef(engine);
        SetHandle(native);
    }

    /// <summary>Keeps a callback state object alive until the native device is closed.</summary>
    public GCHandle Pin(object state)
    {
        var h = GCHandle.Alloc(state);
        lock (_pins) _pins.Add(h);
        return h;
    }

    /// <summary>Frees a pinned state early (a callback registration that failed).</summary>
    public void Unpin(GCHandle h)
    {
        lock (_pins)
        {
            if (_pins.Remove(h)) h.Free();
        }
    }

    protected override bool ReleaseHandle()
    {
        NativeMethods.fa_device_close(handle);
        lock (_pins)
        {
            foreach (var h in _pins) h.Free();
            _pins.Clear();
        }
        _engine?.DangerousRelease();
        _engine = null;
        return true;
    }
}

/// <summary>
/// Owns an <c>fa_mic*</c> of a DJ mixer and keeps the mixer's device open, so the mic is always removed before the
/// mixer goes (<c>fa_mixer_destroy</c> would free it).
/// </summary>
sealed class MicHandle : ChildHandle<DeviceHandle>
{
    protected override void Free(nint native) => NativeMethods.fa_mic_remove(native);
}

/// <summary>A surface whose native object goes with its stream (<see cref="StreamHandle.TrackSurface"/>).</summary>
interface IStreamSurface
{
    /// <summary>Called right before the native stream is closed: destroy the native surface.</summary>
    void Orphan();
}

/// <summary>
/// Owns an <c>fa_stream*</c>, the GC handles of its callbacks, and references to the engine and
/// the attached device so that native teardown always happens in a safe order
/// (stream first, then device, then engine).
/// </summary>
sealed class StreamHandle : SafeHandle
{
    EngineHandle? _engine;
    DeviceHandle? _device;
    readonly List<GCHandle> _pins = [];

    public StreamHandle() : base(0, true) { }
    public override bool IsInvalid => handle == 0;

    public void Initialize(nint native, EngineHandle engine)
    {
        _engine = HandleRef.AddRef(engine);
        SetHandle(native);
    }

    /// <summary>Keeps a callback state object alive until the native stream is closed.</summary>
    public GCHandle Pin(object state)
    {
        var h = GCHandle.Alloc(state);
        lock (_pins) _pins.Add(h);
        return h;
    }

    /// <summary>Takes ownership of an already allocated GC handle (freed when the stream closes).</summary>
    public void Adopt(GCHandle h)
    {
        lock (_pins) _pins.Add(h);
    }

    public void Unpin(GCHandle h)
    {
        lock (_pins)
        {
            if (_pins.Remove(h)) h.Free();
        }
    }

    public void AttachDevice(DeviceHandle device)
    {
        var previous = _device;
        _device = HandleRef.AddRef(device);
        previous?.DangerousRelease();
    }

    /// <summary>Releases the device reference taken by <see cref="AttachDevice"/>, so a detached
    /// stream no longer keeps its old device open.</summary>
    public void DetachDevice()
    {
        var previous = _device;
        _device = null;
        previous?.DangerousRelease();
    }

    readonly List<IStreamSurface> _surfaces = [];

    /// <summary>Registers a surface (CD+G or lyrics): native surfaces are freed with the stream.</summary>
    public void TrackSurface(IStreamSurface surface)
    {
        lock (_surfaces) _surfaces.Add(surface);
    }

    public void UntrackSurface(IStreamSurface surface)
    {
        lock (_surfaces) _surfaces.Remove(surface);
    }

    Sound? _voiceOf;
    int _voiceClosing;

    /// <summary>Makes this a voice of <paramref name="sound"/>, which then learns when the voice's close starts
    /// and when it is over (<see cref="Sound.VoiceClosing"/>, <see cref="Sound.VoiceClosed"/>).</summary>
    public void SetVoiceOf(Sound sound) => _voiceOf = sound;

    protected override void Dispose(bool disposing)
    {
        // Before the handle counts as closed: from here on the sound does not hand this voice out again, even
        // while the native close waits for a call still running on the handle.
        if (_voiceOf is { } sound && Interlocked.Exchange(ref _voiceClosing, 1) == 0 && !IsInvalid)
            sound.VoiceClosing(handle);
        base.Dispose(disposing);
    }

    protected override bool ReleaseHandle()
    {
        // Surfaces the app did not dispose are destroyed with the stream.
        lock (_surfaces)
        {
            foreach (var s in _surfaces) s.Orphan();
            _surfaces.Clear();
        }
        NativeMethods.fa_stream_close(handle);
        _voiceOf?.VoiceClosed(handle);
        lock (_pins)
        {
            foreach (var h in _pins) h.Free();
            _pins.Clear();
        }
        _device?.DangerousRelease();
        _device = null;
        _owner?.DangerousRelease();
        _owner = null;
        _engine?.DangerousRelease();
        _engine = null;
        return true;
    }

    SafeHandle? _owner;

    /// <summary>Keeps the object that owns the native stream (a sound, for its voices) open until the stream is
    /// closed.</summary>
    public void SetOwner(SafeHandle owner) => _owner = HandleRef.AddRef(owner);
}

/// <summary>Owns an <c>fa_bus*</c> and keeps its device (and so its engine) alive.</summary>
sealed class BusHandle : ChildHandle<DeviceHandle>
{
    protected override void Free(nint native) => NativeMethods.fa_bus_destroy(native);
}

/// <summary>Owns an <c>fa_disc*</c>. Streams opened from the disc hold their own native reference.</summary>
sealed class DiscHandle : SafeHandle
{
    public DiscHandle() : base(0, true) { }
    public override bool IsInvalid => handle == 0;

    public void Initialize(nint native) => SetHandle(native);

    protected override bool ReleaseHandle()
    {
        NativeMethods.fa_disc_close(handle);
        return true;
    }
}

/// <summary>Owns one reference to an <c>fa_soundfont*</c>. Engines and streams using it hold their own native references.</summary>
sealed class SoundFontHandle : SafeHandle
{
    public SoundFontHandle() : base(0, true) { }
    public override bool IsInvalid => handle == 0;

    protected override bool ReleaseHandle()
    {
        NativeMethods.fa_soundfont_close(handle);
        return true;
    }
}

/// <summary>Owns an <c>fa_silence_detector*</c>.</summary>
sealed class SilenceDetectorHandle : SafeHandle
{
    public SilenceDetectorHandle() : base(0, true) { }
    public override bool IsInvalid => handle == 0;

    public void Initialize(nint native) => SetHandle(native);

    protected override bool ReleaseHandle()
    {
        NativeMethods.fa_silence_detector_free(handle);
        return true;
    }
}

/// <summary>Owns an <c>fa_sound*</c> and keeps its engine alive. Its voices' stream handles hold a reference to
/// it, so the sound is closed only after every voice wrapper let go (<c>fa_sound_close</c> closes the voices left).</summary>
sealed class SoundHandle : ChildHandle<EngineHandle>
{
    protected override void Free(nint native) => NativeMethods.fa_sound_close(native);
}

/// <summary>Owns an <c>fa_scan_feed*</c> and keeps its engine alive.</summary>
sealed class ScanHandle : ChildHandle<EngineHandle>
{
    protected override void Free(nint native) => NativeMethods.fa_scan_close(native);
}

/// <summary>A native object that needs its parent (engine, device) open until it is closed: the parent gets a
/// reference on <see cref="Initialize"/> and lets go after the child's own close function ran.</summary>
abstract class ChildHandle<TParent> : SafeHandle where TParent : SafeHandle
{
    TParent? _parent;

    protected ChildHandle() : base(0, true) { }
    public override bool IsInvalid => handle == 0;

    public void Initialize(nint native, TParent parent)
    {
        _parent = HandleRef.AddRef(parent);
        SetHandle(native);
    }

    /// <summary>Calls the native close function.</summary>
    protected abstract void Free(nint native);

    protected override bool ReleaseHandle()
    {
        Free(handle);
        _parent?.DangerousRelease();
        _parent = null;
        return true;
    }
}

static class HandleRef
{
    public static T AddRef<T>(T handle) where T : SafeHandle
    {
        var success = false;
        handle.DangerousAddRef(ref success);
        ObjectDisposedException.ThrowIf(!success, typeof(T));
        return handle;
    }
}

/// <summary>
/// Owns an <c>fa_encoder*</c> and the GC handle of its output callbacks. Releasing it completes the file
/// (<c>fa_encoder_close</c>); the result is kept in <see cref="CloseResult"/>.
/// </summary>
sealed class EncoderHandle : SafeHandle
{
    GCHandle _output;

    public EncoderHandle() : base(0, true) { }
    public override bool IsInvalid => handle == 0;

    /// <summary>What <c>fa_encoder_close</c> returned, once released.</summary>
    public int CloseResult { get; private set; }

    volatile bool _released;

    /// <summary>True once <c>fa_encoder_close</c> has run.</summary>
    public bool IsReleased => _released;

    public void Initialize(nint native, GCHandle output)
    {
        _output = output;
        SetHandle(native);
    }

    protected override bool ReleaseHandle()
    {
        CloseResult = NativeMethods.fa_encoder_close(handle);
        if (_output.IsAllocated) _output.Free();
        _released = true;
        return true;
    }
}


/// <summary>Owns an <c>fa_archive*</c> and the GC handle of its I/O callbacks (freed after the native close, which calls them).</summary>
sealed class ArchiveHandle : SafeHandle
{
    GCHandle _io;

    /// <summary>Runs after the native close (frees the cancel flag the archive read).</summary>
    public Action? OnRelease;

    /// <summary>The pinned bytes of an archive opened by reference (<c>fa_archive_open_memory_ref</c>); unpinned after the native close.</summary>
    public System.Buffers.MemoryHandle? Pin;

    public ArchiveHandle() : base(0, true) { }
    public override bool IsInvalid => handle == 0;

    public void Initialize(nint native, GCHandle? io)
    {
        if (io is { } g) _io = g;
        SetHandle(native);
    }

    protected override bool ReleaseHandle()
    {
        NativeMethods.fa_archive_close(handle);
        if (_io.IsAllocated) _io.Free();
        Pin?.Dispose();
        OnRelease?.Invoke();
        return true;
    }
}

/// <summary>Owns an <c>fa_archive_reader*</c> and keeps its archive alive.</summary>
sealed class ArchiveReaderHandle : SafeHandle
{
    ArchiveHandle? _archive;

    public ArchiveReaderHandle() : base(0, true) { }
    public override bool IsInvalid => handle == 0;

    public void Initialize(nint native, ArchiveHandle archive)
    {
        _archive = HandleRef.AddRef(archive);
        SetHandle(native);
    }

    protected override bool ReleaseHandle()
    {
        NativeMethods.fa_archive_reader_close(handle);
        _archive?.DangerousRelease();
        _archive = null;
        return true;
    }
}
