namespace Nadfny;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Keep spare pool threads so progress events and UI requests stay instant while big deletes run.
        ThreadPool.GetMinThreads(out var worker, out var io);
        ThreadPool.SetMinThreads(Math.Max(worker, 32), io);
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
