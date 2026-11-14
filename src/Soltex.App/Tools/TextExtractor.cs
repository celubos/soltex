using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using Drawing = System.Drawing;

namespace Soltex.App.Tools;

/// <summary>
/// Text extractor (PowerToys Text Extractor-class): freezes each screen, lets the user drag a box, reads the text inside
/// it with Windows' own OCR (on this PC, nothing leaves it) and copies it.
/// </summary>
public static class TextExtractor
{
    private static bool running;

    /// <summary>Runs one capture; reports what happened through <paramref name="feedback"/>.</summary>
    public static async Task RunAsync(Action<string> feedback)
    {
        if (running) return;
        running = true;
        try
        {
            using Drawing.Bitmap? selection = await SelectAsync().ConfigureAwait(true);
            if (selection is null) return;
            string? text = await RecognizeAsync(selection).ConfigureAwait(true);
            if (text is null) { feedback("Text extractor needs a Windows OCR language (Settings › Time & language)."); return; }
            if (text.Length == 0) { feedback("No text found in that area."); return; }
            try { Clipboard.SetText(text); feedback(text.Length == 1 ? "1 character copied" : text.Length + " characters copied"); }
            catch (COMException) { feedback("The clipboard is busy; try again."); }
        }
        finally { running = false; }
    }

