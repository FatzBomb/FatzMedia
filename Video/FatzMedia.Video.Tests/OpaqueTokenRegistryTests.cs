namespace FatzMedia.Video.Tests;

public class OpaqueTokenRegistryTests
{
    static OpaqueTokenRegistry<object> Create() => new("exhausted");

    [Fact]
    public void Register_ReturnsDistinctPositiveTokensThatResolve()
    {
        var registry = Create();
        var a = new object();
        var b = new object();

        var tokenA = registry.Register(a);
        var tokenB = registry.Register(b);

        Assert.True(tokenA > 0);
        Assert.NotEqual(tokenA, tokenB);
        Assert.True(registry.TryResolve((nint)tokenA, out var resolvedA));
        Assert.Same(a, resolvedA);
        Assert.True(registry.TryResolve((nint)tokenB, out var resolvedB));
        Assert.Same(b, resolvedB);
    }

    [Fact]
    public void TryResolve_RejectsZeroAndUnknownTokens()
    {
        var registry = Create();
        _ = registry.Register(new object());

        Assert.False(registry.TryResolve(0, out var zero));
        Assert.Null(zero);
        Assert.False(registry.TryResolve(12345, out var unknown));
        Assert.Null(unknown);
    }

    [Fact]
    public void Remove_StopsResolution_AndTokensAreNeverReused()
    {
        var registry = Create();
        var first = registry.Register(new object());

        registry.Remove(first);
        var second = registry.Register(new object());

        Assert.False(registry.TryResolve((nint)first, out _));
        Assert.NotEqual(first, second);
        registry.Remove(first);
        registry.Remove(999);
        Assert.True(registry.TryResolve((nint)second, out _));
    }

    [Fact]
    public void Register_RejectsNull() =>
        Assert.Throws<ArgumentNullException>(() => Create().Register(null!));

    [Fact]
    public void Register_IsThreadSafe()
    {
        var registry = Create();
        var tokens = new long[8 * 500];

        Parallel.For(0, tokens.Length, i => tokens[i] = registry.Register(new object()));

        Assert.Equal(tokens.Length, tokens.Distinct().Count());
        Assert.All(tokens, t => Assert.True(registry.TryResolve((nint)t, out _)));
    }
}
