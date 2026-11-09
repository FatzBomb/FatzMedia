using System.Diagnostics;

namespace FatzMedia.Video.Core.Threading;

/// <summary>
/// Raises events to host handlers one handler at a time (ENG-024: every public player event is
/// raised through here). A handler that throws does not stop the others, never escapes onto the
/// raising (engine or queue) thread, and is reported as a <c>HostEvent</c> Warning diagnostic.
/// Inside a control operation each handler runs in its own handler scope (see
/// <see cref="MediaControlOperationQueue.EnterHandlerScope"/>).
/// </summary>
internal static class HostEventDispatcher
{
    public static void Raise(object sender, EventHandler? handlers, string eventName, bool reportFailures = true)
    {
        if (handlers == null)
            return;

        using MediaControlOperationQueue.HandlerScope scope = MediaControlOperationQueue.EnterHandlerScope();
        if (handlers.HasSingleTarget)
        {
            Invoke(handlers, sender, EventArgs.Empty, eventName, reportFailures);
            return;
        }

        // Walks the invocation list in place (ENG-039): GetInvocationList copied it per raise.
        foreach (EventHandler handler in Delegate.EnumerateInvocationList(handlers))
            Invoke(handler, sender, EventArgs.Empty, eventName, reportFailures);
    }

    public static void Raise<TEventArgs>(
        object sender,
        EventHandler<TEventArgs>? handlers,
        TEventArgs args,
        string eventName,
        bool reportFailures = true)
        where TEventArgs : EventArgs
    {
        if (handlers == null)
            return;

        using MediaControlOperationQueue.HandlerScope scope = MediaControlOperationQueue.EnterHandlerScope();
        if (handlers.HasSingleTarget)
        {
            Invoke(handlers, sender, args, eventName, reportFailures);
            return;
        }

        foreach (EventHandler<TEventArgs> handler in Delegate.EnumerateInvocationList(handlers))
            Invoke(handler, sender, args, eventName, reportFailures);
    }

    private static void Invoke(EventHandler handler, object sender, EventArgs args, string eventName, bool reportFailures)
    {
        try
        {
            handler(sender, args);
        }
        catch (Exception ex)
        {
            ReportFailure(eventName, ex, reportFailures);
        }
    }

    private static void Invoke<TEventArgs>(EventHandler<TEventArgs> handler, object sender, TEventArgs args, string eventName, bool reportFailures)
        where TEventArgs : EventArgs
    {
        try
        {
            handler(sender, args);
        }
        catch (Exception ex)
        {
            ReportFailure(eventName, ex, reportFailures);
        }
    }

    /// <param name="report">
    /// <see langword="false"/> for the diagnostic event itself: reporting its failure as a
    /// diagnostic would raise it again.
    /// </param>
    private static void ReportFailure(string eventName, Exception exception, bool report)
    {
        if (!report)
        {
            Debug.WriteLine($"[HostEvent] {eventName} handler failed: {exception.Message}");
            return;
        }

        DiagnosticRouter.Raise(
            MediaDiagnosticLevel.Warning,
            "HostEvent",
            $"{eventName} handler failed: {exception.Message}",
            innerException: exception);
    }
}
