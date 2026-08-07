using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ManualBuilder;

/// <summary>
/// Renders a real window to a PNG. Not a screen capture - the window is drawn
/// straight to a bitmap, so nothing has to be on screen, nothing flashes past,
/// and the result is the same whatever else the machine is doing.
/// </summary>
internal static class Shots
{
    /// <summary>
    /// Show a window off to one side, let it settle, and draw it to a file.
    /// It has to be shown: an unshown window has no size and no item containers,
    /// so the picture would be an empty shell of a layout.
    /// </summary>
    /// <param name="readyWhen">
    /// Optional test for "this window has finished doing whatever it does on
    /// open". When given, <paramref name="settle"/> becomes a ceiling rather
    /// than a fixed wait.
    /// </param>
    public static string Capture(
        Window window, string outPath, int width, int height, TimeSpan? settle = null,
        Func<Window, bool>? readyWhen = null)
    {
        NotReady = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;

        // On screen, unavoidably. The window has to be shown or it has no size and
        // generates no list rows, and it has to be opaque or it renders faint - and
        // WPF draws nothing at all for a window placed off the desktop, so moving
        // it out of sight produces blank pictures. Running this tool therefore
        // flashes each window up for a few seconds; run it when the machine is not
        // in use.
        window.Left = 0;
        window.Top = 0;
        window.Width = width;
        window.Height = height;
        window.Opacity = 0;                 // present enough to lay out, invisible to the user
        window.ShowInTaskbar = false;
        window.ShowActivated = false;

        // Mica draws nothing of its own - the desktop shows through. Rendered to a
        // bitmap there is no desktop, so the result is a transparent sheet with a
        // few controls floating on it. Turn the backdrop off and paint the theme's
        // own background instead.
        if (window is Wpf.Ui.Controls.FluentWindow fluent)
            fluent.WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;

        if (window.Background is null or SolidColorBrush { Color.A: 0 })
            window.Background = Application.Current.TryFindResource("ApplicationBackgroundBrush")
                                as Brush ?? Brushes.Transparent;

        window.Show();
        window.UpdateLayout();
        Pump();

        // Let it finish what it started - a scan, artwork loading, a tool check.
        // Capturing too early gives a spinner and an empty list.
        //
        // A fixed wait is a guess, and the guess was wrong for the main window:
        // nine seconds produced a picture of the app still scanning. Where the
        // caller can say what "ready" looks like, wait for that instead and only
        // fall back to the clock as a ceiling.
        if (readyWhen is null)
        {
            Settle(settle ?? TimeSpan.FromSeconds(2));
        }
        else
        {
            var deadline = DateTime.UtcNow + (settle ?? TimeSpan.FromSeconds(30));
            while (DateTime.UtcNow < deadline && !readyWhen(window))
            {
                PumpDeep();
                Thread.Sleep(50);
            }

            NotReady = !readyWhen(window);

            // Even once it says it is ready, the containers it just created are
            // generated at background priority and need a moment to exist.
            Settle(TimeSpan.FromSeconds(1.5));
        }

        // Render the window as the desktop actually laid it out. Arranging the
        // content to a size of its own was tried, to get the wide layout on a
        // narrow session, and it renders blank - the layout system puts it back
        // before the bitmap is taken. Whatever size the window really is, is what
        // gets photographed.
        window.Opacity = 1;
        window.UpdateLayout();
        Pump();

        var dpi = VisualTreeHelper.GetDpi(window);
        var w = (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX);
        var h = (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY);

        if (w <= 0 || h <= 0) throw new InvalidOperationException($"{outPath}: laid out at {w}x{h}");

        // Rendering a live window is not wholly reliable - occlusion, compositing
        // and timing all conspire, and a bad take is a flat sheet of one colour
        // rather than an error. Check what came out and take it again if there is
        // nothing on it, rather than writing a blank page into the manual.
        RenderTargetBitmap bmp;
        var attempt = 0;

        while (true)
        {
            bmp = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bmp.Render(window);

            if (LooksDrawn(bmp) || ++attempt >= 4) break;

            window.Activate();          // occluded windows sometimes come out empty
            Settle(TimeSpan.FromSeconds(1.5));
            window.UpdateLayout();
        }

        if (!LooksDrawn(bmp))
            throw new InvalidOperationException($"{outPath}: rendered blank after {attempt} attempts");

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        using (var fs = File.Create(outPath)) encoder.Save(fs);

        window.Close();
        Pump();

        return outPath;
    }

    /// <summary>
    /// Whether anything was actually drawn. A failed render is a single flat
    /// colour - transparent, white or black - so a picture with almost no variety
    /// in it is a blank one however plausible its dimensions.
    /// </summary>
    private static bool LooksDrawn(RenderTargetBitmap bmp)
    {
        var stride = bmp.PixelWidth * 4;
        var pixels = new byte[stride * bmp.PixelHeight];
        bmp.CopyPixels(pixels, stride, 0);

        // Sample rather than walk every pixel - this runs on millions of them.
        var seen = new HashSet<uint>();

        for (var y = 0; y < bmp.PixelHeight; y += 7)
        for (var x = 0; x < bmp.PixelWidth; x += 7)
        {
            var i = y * stride + x * 4;
            seen.Add(BitConverter.ToUInt32(pixels, i));

            if (seen.Count > 24) return true;   // plenty going on
        }

        return seen.Count > 24;
    }

    /// <summary>
    /// Keep the dispatcher turning for a while. Background work - scanning, loading
    /// posters, checking for tools - finishes on this thread, so simply sleeping
    /// would guarantee it never does.
    /// </summary>
    /// <summary>True when the last capture gave up waiting rather than becoming ready.</summary>
    public static bool NotReady { get; private set; }

    public static void Settle(TimeSpan howLong)
    {
        var until = DateTime.UtcNow + howLong;
        while (DateTime.UtcNow < until)
        {
            Pump();
            Thread.Sleep(25);
        }
        Pump();
    }

    /// <summary>Let the dispatcher finish - containers are generated at background priority.</summary>
    public static void Pump() => PumpTo(DispatcherPriority.ContextIdle);

    /// <summary>
    /// Drain right down to the bottom of the queue.
    ///
    /// The sentinel that ends the frame has to be below the work being waited
    /// for, or it wins the race. At ContextIdle it beat the continuation of an
    /// awaited task, so every pump exited before the continuation ran and
    /// re-posting the sentinel each time meant it never ran at all - the main
    /// window sat on "Scanning..." forever and the manual shipped a picture of
    /// it. Used only for waiting: the ordinary Pump is left alone, because it is
    /// the one the rendering path uses and that was never the thing at fault.
    /// </summary>
    public static void PumpDeep() => PumpTo(DispatcherPriority.SystemIdle);

    private static void PumpTo(DispatcherPriority priority)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            priority, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
