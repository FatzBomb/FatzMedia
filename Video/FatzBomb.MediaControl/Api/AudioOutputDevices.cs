using FatzBomb.MediaControl.Core.Audio;

#pragma warning disable IDE0130
namespace FatzBomb.MediaControl;

/// <summary>
/// Enumerates audio output devices through MediaControl's BASS backend.
/// </summary>
public static class AudioOutputDevices
{
    /// <summary>
    /// Gets the currently available audio output devices using the configured native-library paths.
    /// </summary>
    public static IReadOnlyList<AudioOutputDeviceInfo> Enumerate() =>
        Enumerate(DLLPath.GetConfiguredNativeLibraryPaths());

    /// <summary>
    /// Gets the currently available audio output devices using host-provided native-library paths.
    /// </summary>
    public static IReadOnlyList<AudioOutputDeviceInfo> Enumerate(IEnumerable<string> nativeLibraryPaths)
    {
        ArgumentNullException.ThrowIfNull(nativeLibraryPaths);

        var resolvedPaths = nativeLibraryPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return !Bootstrap.EnsureInitialized(resolvedPaths)
            ? throw new InvalidOperationException(Bootstrap.Message)
            : BassAudioOutputDevices.Enumerate();
    }
}
