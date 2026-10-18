using LzFilm.Core;

namespace LzFilm.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.Message, "LzFilm", MessageBoxButtons.OK, MessageBoxIcon.Error);

        if (!Xz.IsAvailable)
        {
            MessageBox.Show(
                "liblzma.dll was not found or could not be loaded.\n\n" +
                "Build it with tools\\build-liblzma.ps1 (or copy liblzma.dll next to LzFilm.exe) and start again.",
                "LzFilm", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var form = new MainForm();
        // Opening an archive from Explorer ("Open with" / drag onto the exe) goes to the Restore tab.
        if (args.Length == 1 && File.Exists(args[0]))
            form.OpenArchiveOnStart(args[0]);
        Application.Run(form);
    }
}
