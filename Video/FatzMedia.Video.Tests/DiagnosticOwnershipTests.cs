using FatzMedia.Video.Core.Audio;

namespace FatzMedia.Video.Tests;

/// <summary>ENG-025: process-wide state is scoped per player.</summary>
public sealed class DiagnosticOwnershipTests
{
    [Fact]
    public void Raise_CarriesTheCurrentOwnerAndRestoresThePreviousOne()
    {
        var owner = new object();
        MediaDiagnosticInfo? captured = null;
        void Record(MediaDiagnosticInfo info)
        {
            if (info.Category == "OwnershipTest.Raise")
                captured = info;
        }

        DiagnosticRouter.DiagnosticOccurred += Record;
        try
        {
            using (DiagnosticRouter.EnterOwner(owner))
                DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "OwnershipTest.Raise", "owned");

            Assert.Same(owner, captured!.Owner);
            Assert.True(captured.IsFor(owner));
            Assert.False(captured.IsFor(new object()));
            Assert.Null(DiagnosticRouter.Owner);

            DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "OwnershipTest.Raise", "unowned");
            Assert.Null(captured.Owner);
            Assert.True(captured.IsFor(owner));
        }
        finally
        {
            DiagnosticRouter.DiagnosticOccurred -= Record;
        }
    }

    [Fact]
    public async Task Owner_FlowsIntoTasksStartedInTheScope()
    {
        var owner = new object();
        Task<object?> observed;
        using (DiagnosticRouter.EnterOwner(owner))
            observed = Task.Run(() => DiagnosticRouter.Owner);

        Assert.Same(owner, await observed);
    }

    [Fact]
    public void PreparedConversionCache_KeepsSeveralIdentitiesAndEvictsTheOldest()
    {
        var cache = new PreparedConversionCache<Entry>(2, static e => e.Identity);
        var a = new Entry("a");
        var b = new Entry("b");
        var c = new Entry("c");

        cache.Add(a);
        cache.Add(b);
        Assert.Same(a, cache.Find("a"));
        Assert.Same(b, cache.Find("b"));

        cache.Add(c);
        Assert.Equal(2, cache.Count);
        Assert.Null(cache.Find("a"));
        Assert.True(a.Disposed);
        Assert.False(b.Disposed);
        Assert.False(c.Disposed);
    }

    [Fact]
    public void PreparedConversionCache_DuplicateAddKeepsTheExistingEntry()
    {
        var cache = new PreparedConversionCache<Entry>(4, static e => e.Identity);
        var first = new Entry("a");
        var second = new Entry("a");

        Assert.Same(first, cache.Add(first));
        Assert.Same(first, cache.Add(second));
        Assert.True(second.Disposed);
        Assert.False(first.Disposed);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void PreparedConversionCache_TakeHandsOverOwnership_ReleaseDisposes()
    {
        var cache = new PreparedConversionCache<Entry>(4, static e => e.Identity);
        var taken = new Entry("a");
        var released = new Entry("b");
        cache.Add(taken);
        cache.Add(released);

        Assert.Same(taken, cache.Take("a"));
        Assert.False(taken.Disposed);
        Assert.Null(cache.Take("a"));

        cache.Release("b");
        Assert.True(released.Disposed);
        Assert.Equal(0, cache.Count);
        cache.Release("missing");
    }

    private sealed class Entry(string identity) : IDisposable
    {
        public string Identity { get; } = identity;

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
