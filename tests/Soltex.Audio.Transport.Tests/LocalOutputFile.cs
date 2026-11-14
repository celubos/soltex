namespace Soltex.Audio.Transport.Tests;

/// <summary>
/// The one rule for files the live harnesses write (journals, receipts, recordings): a new file on a local fixed
/// drive, not a network (UNC) path, and not reached through a junction or symbolic link. Shared by every harness.
/// </summary>
internal static class LocalOutputFile
{
    /// <summary>Returns the full path, or throws when <paramref name="path"/> does not follow the rule.</summary>
    internal static string Require(string path, string? parameterName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        string full = Path.GetFullPath(path);
        string? root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(root).DriveType != DriveType.Fixed)
            throw new ArgumentException("Choose a new file on a local fixed drive, not a network or removable drive.", parameterName);
        for (DirectoryInfo? directory = new(Path.GetDirectoryName(full)!); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("The output path must not pass through a junction or symbolic link.", parameterName);
        }
        if (File.Exists(full) || Directory.Exists(full)) throw new IOException("The output file already exists. Choose a new file name.");
        return full;
    }
}
