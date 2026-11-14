using System.Windows.Threading;

namespace Soltex.App;

internal static class UiThread
{
    /// <summary>
    /// Starts asynchronous UI work so that every continuation runs on <paramref name="dispatcher"/>, even when the event
    /// that triggered it was raised without a UI synchronization context. In that state an ordinary <c>async void</c>
    /// handler resumes on a thread-pool thread after its first await, touches the UI and terminates the process
    /// (seen in a crash report: the Customize switch handler).
    /// </summary>
    public static void Handle(this Dispatcher dispatcher, Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(work);
        if (dispatcher.CheckAccess() && SynchronizationContext.Current is DispatcherSynchronizationContext) Run(work);
        else dispatcher.InvokeAsync(() => Run(work));
    }

    // Deliberately async void: it always starts inside a dispatcher frame, so it captures the UI context and a failure
    // reaches the dispatcher's unhandled-exception handler (local report and a message) instead of a pool thread.
    private static async void Run(Func<Task> work) => await work();
}
