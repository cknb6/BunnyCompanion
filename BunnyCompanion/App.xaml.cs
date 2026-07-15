using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using BunnyCompanion.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace BunnyCompanion;

public partial class App : Application
{
    private const string MutexName = @"Local\BunnyCompanion-9DF65B5E-735B-48CD-A81E-80C0A1ACBA2A";
    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, MutexName, out var isFirstInstance);
        _ownsMutex = isFirstInstance;
        if (!isFirstInstance)
        {
            MessageBox.Show("小申已经在桌面陪着你啦。\n请在右下角系统托盘中找到她。",
                "小申陪伴", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        var settingsService = new SettingsService();
        var settings = settingsService.Load();
        if (!StartupService.SetEnabled(settings.StartWithWindows))
        {
            settings.StartWithWindows = StartupService.IsEnabled();
            try
            {
                settingsService.Save(settings);
            }
            catch
            {
                // The desktop pet can still run when a locked-down profile blocks settings writes.
            }
        }

        var mainWindow = new MainWindow(settingsService, settings, e.Args);
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        MessageBox.Show("小申刚刚打了个盹，程序遇到了问题。错误记录已保存在本地配置目录。",
            "小申陪伴", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            WriteCrashLog(exception);
    }

    private static void WriteCrashLog(Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BunnyCompanion", "Logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{exception}\n\n");
        }
        catch
        {
            // A logging failure must never cause another application failure.
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (MainWindow is MainWindow mainWindow)
            mainWindow.PrepareForSessionEnd();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex)
            _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
