namespace FatzMedia.Video.Core.Graphics.Rendering;

/// <summary>
/// Releases per-target renderer resources whose target is gone (ENG-026: post-process textures
/// of removed secondary windows were kept until the renderer reset).
/// </summary>
public static class TargetResourcePruning
{
    /// <summary>
    /// Disposes and removes every entry of <paramref name="resources"/> whose key is not the id
    /// of one of <paramref name="liveTargets"/> (matched with the dictionary's comparer).
    /// Allocation-free; returns how many entries were removed.
    /// </summary>
    public static int RetainOnly<TResource, TTarget>(
        Dictionary<string, TResource> resources,
        IReadOnlyList<TTarget> liveTargets,
        Func<TTarget, string> idOf)
        where TResource : IDisposable
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(liveTargets);
        ArgumentNullException.ThrowIfNull(idOf);
        if (resources.Count == 0)
            return 0;

        var removed = 0;
        IEqualityComparer<string> comparer = resources.Comparer;
        foreach (KeyValuePair<string, TResource> pair in resources)
        {
            if (IsLive(pair.Key, liveTargets, idOf, comparer))
                continue;

            // Removing the current entry while enumerating is supported by Dictionary.
            _ = resources.Remove(pair.Key);
            pair.Value.Dispose();
            removed++;
        }

        return removed;
    }

    private static bool IsLive<TTarget>(string key, IReadOnlyList<TTarget> liveTargets, Func<TTarget, string> idOf, IEqualityComparer<string> comparer)
    {
        for (var i = 0; i < liveTargets.Count; i++)
        {
            if (comparer.Equals(key, idOf(liveTargets[i])))
                return true;
        }

        return false;
    }
}
