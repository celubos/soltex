using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Soltex.Audio.App.Tests;

internal static class Checks
{
    public static void That(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static T Named<T>(Window window, string name) where T : FrameworkElement =>
        (window is MixerFixture fixture ? fixture.Workspace.FindName(name) : window.FindName(name)) as T ?? throw new InvalidOperationException($"Required production control '{name}' was not found.");

    public static T Field<T>(object owner, string name) =>
        (T)(owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
            ?? throw new InvalidOperationException($"Required lifecycle observation '{name}' was not found."));

    public static bool FieldIsNull(object owner, string name)
    {
        var field = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Required lifecycle observation '{name}' was not found.");
        return field.GetValue(owner) is null;
    }

    public static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    public static void Click(Window window, string caption)
    {
        var button = Descendants(window).OfType<Button>().Single(b => string.Equals(b.Content as string, caption, StringComparison.Ordinal));
        That(button.IsEnabled, $"'{caption}' must be enabled for this check.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    public static void PrepareOffscreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = SystemParameters.VirtualScreenLeft - 5000;
        window.Top = SystemParameters.VirtualScreenTop - 5000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
    }

    public static Task UntilAsync(Func<bool> predicate, string message) => UntilAsync(predicate, message, null);

    /// <summary>Waits for a condition; on timeout the message includes the observed state so failures are diagnosable.</summary>
    public static async Task UntilAsync(Func<bool> predicate, string message, Func<string>? observed)
    {
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (!predicate())
        {
            if (DateTime.UtcNow >= timeout)
                throw new TimeoutException(observed is null ? message : message + " Observed: " + observed());
            await Task.Delay(20);
        }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    public static async Task CloseAsync(Window window)
    {
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public static void Render(Window window, string? directory, string filename)
    {
        if (directory is null) return;
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var context = background.RenderOpen())
            context.DrawRectangle(window.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        bitmap.Render(background);
        // Generic Window content may have a margin. Translate that layout offset out of the render,
        // preserving the actual visual rather than stretching it to the native frame's dimensions.
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
            context.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(Path.Combine(directory, filename), FileMode.CreateNew);
        encoder.Save(stream);
    }

    public static string Number(double value) => value.ToString(CultureInfo.CurrentCulture);
}
