using MiniRedis.App.Ui;
using MiniRedis.Core.Storage;

namespace MiniRedis.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var smoke = Array.IndexOf(args, "--smoke");
        if (smoke >= 0)
        {
            var output = smoke + 1 < args.Length
                ? args[smoke + 1]
                : Path.Combine(AppContext.BaseDirectory, "smoke-metrics.txt");
            return SmokeRunner.Run(output);
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            var storage = AppStorage.Open(AppContext.BaseDirectory);
            Application.Run(new MainForm(storage, new WindowsDpapiProtector()));
            return 0;
        }
        catch (StorageNotWritableException ex)
        {
            MessageBox.Show(ex.Message, "Mini Redis", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
