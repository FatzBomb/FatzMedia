using FatzMedia.Video.Core.Abstractions;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace FatzMedia.Video.Adapters.Archives;

public sealed class SharpArchiveAdapter : IArchiveService
{
    public IArchiveHandle Open(string path) => new SharpArchiveHandle(ArchiveFactory.OpenArchive(path, new ReaderOptions()));

    private sealed class SharpArchiveHandle(IArchive archive) : IArchiveHandle
    {
        public IEnumerable<IArchiveEntryInfo> Entries => archive.Entries.Where(e => e.Key != null && !e.IsDirectory).Select(e => new SharpArchiveEntryInfo(e));
        public void Dispose() => archive.Dispose();
    }

    private sealed class SharpArchiveEntryInfo(IArchiveEntry entry) : IArchiveEntryInfo
    {
        public string Key => entry.Key!;
        public long? Size => entry.Size;
        public void WriteTo(Stream stream) => entry.WriteTo(stream);
    }
}