    /// <summary>Reads the text in a bitmap with Windows OCR (the user's languages). Null when no OCR language is installed.</summary>
    public static async Task<string?> RecognizeAsync(Drawing.Bitmap bitmap)
    {
        OcrEngine? engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null) return null;
        // Small selections read better a little larger; OCR refuses images larger than MaxImageDimension on either side.
        double factor = bitmap.Height < 60 ? 2.0 : 1.0;
        double limit = OcrEngine.MaxImageDimension;
        factor = Math.Min(factor, limit / Math.Max(bitmap.Width, bitmap.Height));
        int width = Math.Max(1, (int)(bitmap.Width * factor)), height = Math.Max(1, (int)(bitmap.Height * factor));
        using Drawing.Bitmap scaled = width == bitmap.Width && height == bitmap.Height ? new Drawing.Bitmap(bitmap) : new Drawing.Bitmap(bitmap, width, height);
        using MemoryStream png = new();
        scaled.Save(png, Drawing.Imaging.ImageFormat.Png);
        using InMemoryRandomAccessStream stream = new();
        using (DataWriter writer = new(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(png.ToArray());
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream(); // keep the random-access stream open for the decoder
        }
        stream.Seek(0);
        Windows.Graphics.Imaging.BitmapDecoder decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        using SoftwareBitmap software = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        OcrResult result = await engine.RecognizeAsync(software);
        return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text)).Trim();
    }

    /// <summary>
    /// Shows a frozen copy of every screen and returns the area the user drags (null when cancelled with Esc, a right
    /// click, or by closing any of the screens some other way, such as Alt+F4).
    /// </summary>
    private static Task<Drawing.Bitmap?> SelectAsync()
    {
        TaskCompletionSource<Drawing.Bitmap?> done = new();
        List<SelectionWindow> windows = [];
        void Finish(Drawing.Bitmap? result)
        {
            foreach (SelectionWindow w in windows) w.Dismiss();
            if (!done.TrySetResult(result)) result?.Dispose();
        }
        foreach (System.Windows.Forms.Screen screen in System.Windows.Forms.Screen.AllScreens)
        {
            Drawing.Rectangle bounds = screen.Bounds;
            Drawing.Bitmap shot = new(bounds.Width, bounds.Height);
            using (Drawing.Graphics graphics = Drawing.Graphics.FromImage(shot)) graphics.CopyFromScreen(bounds.Location, Drawing.Point.Empty, bounds.Size);
            SelectionWindow window = new(shot, bounds);
            window.Selected += crop => Finish(crop);
            window.Cancelled += () => Finish(null);
            windows.Add(window);
        }
        foreach (SelectionWindow window in windows) window.Show();
        windows.FirstOrDefault()?.Activate();
        return done.Task;
    }

    /// <summary>One screen's frozen image with a drag-to-select box.</summary>
    private sealed class SelectionWindow : Window
    {
        private readonly Drawing.Bitmap shot;
        private readonly Drawing.Rectangle bounds;
        private readonly Canvas canvas = new() { Background = Brushes.Transparent, Cursor = Cursors.Cross };
        private readonly System.Windows.Shapes.Rectangle box = new() { Stroke = Brushes.White, StrokeThickness = 1.5, Fill = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), Visibility = Visibility.Collapsed };
        private Point start;
        private bool dragging, finished, closed;

        public SelectionWindow(Drawing.Bitmap shot, Drawing.Rectangle bounds)
        {
            this.shot = shot;
            this.bounds = bounds;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = false;
            Title = "Text extractor";
            nint hBitmap = shot.GetHbitmap();
            BitmapSource image;
            try { image = Imaging.CreateBitmapSourceFromHBitmap(hBitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); }
            finally { _ = DeleteObject(hBitmap); }
            Grid layers = new();
            layers.Children.Add(new Image { Source = image, Stretch = Stretch.Fill });
            layers.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)) });
            canvas.Children.Add(box);
            layers.Children.Add(canvas);
            Content = layers;
            SourceInitialized += (_, _) => _ = SetWindowPos(new WindowInteropHelper(this).Handle, -1, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0040);
            KeyDown += (_, e) => { if (e.Key == Key.Escape) Cancel(); };
            canvas.MouseLeftButtonDown += (_, e) => { start = e.GetPosition(canvas); dragging = true; canvas.CaptureMouse(); Draw(start); };
            canvas.MouseMove += (_, e) => { if (dragging) Draw(e.GetPosition(canvas)); };
            canvas.MouseLeftButtonUp += (_, e) => { if (dragging) Complete(e.GetPosition(canvas)); };
            MouseRightButtonDown += (_, _) => Cancel();
            Closed += (_, _) =>
            {
                closed = true;
                shot.Dispose();
                Cancel(); // closed by Windows (Alt+F4) rather than by a selection: end the whole capture
            };
        }

        public event Action<Drawing.Bitmap>? Selected;
        public event Action? Cancelled;

        /// <summary>Closes this screen without raising <see cref="Cancelled"/>.</summary>
        public void Dismiss()
        {
            finished = true;
            if (!closed) Close();
        }

        private void Draw(Point end)
        {
            box.Visibility = Visibility.Visible;
            Canvas.SetLeft(box, Math.Min(start.X, end.X));
            Canvas.SetTop(box, Math.Min(start.Y, end.Y));
            box.Width = Math.Abs(end.X - start.X);
            box.Height = Math.Abs(end.Y - start.Y);
        }

        private void Complete(Point end)
        {
            dragging = false;
            canvas.ReleaseMouseCapture();
            // The canvas keeps the mouse captured, so a drag that ends on another monitor gives points outside this
            // screen (negative to the left or above). Clamp both corners to this screen's image.
            double sx = bounds.Width / Math.Max(1, canvas.ActualWidth), sy = bounds.Height / Math.Max(1, canvas.ActualHeight);
            int x0 = Math.Clamp((int)(Math.Min(start.X, end.X) * sx), 0, bounds.Width), x1 = Math.Clamp((int)(Math.Max(start.X, end.X) * sx), 0, bounds.Width);
            int y0 = Math.Clamp((int)(Math.Min(start.Y, end.Y) * sy), 0, bounds.Height), y1 = Math.Clamp((int)(Math.Max(start.Y, end.Y) * sy), 0, bounds.Height);
            if (x1 - x0 < 4 || y1 - y0 < 4) { box.Visibility = Visibility.Collapsed; return; }
            Drawing.Bitmap crop;
            try { crop = shot.Clone(new Drawing.Rectangle(x0, y0, x1 - x0, y1 - y0), shot.PixelFormat); }
            catch (Exception error) when (error is ArgumentException or OutOfMemoryException)
            {
                Cancel();
                return;
            }
            finished = true;
            Selected?.Invoke(crop);
        }

        private void Cancel()
        {
            if (finished) return;
            finished = true;
            Cancelled?.Invoke();
        }

        [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    }
}
