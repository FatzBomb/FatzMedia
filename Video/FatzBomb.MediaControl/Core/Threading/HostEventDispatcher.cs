namespace FatzBomb.MediaControl.Core.Threading;

internal static class HostEventDispatcher
{
    public static void Raise(object sender, EventHandler? handlers, string eventName)
    {
        if (handlers == null)
            return;

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(sender, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                ReportFailure(eventName, ex);
            }
        }
    }

    public static void Raise<TEventArgs>(
        object sender,
        EventHandler<TEventArgs>? handlers,
        TEventArgs args,
        string eventName)
        where TEventArgs : EventArgs
    {
        if (handlers == null)
            return;

        foreach (EventHandler<TEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(sender, args);
            }
            catch (Exception ex)
            {
                ReportFailure(eventName, ex);
            }
        }
    }

    private static void ReportFailure(string eventName, Exception exception) =>
        DiagnosticRouter.Raise(
            MediaDiagnosticLevel.Warning,
            "HostEvent",
            $"{eventName} handler failed: {exception.Message}",
            innerException: exception);
}
