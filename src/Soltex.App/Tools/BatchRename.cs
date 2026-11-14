using System.IO;
using System.Text.RegularExpressions;

namespace Soltex.App.Tools;

/// <summary>One planned rename; <see cref="Problem"/> explains why it will be skipped.</summary>
public sealed record RenameItem(string Path, string NewName, string? Problem)
{
    public string OldName => System.IO.Path.GetFileName(Path);
    public bool Changes => Problem is null && !string.Equals(OldName, NewName, StringComparison.Ordinal);
}

/// <summary>
/// Batch rename (PowerRename-class): find and replace in file and folder names, as plain text or a regular expression,
/// with a preview before anything changes and an undo for the last batch.
/// </summary>
public static class BatchRename
{
    private static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    /// <summary>
    /// Plans the new names. <paramref name="isDirectory"/> says which items are folders (folders keep their whole name);
    /// by default each item is checked on disk. The check for an existing item with the new name always reads the disk,
    /// so on a slow drive call this off the UI thread.
    /// </summary>
    public static IReadOnlyList<RenameItem> Plan(IEnumerable<string> paths, string find, string replace, bool regex, bool matchCase, bool includeExtension,
        Func<string, bool>? isDirectory = null)
    {
        isDirectory ??= Directory.Exists;
        List<RenameItem> plan = [];
        if (string.IsNullOrEmpty(find)) return [.. paths.Select(p => new RenameItem(p, Path.GetFileName(p), null))];
        Regex? pattern = null;
        if (regex)
        {
            try { pattern = new Regex(find, matchCase ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200)); }
            catch (ArgumentException) { return [.. paths.Select(p => new RenameItem(p, Path.GetFileName(p), "The pattern is not a valid regular expression."))]; }
        }
        HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            string name = Path.GetFileName(path);
            bool directory = isDirectory(path);
            string stem = includeExtension || directory ? name : Path.GetFileNameWithoutExtension(name);
            string extension = includeExtension || directory ? "" : Path.GetExtension(name);
            string renamed;
            try
            {
                renamed = (pattern is not null ? pattern.Replace(stem, replace)
                    : stem.Replace(find, replace, matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)) + extension;
            }
            catch (RegexMatchTimeoutException) { plan.Add(new RenameItem(path, name, "The pattern took too long.")); continue; }
            string? problem = renamed.Trim().Length == 0 ? "The new name would be empty."
                : renamed.IndexOfAny(Invalid) >= 0 ? "The new name has characters Windows does not allow."
                : null;
            string target = Path.Combine(Path.GetDirectoryName(path) ?? "", renamed);
            if (problem is null && !string.Equals(name, renamed, StringComparison.OrdinalIgnoreCase) && (File.Exists(target) || Directory.Exists(target)))
                problem = "Another item already has that name.";
            if (problem is null && !taken.Add(target)) problem = "Two items would get the same name.";
            plan.Add(new RenameItem(path, renamed, problem));
        }
        return plan;
    }

    /// <summary>Renames the planned items that change. Returns (from, to) pairs done, in order, for <see cref="Undo"/>.</summary>
    public static IReadOnlyList<(string From, string To)> Apply(IReadOnlyList<RenameItem> plan, out IReadOnlyList<string> failures)
    {
        List<(string, string)> done = [];
        List<string> failed = [];
        foreach (RenameItem item in plan.Where(i => i.Changes))
        {
            string target = Path.Combine(Path.GetDirectoryName(item.Path) ?? "", item.NewName);
            try
            {
                if (Directory.Exists(item.Path)) Directory.Move(item.Path, target);
                else File.Move(item.Path, target);
                done.Add((item.Path, target));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed.Add(item.OldName + ": " + error.Message); }
        }
        failures = failed;
        return done;
    }

    /// <summary>
    /// Puts the last batch back (newest first). Returns the (from, to) pairs that could not be put back, so they can be
    /// tried again, and in <paramref name="failures"/> why.
    /// </summary>
    public static IReadOnlyList<(string From, string To)> Undo(IReadOnlyList<(string From, string To)> done, out IReadOnlyList<string> failures)
    {
        List<(string, string)> remaining = [];
        List<string> failed = [];
        foreach ((string from, string to) in done.Reverse())
        {
            try
            {
                if (Directory.Exists(to)) Directory.Move(to, from);
                else File.Move(to, from);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                remaining.Insert(0, (from, to)); // keep the batch order
                failed.Add(Path.GetFileName(to) + ": " + error.Message);
            }
        }
        failures = failed;
        return remaining;
    }
}
