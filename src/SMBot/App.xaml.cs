using System.IO;
using System.Windows;
using SMBot.Views;

namespace SMBot;

public partial class App : Application
{
    static readonly string ErrorLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SMBot", "error.log");

    // Only one SMBot per Windows user. Starting it again signals the running one instead
    // ("show yourself", or "open the checklist" for --checklist) and exits.
    const string InstanceName = @"Local\SMBot-single-instance";
    const string ShowSignal = @"Local\SMBot-show", ChecklistSignal = @"Local\SMBot-checklist";
    static Mutex? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Write any crash to %LOCALAPPDATA%\SMBot\error.log so it can be diagnosed
        DispatcherUnhandledException += (_, args) => LogError(args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogError(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => LogError(args.Exception);
        base.OnStartup(e);

        var wantsChecklist = e.Args.Contains("--checklist");
        _instance = new Mutex(true, InstanceName, out var isFirst);
        if (!isFirst)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(wantsChecklist ? ChecklistSignal : ShowSignal);
                signal.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { /* the other one is still starting up */ }
            Shutdown();
            return;
        }

        var bot = new BuddyWindow();
        MainWindow = bot;
        bot.Show();
        Listen(ShowSignal, bot.BringForward);
        Listen(ChecklistSignal, bot.OpenChecklist);
    }

    // Waits in the background for another launch's signal and runs the action on the UI thread
    void Listen(string name, Action action)
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        new Thread(() =>
        {
            while (signal.WaitOne())
                Dispatcher.BeginInvoke(action);
        }) { IsBackground = true, Name = name }.Start();
    }

    static void LogError(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ErrorLog)!);
            File.AppendAllText(ErrorLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }
}
