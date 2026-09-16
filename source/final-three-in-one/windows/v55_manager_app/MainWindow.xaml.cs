using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Y700Switch2V55Manager;

public partial class MainWindow : Window
{
    private bool logScrollPending;
    private bool firstRunOfferHandled;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel(this);
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (firstRunOfferHandled || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        firstRunOfferHandled = true;
        await viewModel.WaitForInitializationAsync();
        if (!viewModel.ShouldOfferFirstRunGuide)
        {
            return;
        }

        viewModel.MarkFirstRunGuideOffered();
        MessageBoxResult answer = MessageBox.Show(
            this,
            "检测到这是 V5.9.19 ESP 控制台的首次运行。\n\n是否打开双 USB 接线、刷写、USB 身份验证和 Pro2 BLE 配对向导？",
            "首次使用向导",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes)
        {
            OpenFirstRunGuide(viewModel);
        }
    }

    private void OpenFirstRunGuide_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            OpenFirstRunGuide(viewModel);
        }
    }

    private void OpenFirstRunGuide(MainViewModel viewModel)
    {
        var guide = new FirstRunGuideWindow(viewModel)
        {
            Owner = this
        };
        guide.ShowDialog();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.Shutdown();
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        System.Windows.Application.Current.Shutdown(0);
    }

    private void LogBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        if (logScrollPending || textBox.Text.Length == 0)
        {
            return;
        }

        logScrollPending = true;
        textBox.Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                logScrollPending = false;
                if (!textBox.IsVisible || textBox.IsKeyboardFocusWithin)
                {
                    return;
                }

                textBox.CaretIndex = textBox.Text.Length;
                textBox.ScrollToEnd();
            }));
    }
}
