using System.Runtime.InteropServices;

namespace Soltex.App;

internal sealed record UpdateCheck(IReadOnlyList<string> Titles, string? Error);

/// <summary>
/// Asks the Windows Update Agent which updates are waiting (search only: nothing is downloaded or installed).
/// With De-Windows' notify-only setting, this is how the user hears about updates instead of forced installs.
/// </summary>
internal static class UpdateWatcher
{
    public static Task<UpdateCheck> CheckAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        try
        {
            if (Type.GetTypeFromProgID("Microsoft.Update.Session") is not { } session) return new UpdateCheck([], "Windows Update is not available on this PC.");
            dynamic updateSession = Activator.CreateInstance(session)!;
            updateSession.ClientApplicationID = ProductIdentity.DisplayName;
            dynamic searcher = updateSession.CreateUpdateSearcher();
            dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0");
            List<string> titles = [];
            int count = result.Updates.Count;
            for (int i = 0; i < count; i++) titles.Add((string)result.Updates.Item(i).Title);
            return new UpdateCheck(titles, null);
        }
        catch (Exception error) when (error is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or UnauthorizedAccessException)
        {
            return new UpdateCheck([], "Could not check for updates: " + error.Message);
        }
    }, cancellationToken);
}
