using System.Diagnostics;
using MiniRedis.App.Ui;
using MiniRedis.Core.Storage;

namespace MiniRedis.App;

internal static class SmokeRunner
{
    public static int Run(string outputPath)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var storage = AppStorage.Open(AppContext.BaseDirectory);
            using var form = new MainForm(storage, new WindowsDpapiProtector());
            form.Show();
            Application.DoEvents();
            watch.Stop();
            var process = Process.GetCurrentProcess();
            process.Refresh();
            File.WriteAllText(outputPath,
                "ok=true\n" +
                "startup_ms=" + watch.ElapsedMilliseconds + "\n" +
                "working_set_bytes=" + process.WorkingSet64 + "\n" +
                "peak_working_set_bytes=" + process.PeakWorkingSet64 + "\n");
            form.Close();
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(outputPath, "ok=false\nerror=" + ex.GetType().Name + ": " + ex.Message + "\n");
            }
            catch (IOException)
            {
            }

            return 1;
        }
    }
}
