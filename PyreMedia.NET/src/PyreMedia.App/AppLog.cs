using System.IO;
using System.Text;

namespace PyreMedia.App;

/// <summary>
/// Plain-text log next to the settings. Exists so an error report is a file with
/// a stack trace rather than a message box saying "unexpected error".
/// </summary>
public static class AppLog
{
    private static readonly Lock Gate = new();

    // Computed, not captured once: a tool that redirects the app-data folder has
    // to be able to do so before the first line is written.
    public static string FilePath => Path.Combine(PyreMedia.Core.AppPaths.Folder, "pyremedia.log");

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string context, Exception ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine(context);

        for (var e = ex; e is not null; e = e.InnerException)
        {
            sb.AppendLine($"  {e.GetType().FullName}: {e.Message}");
            if (!string.IsNullOrWhiteSpace(e.StackTrace))
                sb.AppendLine(e.StackTrace);
        }

        Write("ERROR", sb.ToString());
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                // Keep the file from growing without bound - but roll it over
                // rather than delete it. Deleting threw away the run leading up to
                // whatever went wrong, which is the part worth having; the answer
                // to "why did my folder list vanish" was in lines that old.
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 2 * 1024 * 1024)
                {
                    try { File.Move(FilePath, FilePath + ".old", overwrite: true); }
                    catch (IOException) { File.Delete(FilePath); }
                }

                File.AppendAllText(FilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Logging must never itself break the app.
        }
    }
}
