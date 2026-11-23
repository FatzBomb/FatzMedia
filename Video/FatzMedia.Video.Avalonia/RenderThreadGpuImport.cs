using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;

namespace FatzMedia.Video.Avalonia;

// ---------------------------------------------------------------------------------------------
// The one place that uses Avalonia's [Unstable] render-interface API (ENG-066).
//
// The shared GPU image path must not wait for the UI thread, but Avalonia's public image update
// (CompositionDrawingSurface.UpdateWithKeyedMutexAsync and its siblings) only runs from the UI
// thread. The same work is available on the render thread through
// IExternalObjectsRenderInterfaceContextFeature (import the renderer's shared textures) and
// IPlatformRenderInterfaceImportedImage (snapshot the newest one as a bitmap). Those interfaces
// are marked [Unstable]: they are pinned with the Avalonia version in the csproj (an exact
// version, no range), and anything here that throws, or does not exist in another Avalonia
// version, makes the surface report itself unavailable, which switches it to the frame sink.
// Nothing outside this file names these types.
// ---------------------------------------------------------------------------------------------

/// <summary>Imports a renderer's shared images into the compositor's graphics context (render thread only).</summary>
interface IGpuImageImporter
{
    /// <summary>Imports every image (and semaphore) of <paramref name="set"/>; the caller reports the import done.</summary>
    IImportedGpuImages Import(MediaSharedGpuImageSet set);
}

/// <summary>The imported images of one set, on the render thread.</summary>
interface IImportedGpuImages : IDisposable
{
    /// <summary>Makes a bitmap of the frame's image, with the synchronization the renderer chose; the image may be reused after.</summary>
    Bitmap Snapshot(MediaSharedGpuImageFrame frame);
}

/// <summary>Avalonia's implementation of <see cref="IGpuImageImporter"/>.</summary>
sealed class AvaloniaGpuImageImporter : IGpuImageImporter
{
    readonly IExternalObjectsRenderInterfaceContextFeature _feature;

    AvaloniaGpuImageImporter(IExternalObjectsRenderInterfaceContextFeature feature) => _feature = feature;

    /// <summary>Asks the compositor for the import feature; null when its backend has none (or this Avalonia has changed).</summary>
    public static async ValueTask<IGpuImageImporter?> QueryAsync(Compositor compositor)
    {
        try
        {
            return await compositor.TryGetRenderInterfaceFeature(typeof(IExternalObjectsRenderInterfaceContextFeature)) is IExternalObjectsRenderInterfaceContextFeature feature
                ? new AvaloniaGpuImageImporter(feature)
                : null;
        }
        catch (Exception)
        {
            // Any failure of this [Unstable] call (it may not exist in another Avalonia) means: no render-thread import.
            return null;
        }
    }

    public IImportedGpuImages Import(MediaSharedGpuImageSet set)
    {
        var imported = new ImportedImages(set);
        try
        {
            var imageType = AvaloniaGpuInterop.ToAvaloniaImageHandleType(set.HandleKind);
            var semaphoreType = set.SyncKind == MediaSharedGpuImageSyncKinds.Semaphores
                ? AvaloniaGpuInterop.ToAvaloniaSemaphoreHandleType(set.SemaphoreHandleKind)
                : null;
            foreach (var buffer in set.Buffers)
            {
                imported.Images.Add(_feature.ImportImage(
                    new PlatformHandle(buffer.ImageHandle, imageType),
                    AvaloniaGpuInterop.ToImageProperties(set, buffer)));
                // From here the buffer counts as handed over, even if a semaphore import fails: closing a
                // handle the importer took would be worse than leaking one it did not.
                imported.ConsumedBuffers++;
                if (semaphoreType is not null)
                {
                    imported.RenderComplete.Add(_feature.ImportSemaphore(new PlatformHandle(buffer.RenderCompleteSemaphore, semaphoreType)));
                    imported.ConsumerRelease.Add(_feature.ImportSemaphore(new PlatformHandle(buffer.ConsumerReleaseSemaphore, semaphoreType)));
                }
            }
        }
        catch (Exception exception)
        {
            throw new GpuImportException(imported.ConsumedBuffers, exception);
        }

        return imported;
    }

    sealed class ImportedImages(MediaSharedGpuImageSet set) : IImportedGpuImages
    {
        public List<IPlatformRenderInterfaceImportedImage> Images { get; } = [];
        public List<IPlatformRenderInterfaceImportedSemaphore> RenderComplete { get; } = [];
        public List<IPlatformRenderInterfaceImportedSemaphore> ConsumerRelease { get; } = [];
        public int ConsumedBuffers { get; set; }

        public Bitmap Snapshot(MediaSharedGpuImageFrame frame)
        {
            var image = Images[frame.BufferIndex];
            return new SnapshotBitmap(set.SyncKind switch
            {
                MediaSharedGpuImageSyncKinds.KeyedMutex => image.SnapshotWithKeyedMutex((uint)frame.AcquireKey, (uint)frame.ReleaseKey),
                MediaSharedGpuImageSyncKinds.Semaphores => image.SnapshotWithSemaphores(RenderComplete[frame.BufferIndex], ConsumerRelease[frame.BufferIndex]),
                _ => image.SnapshotWithAutomaticSync(),
            });
        }

        public void Dispose()
        {
            foreach (var image in Images)
                DisposeQuietly(image);
            foreach (var semaphore in RenderComplete)
                DisposeQuietly(semaphore);
            foreach (var semaphore in ConsumerRelease)
                DisposeQuietly(semaphore);
        }

        static void DisposeQuietly(IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception)
            {
                // A lost or failed import has nothing left to release.
            }
        }
    }

    /// <summary>The compositor's snapshot of one shared image, drawn like any bitmap.</summary>
    sealed class SnapshotBitmap(IBitmapImpl impl) : Bitmap(impl);
}

/// <summary>An import that failed part-way: how many buffers' handles the importer already took.</summary>
sealed class GpuImportException(int consumedBuffers, Exception inner) : Exception(inner.Message, inner)
{
    public int ConsumedBuffers { get; } = consumedBuffers;
}
