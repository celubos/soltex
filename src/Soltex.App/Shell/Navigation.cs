using System.Windows;
using Soltex.App.Settings;

namespace Soltex.App.Shell;

/// <summary>A page hosted by the shell. Pages do no recurring work unless <see cref="OnShown"/> was called.</summary>
public interface IShellPage
{
    Destination Destination { get; }
    string Title { get; }
    FrameworkElement View { get; }
    void OnShown();
    void OnHidden();
    IEnumerable<SearchEntry> SearchEntries();
    /// <summary>Scrolls to and focuses the element registered under <paramref name="anchor"/>.</summary>
    bool Reveal(string anchor);
}

/// <summary>Commands the single-instance pipe and the tray can send to the open window.</summary>
public interface ISuiteWindow
{
    void ShowSuite();
    void OpenAudio();
    void RequestQuit();
}

/// <summary>One searchable thing: a page, a section or a single setting.</summary>
public sealed record SearchEntry(string Title, string Location, Destination Destination, string Anchor, params string[] Synonyms)
{
    internal int Score(string[] terms)
    {
        int score = 0;
        foreach (string term in terms)
        {
            int best = 0;
            if (Title.StartsWith(term, StringComparison.CurrentCultureIgnoreCase)) best = 40;
            else if (Title.Contains(term, StringComparison.CurrentCultureIgnoreCase)) best = 25;
            else if (Synonyms.Any(s => s.StartsWith(term, StringComparison.CurrentCultureIgnoreCase))) best = 18;
            else if (Synonyms.Any(s => s.Contains(term, StringComparison.CurrentCultureIgnoreCase))) best = 12;
            else if (Location.Contains(term, StringComparison.CurrentCultureIgnoreCase)) best = 6;
            if (best == 0) return 0; // every typed word must match something
            score += best;
        }
        return score;
    }
}

/// <summary>Search over every page's entries. Pure and synchronous: results come from an in-memory list.</summary>
internal sealed class CommandIndex
{
    private readonly List<SearchEntry> entries = [];

    public void Replace(IEnumerable<SearchEntry> all)
    {
        entries.Clear();
        entries.AddRange(all);
    }

    public IReadOnlyList<SearchEntry> Find(string query, int limit = 8)
    {
        string[] terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return [];
        return entries
            .Select(e => (Entry: e, Score: e.Score(terms)))
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Entry.Title.Length)
            .Take(limit)
            .Select(r => r.Entry)
            .ToArray();
    }
}
