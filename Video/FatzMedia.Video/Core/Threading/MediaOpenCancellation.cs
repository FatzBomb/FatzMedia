namespace FatzMedia.Video.Core.Threading;

/// <summary>
/// The cancellation of the load that is opening media on the current flow (ENG-024). The player
/// sets it around the engine's open, so a long open (a network connect's retry delays) can stop
/// early without a token on every engine interface. An attempt already in progress (one native
/// connect) is not interrupted.
/// </summary>
static class MediaOpenCancellation
{
    static readonly AsyncLocal<CancellationToken> CurrentToken = new();

    /// <summary>Gets the current open's token, or <see cref="CancellationToken.None"/> outside a load.</summary>
    public static CancellationToken Current => CurrentToken.Value;

    /// <summary>Sets <paramref name="token"/> until the returned scope is disposed (on the same flow).</summary>
    public static Scope Enter(CancellationToken token)
    {
        var previous = CurrentToken.Value;
        CurrentToken.Value = token;
        return new Scope(previous);
    }

    internal readonly struct Scope(CancellationToken previous) : IDisposable
    {
        public void Dispose() => CurrentToken.Value = previous;
    }
}
