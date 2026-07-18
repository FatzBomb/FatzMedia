#pragma warning disable IDE0130
using System.Collections.ObjectModel;

namespace FatzBomb.MediaControl;

/// <summary>Stable extensible identifier for a native presentation-surface type.</summary>
public readonly record struct MediaNativeSurfaceTypeId
{
    public MediaNativeSurfaceTypeId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim();
    }

    public string Value { get; }
    public bool IsValid => !string.IsNullOrWhiteSpace(Value);

    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Stable extensible identifier for one handle within a native surface.</summary>
public readonly record struct MediaNativeHandleId
{
    public MediaNativeHandleId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value.Trim();
    }

    public string Value { get; }
    public bool IsValid => !string.IsNullOrWhiteSpace(Value);

    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Known native surface IDs. Additional platform packages may define their own stable IDs.</summary>
public static class MediaNativeSurfaceTypeIds
{
    public static MediaNativeSurfaceTypeId Win32Hwnd { get; } = new("win32-hwnd");
    public static MediaNativeSurfaceTypeId X11Window { get; } = new("x11-window");
    public static MediaNativeSurfaceTypeId WaylandSurface { get; } = new("wayland-surface");
    public static MediaNativeSurfaceTypeId AppKitView { get; } = new("appkit-view");
    public static MediaNativeSurfaceTypeId AndroidNativeWindow { get; } = new("android-native-window");
}

/// <summary>Known native handle IDs used by standard surface descriptors.</summary>
public static class MediaNativeHandleIds
{
    public static MediaNativeHandleId Window { get; } = new("window");
    public static MediaNativeHandleId Display { get; } = new("display");
    public static MediaNativeHandleId Surface { get; } = new("surface");
    public static MediaNativeHandleId View { get; } = new("view");
}

/// <summary>A named borrowed native handle. The receiver must not release or destroy it.</summary>
public readonly record struct MediaNativeHandle(MediaNativeHandleId Id, nint Value)
{
    public bool IsValid => Id.IsValid && Value != 0;
}

/// <summary>
/// Describes an adapter-owned native surface using an extensible type ID and named handles.
/// Handles remain borrowed and valid from successful attach until detach or replacement.
/// </summary>
public sealed class MediaNativeSurfaceDescriptor
{
    private readonly ReadOnlyCollection<MediaNativeHandle> _handles;

    public MediaNativeSurfaceDescriptor(
        MediaNativeSurfaceTypeId surfaceTypeId,
        IEnumerable<MediaNativeHandle> handles)
    {
        ArgumentNullException.ThrowIfNull(handles);
        SurfaceTypeId = surfaceTypeId;
        _handles = Array.AsReadOnly<MediaNativeHandle>([.. handles]);
    }

    public MediaNativeSurfaceTypeId SurfaceTypeId { get; }
    public IReadOnlyList<MediaNativeHandle> Handles => _handles;

    public bool IsValid
    {
        get
        {
            if (!SurfaceTypeId.IsValid || _handles.Count == 0)
                return false;

            HashSet<MediaNativeHandleId> seenIds = [];
            foreach (MediaNativeHandle handle in _handles)
            {
                if (!handle.IsValid || !seenIds.Add(handle.Id))
                    return false;
            }

            return true;
        }
    }

    public bool TryGetHandle(MediaNativeHandleId handleId, out nint value)
    {
        foreach (MediaNativeHandle handle in _handles)
        {
            if (handle.Id == handleId)
            {
                value = handle.Value;
                return value != 0;
            }
        }

        value = 0;
        return false;
    }

    public static MediaNativeSurfaceDescriptor Win32Hwnd(nint hwnd)
    {
        ArgumentOutOfRangeException.ThrowIfZero(hwnd);
        return new(
            MediaNativeSurfaceTypeIds.Win32Hwnd,
            [new MediaNativeHandle(MediaNativeHandleIds.Window, hwnd)]);
    }

    public override string ToString() => $"{SurfaceTypeId}; handles={_handles.Count}";
}
