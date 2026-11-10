using Xunit;

namespace FatzMedia.Audio.Tests;

public class CacheInfoTests
{
    [Fact]
    public void MissingParents_AreCreated_AndReported()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fa-cacheinfo-" + Guid.NewGuid().ToString("N"), "a", "b");
        try
        {
            using var engine = AudioEngine.Create(new AudioEngineOptions { NullOutput = true, CacheDirectory = dir });
            var info = engine.GetCacheInfo();
            Assert.Equal(ErrorCode.Ok, info.Status);
            Assert.Equal(dir, info.Directory);
            Assert.False(info.IsDefault);
            Assert.True(Directory.Exists(dir));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(dir))!, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Unset_UsesTheOsFolder_AndNoCacheTurnsItOff()
    {
        using (var engine = AudioEngine.Create(new AudioEngineOptions { NullOutput = true }))
        {
            var info = engine.GetCacheInfo();
            Assert.True(info.IsDefault);
            Assert.Equal(ErrorCode.Ok, info.Status);
            Assert.Contains("FatzAudio", info.Directory);
        }
        using (var engine = AudioEngine.Create(new AudioEngineOptions { NullOutput = true, NoCache = true }))
        {
            var info = engine.GetCacheInfo();
            Assert.Equal(ErrorCode.NotFound, info.Status);
            Assert.Null(info.Directory);
        }
    }
}
