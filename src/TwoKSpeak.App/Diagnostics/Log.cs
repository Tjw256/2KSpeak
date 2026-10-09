using TwoKSpeak.Engine;

namespace TwoKSpeak.App.Diagnostics;

/// <summary>Append-only text log in LocalAppData. Never records transcript text.</summary>
public static class Log
{
    private static readonly Lock Gate = new();
    private static readonly string FilePath = Path.Combine(AppPaths.Logs, "app.log");

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.Logs);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Logging must never take dictation down.
            }
        }
    }
}
