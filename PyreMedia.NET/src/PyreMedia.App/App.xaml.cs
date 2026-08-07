using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Appearance;

namespace PyreMedia.App;

public partial class App : Application
{
    /// <summary>
    /// Held for the life of the process. Two copies running at once each keep
    /// their own picture of the settings in memory and write the whole file on
    /// save, so whichever closes last silently discards the other's changes -
    /// including a folder list. Clearing history has the same shape: it rewrites
    /// the log, dropping anything the other copy appended meanwhile.
    /// </summary>
    private static Mutex? _onlyOne;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _onlyOne = new Mutex(initiallyOwned: true, @"Local\PyreMedia.SingleInstance", out var isFirst);

        if (!isFirst)
        {
            AppLog.Info("Another copy is already running - bringing it to the front.");
            ActivateRunningCopy();
            Shutdown();
            return;
        }

        AppLog.Info($"--- PyreMedia starting (v{typeof(App).Assembly.GetName().Version}) ---");

        ApplicationThemeManager.ApplySystemTheme();

        DispatcherUnhandledException += OnDispatcherException;

        // A faulted fire-and-forget task surfaces here, not on the dispatcher.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                AppLog.Error("Fatal unhandled exception", ex);
        };
    }

    /// <summary>
    /// Put the copy that's already running in front, so launching again looks
    /// like it worked rather than like nothing happened.
    /// </summary>
    private static void ActivateRunningCopy()
    {
        try
        {
            var me = Environment.ProcessId;

            var other = Process.GetProcessesByName(
                    Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "PyreMedia")
                .FirstOrDefault(p => p.Id != me && p.MainWindowHandle != IntPtr.Zero);

            if (other is null) return;

            const int SW_RESTORE = 9;
            ShowWindow(other.MainWindowHandle, SW_RESTORE);
            SetForegroundWindow(other.MainWindowHandle);
        }
        catch (Exception)
        {
            // Best effort - failing to raise the window isn't worth an error.
        }
    }

    // DllImport rather than LibraryImport: the source-generated form needs
    // AllowUnsafeBlocks turned on for the whole project, which is a lot to give
    // up for two calls that only raise a window.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("Unhandled UI exception", e.Exception);

        var result = MessageBox.Show(
            $"{e.Exception.GetType().Name}: {e.Exception.Message}\n\n" +
            "The app will keep running, but the last action may not have completed.\n\n" +
            "Open the log file with the full details?",
            "PyreMedia error",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes && File.Exists(AppLog.FilePath))
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = AppLog.FilePath, UseShellExecute = true });
            }
            catch (Exception)
            {
                // Best effort only.
            }
        }

        e.Handled = true;
    }
}
