namespace FatzMedia.Video.Core.Abstractions;
/// <summary>
/// Provides functionality to open and work with archive files.
/// </summary>
public interface IArchiveService
{
    /// <summary>
    /// Opens an archive file at the specified path.
    /// </summary>
    /// <param name="path">The path to the archive file to open.</param>
    /// <returns>An <see cref="IArchiveHandle"/> that provides access to the archive contents.</returns>
    IArchiveHandle Open(string path);
}

/// <summary>
/// Represents a handle to an opened archive file that provides access to its entries.
/// </summary>
public interface IArchiveHandle : IDisposable
{
    /// <summary>
    /// Gets a collection of entries contained within the archive.
    /// </summary>
    IEnumerable<IArchiveEntryInfo> Entries { get; }
}

/// <summary>
/// Provides information about an entry within an archive.
/// </summary>
public interface IArchiveEntryInfo
{
    /// <summary>
    /// Gets the key or path identifier for the archive entry.
    /// </summary>
    string Key { get; }

    /// <summary>
    /// Gets the size of the archive entry in bytes, or null if the size is unknown.
    /// </summary>
    long? Size { get; }

    /// <summary>
    /// Writes the archive entry contents into the supplied stream.
    /// </summary>
    void WriteTo(Stream stream);
}
