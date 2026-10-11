using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Y700Switch2V60Viiper;

public partial class ManualConnectWindow : Window
{
    private readonly ManualConnectionViewModel model;
    private bool allowClose, closing;
    public bool Connected { get; private set; }
    public ManualConnectWindow(ManualConnectionViewModel model)
    {
        this.model = model;
        InitializeComponent(); DataContext = model;
        Loaded += async (_, _) => await model.RefreshAsync();
        model.ConnectionSucceeded += OnConnectionSucceeded;
        Closing += OnClosing;
        Closed += (_, _) => model.ConnectionSucceeded -= OnConnectionSucceeded;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); }
        };
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await model.RefreshAsync();
    private async void Candidate_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag is Pro2ManualCandidate candidate) await model.ConnectAsync(candidate);
    }
    private void Return_Click(object sender, RoutedEventArgs e) => Close();
    private void OnConnectionSucceeded()
    {
        Connected = true; allowClose = true; Close();
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        try { await model.StopAsync(); }
        finally { allowClose = true; Close(); }
    }
}
