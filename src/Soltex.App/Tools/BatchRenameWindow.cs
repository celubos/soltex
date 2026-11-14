using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Soltex.App.Tools;

/// <summary>Batch rename: choose files or a folder, type what to find and what to put instead, check the preview, rename.</summary>
/// <remarks>
/// The preview is planned on a background thread a moment after typing stops: planning checks the disk for every new
/// name, which is slow for large folders and network drives. Rename stays off until the preview matches the boxes.
/// </remarks>
public sealed class BatchRenameWindow : Window
{
    private readonly TextBox find = new() { MinWidth = 220 }, replace = new() { MinWidth = 220 };
    private readonly CheckBox regex = new() { Content = "Regular expression" }, matchCase = new() { Content = "Match case" }, extension = new() { Content = "Include extension" };
    private readonly ListView preview = new() { Height = 280 };
    private readonly TextBlock summary = new();
    private readonly Button rename = new() { Content = "Rename" }, undo = new() { Content = "Undo last rename", IsEnabled = false };
    private readonly DispatcherTimer typing;
    private List<string> paths = [];
    private HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<RenameItem> plan = [];
    private IReadOnlyList<(string From, string To)> last = [];
    private int version;

    public BatchRenameWindow()
    {
        Title = "Batch rename";
        Width = 760;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)FindResource("Studio.BaseBrush");
        typing = new DispatcherTimer(DispatcherPriority.Input, Dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
        typing.Tick += (_, _) => Refresh();
        StackPanel body = new() { Margin = new Thickness(24) };
        TextBlock heading = new() { Text = "Batch rename", Style = (Style)FindResource("Shell.CardTitle"), Margin = new Thickness(0, 0, 0, 12) };
        body.Children.Add(heading);

        WrapPanel pick = new() { Margin = new Thickness(0, 0, 0, 12) };
        Button files = new() { Content = "Choose files", Margin = new Thickness(0, 0, 8, 0) };
        files.Click += (_, _) => ChooseFiles();
        Button folder = new() { Content = "Choose a folder", Margin = new Thickness(0, 0, 8, 0) };
        folder.Click += (_, _) => ChooseFolder();
        pick.Children.Add(files);
        pick.Children.Add(folder);
        body.Children.Add(pick);

        body.Children.Add(Field("Find", find));
        body.Children.Add(Field("Replace with", replace));
        WrapPanel options = new() { Margin = new Thickness(0, 4, 0, 12) };
        foreach (CheckBox option in new[] { regex, matchCase, extension })
        {
            option.Margin = new Thickness(0, 0, 18, 0);
            option.Checked += (_, _) => RefreshSoon();
            option.Unchecked += (_, _) => RefreshSoon();
            options.Children.Add(option);
        }
        body.Children.Add(options);
        find.TextChanged += (_, _) => RefreshSoon();
        replace.TextChanged += (_, _) => RefreshSoon();
        AutomationProperties.SetName(find, "Find");
        AutomationProperties.SetName(replace, "Replace with");

        GridView columns = new();
        columns.Columns.Add(new GridViewColumn { Header = "Now", Width = 300, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(RenameItem.OldName)) });
        columns.Columns.Add(new GridViewColumn { Header = "After", Width = 300, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(RenameItem.NewName)) });
        preview.View = columns;
        AutomationProperties.SetName(preview, "Rename preview");
        body.Children.Add(preview);
        summary.Style = (Style)FindResource("Shell.Secondary");
        summary.Margin = new Thickness(0, 8, 0, 12);
        summary.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(summary);

        WrapPanel actions = new();
        rename.Style = (Style)FindResource("Studio.PrimaryButton");
        rename.Margin = new Thickness(0, 0, 8, 0);
        rename.Click += (_, _) => Apply();
        undo.Click += (_, _) => Undo();
        actions.Children.Add(rename);
        actions.Children.Add(undo);
        body.Children.Add(actions);
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Closed += (_, _) =>
        {
            typing.Stop();
            version++; // drop any preview still being planned
        };
        Refresh();
    }

    /// <summary>
    /// The items to rename (also set by tests). <paramref name="folders"/> lists the ones that are folders; when it is
    /// left out, each item is checked on disk once here.
    /// </summary>
    internal void Use(IEnumerable<string> items, IEnumerable<string>? folders = null)
    {
        paths = [.. items];
        directories = new HashSet<string>(folders ?? paths.Where(Directory.Exists), StringComparer.OrdinalIgnoreCase);
        Refresh();
    }

    internal TextBox FindBox => find;
    internal TextBox ReplaceBox => replace;
    internal IReadOnlyList<RenameItem> Plan => plan;

    internal void Apply()
    {
        last = BatchRename.Apply(plan, out IReadOnlyList<string> failures);
        Dictionary<string, string> renamed = last.ToDictionary(d => d.From, d => d.To, StringComparer.Ordinal);
        Move(p => renamed.GetValueOrDefault(p, p));
        undo.IsEnabled = last.Count > 0;
        Refresh((last.Count == 1 ? "1 item renamed." : last.Count + " items renamed.") + (failures.Count > 0 ? " Not renamed: " + string.Join("; ", failures) : ""));
    }

    internal void Undo()
    {
        IReadOnlyList<(string From, string To)> remaining = BatchRename.Undo(last, out IReadOnlyList<string> failures);
        // Only the names that really went back change in the list; the rest keep their new names and can be tried again.
        Dictionary<string, string> restored = last.Except(remaining).ToDictionary(d => d.To, d => d.From, StringComparer.Ordinal);
        Move(p => restored.GetValueOrDefault(p, p));
        last = remaining;
        undo.IsEnabled = last.Count > 0;
        Refresh(failures.Count == 0 ? "Names put back." : "Not put back: " + string.Join("; ", failures));
    }

    /// <summary>Follows renamed items to their new paths.</summary>
    private void Move(Func<string, string> next)
    {
        paths = [.. paths.Select(next)];
        directories = new HashSet<string>(directories.Select(next), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Plans the preview a moment after the last change, so typing is never held up by the disk.</summary>
    private void RefreshSoon()
    {
        version++;
        rename.IsEnabled = false;
        typing.Stop();
        typing.Start();
    }

    /// <summary>Plans the preview now, off the UI thread. <paramref name="status"/> replaces the usual summary.</summary>
    private void Refresh(string? status = null)
    {
        typing.Stop();
        int ticket = ++version;
        rename.IsEnabled = false;
        if (status is not null) summary.Text = status;
        if (paths.Count == 0)
        {
            ShowPlan([], status);
            return;
        }
        string[] items = [.. paths];
        HashSet<string> folders = directories;
        string findText = find.Text, replaceText = replace.Text;
        bool useRegex = regex.IsChecked == true, caseSensitive = matchCase.IsChecked == true, wholeName = extension.IsChecked == true;
        _ = PlanAsync();

        async Task PlanAsync()
        {
            IReadOnlyList<RenameItem> next;
            try { next = await Task.Run(() => BatchRename.Plan(items, findText, replaceText, useRegex, caseSensitive, wholeName, folders.Contains)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                if (ticket == version) summary.Text = "The preview could not be made: " + error.Message;
                return;
            }
            if (ticket == version) ShowPlan(next, status);
        }
    }

    private void ShowPlan(IReadOnlyList<RenameItem> next, string? status)
    {
        plan = next;
        preview.ItemsSource = plan;
        int changes = plan.Count(p => p.Changes), problems = plan.Count(p => p.Problem is not null);
        rename.IsEnabled = changes > 0;
        summary.Text = status ?? (paths.Count == 0 ? "Choose files or a folder to start."
            : $"{changes} of {paths.Count} will be renamed." + (problems > 0 ? " Skipped: " + string.Join(" ", plan.Where(p => p.Problem is not null).Select(p => p.OldName + " (" + p.Problem + ")").Take(3)) : ""));
    }

    private void ChooseFiles()
    {
        Microsoft.Win32.OpenFileDialog dialog = new() { Multiselect = true, Title = "Files to rename" };
        if (dialog.ShowDialog(this) == true) Use(dialog.FileNames, folders: []);
    }

    private void ChooseFolder()
    {
        Microsoft.Win32.OpenFolderDialog dialog = new() { Title = "Rename everything in this folder" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            FileSystemInfo[] entries = [.. new DirectoryInfo(dialog.FolderName).EnumerateFileSystemInfos().OrderBy(e => e.FullName, StringComparer.CurrentCultureIgnoreCase)];
            Use(entries.Select(e => e.FullName), entries.OfType<DirectoryInfo>().Select(e => e.FullName));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { summary.Text = error.Message; }
    }

    private StackPanel Field(string label, TextBox box)
    {
        StackPanel field = new() { Margin = new Thickness(0, 0, 0, 8) };
        field.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Shell.Secondary"), Margin = new Thickness(0, 0, 0, 4) });
        field.Children.Add(box);
        return field;
    }
}
