using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Y700Switch2V55Manager;

public partial class FirstRunGuideWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly FrameworkElement[] stepPanels;
    private readonly string[] stepTitles =
    {
        "接线准备",
        "检测控制板",
        "选择并刷写模式",
        "验证 USB 手柄身份",
        "配对真实 Pro2",
        "完成"
    };
    private int currentStep;
    private bool detectionPassed;
    private bool flashPassed;
    private bool usbVerified;
    private bool pairingPassed;
    private OutputModeId selectedMode = OutputModeId.Pro2;
    private OutputModeProfile? selectedProfile;

    public FirstRunGuideWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
        stepPanels =
        [
            Step0Panel,
            Step1Panel,
            Step2Panel,
            Step3Panel,
            Step4Panel,
            Step5Panel
        ];
        Loaded += (_, _) =>
        {
            StartCableAnimation();
            UpdateStep();
        };
    }

    private void StartCableAnimation()
    {
        var animation = new DoubleAnimation(0.45, 1.0, TimeSpan.FromMilliseconds(850))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        ControlCable.BeginAnimation(OpacityProperty, animation);
        var delayed = animation.Clone();
        delayed.BeginTime = TimeSpan.FromMilliseconds(400);
        DataCable.BeginAnimation(OpacityProperty, delayed);
    }

    private void UpdateStep()
    {
        for (int i = 0; i < stepPanels.Length; i++)
        {
            stepPanels[i].Visibility = i == currentStep ? Visibility.Visible : Visibility.Collapsed;
        }

        StepTitleText.Text = stepTitles[currentStep];
        StepCounterText.Text = "步骤 " + (currentStep + 1) + " / " + stepPanels.Length;
        BackButton.IsEnabled = currentStep > 0 && currentStep < stepPanels.Length - 1;
        NextButton.Content = currentStep == stepPanels.Length - 1 ? "完成" : "下一步";
        NextButton.IsEnabled = currentStep switch
        {
            0 => true,
            1 => detectionPassed,
            2 => flashPassed,
            3 => usbVerified,
            4 => pairingPassed,
            5 => true,
            _ => false
        };

        if (currentStep == stepPanels.Length - 1)
        {
            StartSuccessAnimation();
        }
    }

    private void SetBusy(bool busy, string message = "")
    {
        OperationText.Text = message;
        OperationOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BackButton.IsEnabled = !busy && currentStep > 0 && currentStep < stepPanels.Length - 1;
        if (!busy)
        {
            UpdateStep();
        }
    }

    private static void ShowResult(Border border, TextBlock title, TextBlock body, bool success, string heading, string text)
    {
        border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(success ? "#ECFDF5" : "#FEF2F2"));
        border.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(success ? "#6EE7B7" : "#FCA5A5"));
        title.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(success ? "#047857" : "#B91C1C"));
        title.Text = heading;
        body.Text = text;
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (currentStep == stepPanels.Length - 1)
        {
            DialogResult = true;
            Close();
            return;
        }

        currentStep++;
        UpdateStep();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (currentStep > 0)
        {
            currentStep--;
            UpdateStep();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void Detect_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "正在刷新串口并检查原生 USB...");
        try
        {
            GuideDetectionResult result = await viewModel.RunGuideDetectionAsync();
            detectionPassed = result.SerialReady;
            ShowResult(
                DetectionResultBorder,
                DetectionResultTitle,
                DetectionResultText,
                result.SerialReady,
                result.SerialReady ? "控制板检测通过" : "没有找到可刷写控制口",
                result.Summary + "\n\n" + result.NextAction);
        }
        catch (Exception ex)
        {
            detectionPassed = false;
            ShowResult(DetectionResultBorder, DetectionResultTitle, DetectionResultText, false, "检测失败", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "正在检查 CH343 驱动；若需要修复，请确认 UAC 弹窗...");
        try
        {
            await viewModel.RunGuideRepairCh343DriverAsync();
            ShowResult(
                DetectionResultBorder,
                DetectionResultTitle,
                DetectionResultText,
                true,
                "驱动处理已结束",
                viewModel.PortStatus + "\n\n" + viewModel.NextAction);
        }
        catch (Exception ex)
        {
            ShowResult(DetectionResultBorder, DetectionResultTitle, DetectionResultText, false, "驱动修复失败", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void FlashPs5_Click(object sender, RoutedEventArgs e) => StartFlash(OutputModeId.DualSenseStandard);
    private void FlashEdge_Click(object sender, RoutedEventArgs e) => StartFlash(OutputModeId.DualSenseLike);
    private void FlashPro2_Click(object sender, RoutedEventArgs e) => StartFlash(OutputModeId.Pro2);
    private void FlashXbox_Click(object sender, RoutedEventArgs e) => StartFlash(OutputModeId.Xbox);

    private async void StartFlash(OutputModeId mode)
    {
        selectedMode = mode;
        flashPassed = false;
        usbVerified = false;
        pairingPassed = false;
        SetBusy(true, "正在进入下载模式并刷写固件。不要拔线，也不要关闭程序...");
        try
        {
            GuideFlashResult result = await viewModel.RunGuideFlashAsync(mode);
            selectedProfile = result.Profile;
            flashPassed = result.Succeeded;
            string heading = result.Succeeded
                ? result.Profile.Label + " 刷写完成"
                : "刷写失败：" + result.FailureCategory;
            ShowResult(
                FlashResultBorder,
                FlashResultTitle,
                FlashResultText,
                result.Succeeded,
                heading,
                result.Summary + "\n\n" + result.NextAction);
            if (result.Succeeded)
            {
                ExpectedUsbText.Text = "目标模式：" + result.Profile.Label +
                                       "。Windows 应枚举出 " + result.Profile.ExpectedUsbMarker + "。";
                UsbResultText.Text = "重新插拔数据线后开始检查。";
            }
        }
        catch (Exception ex)
        {
            ShowResult(FlashResultBorder, FlashResultTitle, FlashResultText, false, "刷写失败", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        if (selectedProfile == null)
        {
            ShowResult(UsbResultBorder, UsbResultTitle, UsbResultText, false, "没有目标模式", "请返回上一步完成刷写。 ");
            return;
        }

        SetBusy(true, "正在读取 Windows USB / HID 枚举...");
        try
        {
            GuideUsbVerificationResult result = await viewModel.RunGuideUsbVerificationAsync(selectedMode);
            usbVerified = result.Succeeded;
            ShowResult(
                UsbResultBorder,
                UsbResultTitle,
                UsbResultText,
                result.Succeeded,
                result.Succeeded ? "目标手柄身份已确认" : "目标手柄尚未出现",
                result.Summary + "\n\n" + result.NextAction);
        }
        catch (Exception ex)
        {
            usbVerified = false;
            ShowResult(UsbResultBorder, UsbResultTitle, UsbResultText, false, "USB 检查失败", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OpenJoy_Click(object sender, RoutedEventArgs e) => viewModel.OpenControllerPanel();

    private async void AutoPair_Click(object sender, RoutedEventArgs e)
    {
        ManualFallbackPanel.Visibility = Visibility.Collapsed;
        SetBusy(true, "正在等待 Pro2 广播、连接和实时输入，最长约 35 秒...");
        try
        {
            GuidePairingResult result = await viewModel.RunGuideFirstPairingAsync();
            pairingPassed = result.Succeeded;
            ShowResult(
                PairResultBorder,
                PairResultTitle,
                PairResultText,
                result.Succeeded,
                result.Succeeded ? "Pro2 配对完成" : "自动配对尚未完成",
                result.Summary + "\n\n" + result.NextAction);
            if (result.Succeeded)
            {
                viewModel.MarkFirstRunGuideCompleted();
                currentStep = 5;
            }
            else
            {
                ManualFallbackPanel.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            pairingPassed = false;
            ManualFallbackPanel.Visibility = Visibility.Visible;
            ShowResult(PairResultBorder, PairResultTitle, PairResultText, false, "自动配对失败", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "正在扫描附近 BLE 设备，请保持 Pro2 配对灯闪烁...");
        try
        {
            GuidePairingResult result = await viewModel.RunGuideBleScanAsync();
            ManualResultText.Text = result.Summary + " " + result.NextAction;
        }
        catch (Exception ex)
        {
            ManualResultText.Text = "扫描失败：" + ex.Message;
        }
        finally
        {
            SetBusy(false);
            ManualFallbackPanel.Visibility = Visibility.Visible;
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "正在连接所选 Pro2，并等待实时输入...");
        try
        {
            GuidePairingResult result = await viewModel.RunGuideConnectSelectedAsync();
            pairingPassed = result.Succeeded;
            ShowResult(
                PairResultBorder,
                PairResultTitle,
                PairResultText,
                result.Succeeded,
                result.Succeeded ? "Pro2 配对完成" : "所选目标连接失败",
                result.Summary + "\n\n" + result.NextAction);
            if (result.Succeeded)
            {
                viewModel.MarkFirstRunGuideCompleted();
                currentStep = 5;
            }
        }
        catch (Exception ex)
        {
            pairingPassed = false;
            ShowResult(PairResultBorder, PairResultTitle, PairResultText, false, "连接失败", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void StartSuccessAnimation()
    {
        var scale = new DoubleAnimation(0.7, 1.0, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut }
        };
        var fade = new DoubleAnimation(0.15, 1.0, TimeSpan.FromMilliseconds(360));
        SuccessScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        SuccessScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale.Clone());
        SuccessBadge.BeginAnimation(OpacityProperty, fade);
    }
}
