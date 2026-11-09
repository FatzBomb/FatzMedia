namespace FatzMedia.Video.Sample.Player.Shared.Hosting;

/// <summary>
/// The host's UI thread. Engine events arrive on background threads; the view model posts their
/// effects here, and every bound property changes on this thread.
/// </summary>
public interface IPlayerDispatcher
{
    /// <summary>Gets whether the caller is on the UI thread.</summary>
    bool CheckAccess();

    /// <summary>Runs <paramref name="action"/> later on the UI thread.</summary>
    void Post(Action action);

    /// <summary>Creates a stopped timer that calls <paramref name="tick"/> on the UI thread.</summary>
    IPlayerTimer CreateTimer(TimeSpan interval, Action tick);
}

/// <summary>A UI-thread timer created by <see cref="IPlayerDispatcher.CreateTimer"/>.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716", Justification = "Stop is the natural name; a C#-only API where the VB keyword clash does not matter and a rename would break every host.")]
public interface IPlayerTimer : IDisposable
{
    void Start();

    void Stop();
}
