using System;
using System.Threading;
using System.Windows;

namespace Y700Switch2V60Viiper;

public partial class App : System.Windows.Application
{
    private Mutex? singleInstanceMutex;
    private bool ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupProcessGuard.CleanupConflictingProcesses();

        singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: @"Global\Y700Switch2V60ViiperManager",
            createdNew: out bool createdNew);
        ownsSingleInstanceMutex = createdNew;
        if (!ownsSingleInstanceMutex)
        {
            try
            {
                ownsSingleInstanceMutex = singleInstanceMutex.WaitOne(TimeSpan.FromSeconds(3));
            }
            catch (AbandonedMutexException)
            {
                ownsSingleInstanceMutex = true;
            }
        }

        if (!ownsSingleInstanceMutex)
        {
            System.Windows.MessageBox.Show(
                "已有一个新和联胜 VIIPER 管理器仍在关闭中。请等待几秒后重新打开。",
                "PRO2 控制板",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

        base.OnStartup(e);
        MainWindow window = new();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsSingleInstanceMutex)
        {
            try
            {
                singleInstanceMutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }

        singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
