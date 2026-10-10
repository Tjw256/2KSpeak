using System.Diagnostics;
using TwoKSpeak.App.Diagnostics;
using TwoKSpeak.App.Settings;
using TwoKSpeak.Engine;
using Velopack;

namespace TwoKSpeak.App;

public static class Program
{
    /// <summary>
    /// Velopack runs first: the installer and uninstaller start this exe with hook arguments, handled here before any
    /// window exists, and a downloaded update is applied before the app starts.
    /// </summary>
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => Autostart.Apply(true, Environment.ProcessPath!))
            .OnBeforeUninstallFastCallback(_ => RemoveUserData())
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    /// <summary>
    /// The installer folder holds only the app; models, settings and history live outside it (Setup wipes its own
    /// folder on a repair, and the downloads must survive that), so uninstalling removes them here.
    /// </summary>
    private static void RemoveUserData()
    {
        try
        {
            Autostart.Apply(false, "");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Write($"uninstall: autostart not removed: {ex.Message}");
        }
        // Velopack has just killed the app, but the worker and llama-server (which lives outside the install folder)
        // may still hold the CUDA DLLs for a moment. The hook has 60 s; give the files up to 15 s to come free.
        StopHelpers();
        foreach (var directory in new[] { AppPaths.LocalRoot, AppPaths.RoamingRoot })
        {
            for (var attempt = 1; Directory.Exists(directory); attempt++)
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex) when ((ex is UnauthorizedAccessException or IOException) && attempt < 15)
                {
                    Thread.Sleep(1000);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    break; // still locked; leave the rest rather than fail the uninstall
                }
            }
        }
    }

    private static void StopHelpers()
    {
        foreach (var process in Process.GetProcessesByName("llama-server").Concat(Process.GetProcessesByName("2KSpeak.Worker")))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName ?? "";
                    if (path.StartsWith(AppPaths.LocalRoot, StringComparison.OrdinalIgnoreCase)
                        || path.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Already gone, or not ours to inspect.
                }
            }
        }
    }
}
