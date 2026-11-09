using FatzMedia.Video.Core.Audio;

namespace FatzMedia.Video.Tests;

/// <summary>One normalization rule for file audio and video soundtracks, and one bounded cache.</summary>
public sealed class NormalizationGainTests
{
    [Fact]
    public void TheQuickPass_NeverBoosts_ButAttenuates()
    {
        // A quiet intro would ask for the full boost; a loud start is turned down at once.
        Assert.Equal(NormalizationGainPolicy.MaxQuickPassGain, NormalizationGainPolicy.QuickGain(0.05));
        Assert.True(NormalizationGainPolicy.QuickPassHeldBackBoost(0.05));
        Assert.Equal(AudioNormalizer.CalculateGainFromPeak(1.0), NormalizationGainPolicy.QuickGain(1.0));
        Assert.True(NormalizationGainPolicy.QuickGain(1.0) < 1);
        Assert.False(NormalizationGainPolicy.QuickPassHeldBackBoost(1.0));
    }

    [Fact]
    public void TheFinalPeak_IsTheLouderOfBothPasses() =>
        // A transient the quick pass saw is not lost to the deep pass's sampling.
        Assert.Equal(0.9, NormalizationGainPolicy.FinalPeak(quickPeak: 0.9, deepPeak: 0.4));

    [Fact]
    public void TheCache_DropsTheLeastRecentlyUsedEntryPastItsCapacity()
    {
        var cache = new NormalizationGainCache(capacity: 2);
        cache.Set("a", 1);
        cache.Set("b", 2);
        Assert.True(cache.TryGetValue("A", out _)); // keys ignore case; "a" is now the most recent
        cache.Set("c", 3);

        Assert.Equal(2, cache.Count);
        Assert.False(cache.TryGetValue("b", out _));
        Assert.True(cache.TryGetValue("a", out var a));
        Assert.Equal(1, a);
        cache.Set("a", 4);
        Assert.True(cache.TryGetValue("a", out a));
        Assert.Equal(4, a);
    }
}
