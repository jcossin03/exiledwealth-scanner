using Velopack;

namespace EwCapture;

static class Program
{
    [STAThread]
    static void Main()
    {
        // First: lets the installer and updater run their hooks (install, update, uninstall).
        VelopackApp.Build().Run();
        // One tray icon, one hotkey owner.
        using var mutex = new Mutex(true, @"Local\ExiledWealthCapture", out var first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new App());
    }
}
