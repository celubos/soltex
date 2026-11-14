namespace Soltex.App;

/// <summary>
/// The one place the user-facing product name lives. Internal identities (assembly names, pipe and mutex names, the
/// Run value, %LOCALAPPDATA%\Soltex and authenticated state) use the same name.
/// </summary>
internal static class ProductIdentity
{
    public const string DisplayName = "Soltex";

    /// <summary>Folder under %LOCALAPPDATA% that holds user data. Never rename it: existing settings and journals live there.</summary>
    public const string DataFolderName = "Soltex";
}
