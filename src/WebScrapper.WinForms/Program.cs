namespace WebScrapper.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var form = new MainForm();
        using var smokeTimer = new System.Windows.Forms.Timer { Interval = 500 };
        if (args.Contains("--smoke-test", StringComparer.Ordinal))
        {
            smokeTimer.Tick += (_, _) => { smokeTimer.Stop(); form.Close(); };
            form.Shown += (_, _) => smokeTimer.Start();
        }
        Application.Run(form);
    }
}
