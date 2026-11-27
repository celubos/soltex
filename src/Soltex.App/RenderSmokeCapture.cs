using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Soltex.App;

internal static class RenderSmokeCapture
{
    /// <summary>The canonical capture size; --size chooses another one.</summary>
    internal const int PixelWidth = 1280;
    internal const int PixelHeight = 820;

    internal static void ConfigureWindow(Window window) => ConfigureWindow(window, PixelWidth, PixelHeight);

    internal static void ConfigureWindow(Window window, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.NoResize;
        window.SizeToContent = SizeToContent.Manual;
        window.ShowActivated = false;
        // Min and Max first in an order that never conflicts with the previous size.
        window.MinWidth = 0;
        window.MinHeight = 0;
        window.MaxWidth = width;
        window.MaxHeight = height;
        window.MinWidth = width;
        window.MinHeight = height;
        window.Width = width;
        window.Height = height;
    }

    internal static RenderTargetBitmap Capture(Window window) => Capture(window, PixelWidth, PixelHeight);

    internal static RenderTargetBitmap Capture(Window window, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(window);

        window.InvalidateMeasure();
        window.InvalidateArrange();
        window.InvalidateVisual();
        window.UpdateLayout();
        window.Dispatcher.Invoke(static () => { }, DispatcherPriority.Render);

        int actualWidth = Math.Max(1, checked((int)Math.Ceiling(window.ActualWidth)));
        int actualHeight = Math.Max(1, checked((int)Math.Ceiling(window.ActualHeight)));
        if (actualWidth != width || actualHeight != height)
        {
            throw new InvalidOperationException(
                $"Render-smoke window is {actualWidth}x{actualHeight}; " +
                $"expected {width}x{height}.");
        }

        RenderTargetBitmap warmup = new(width, height, 96, 96, PixelFormats.Pbgra32);
        warmup.Render(window);
        window.Dispatcher.Invoke(static () => { }, DispatcherPriority.Render);

        RenderTargetBitmap bitmap = new(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        return bitmap;
    }
}
