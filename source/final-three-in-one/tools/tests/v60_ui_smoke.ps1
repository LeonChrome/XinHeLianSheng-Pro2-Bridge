param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath,
    [double]$MinimumFeedHz = 200.0,
    [int]$ApiPort = 3242,
    [switch]$SkipServerFaultTest,
    [switch]$AllowExistingServer
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class V60WindowControl
{
    [DllImport("user32.dll")]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
}
"@

function Wait-Until {
    param(
        [scriptblock]$Condition,
        [int]$TimeoutSeconds = 15,
        [string]$Failure = "Timed out waiting for condition."
    )

    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if (& $Condition) {
            return
        }
        Start-Sleep -Milliseconds 200
    }
    throw $Failure
}

function Find-Button {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name
    )

    $condition = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $Name)))
    $button = $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
    if ($null -eq $button) {
        throw "Button not found: $Name"
    }
    return $button
}

function Invoke-Button {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name
    )

    $button = Find-Button -Root $Root -Name $Name
    Wait-Until -TimeoutSeconds 10 -Failure "Button did not become enabled: $Name" -Condition {
        $button.Current.IsEnabled
    }
    $pattern = [System.Windows.Automation.InvokePattern]$button.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Get-EditControls {
    param([System.Windows.Automation.AutomationElement]$Root)

    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    return $Root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
}

function Find-Edit {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name
    )

    $condition = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Edit)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $Name)))
    $edit = $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
    if ($null -eq $edit) {
        throw "Edit control not found: $Name"
    }
    return $edit
}

function Expand-Section {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$Name
    )

    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    $element = $Root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
    if ($null -eq $element) {
        throw "Expandable section not found: $Name"
    }

    $pattern = [System.Windows.Automation.ExpandCollapsePattern]$element.GetCurrentPattern(
        [System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($pattern.Current.ExpandCollapseState -ne
        [System.Windows.Automation.ExpandCollapseState]::Expanded) {
        $pattern.Expand()
        Start-Sleep -Milliseconds 400
    }
}

function Get-Value {
    param([System.Windows.Automation.AutomationElement]$Element)

    $pattern = [System.Windows.Automation.ValuePattern]$Element.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)
    return $pattern.Current.Value
}

function Set-Value {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [string]$Value
    )

    $pattern = [System.Windows.Automation.ValuePattern]$Element.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue($Value)
}

function Get-PresentIdentity {
    param([string]$Pattern)

    return @(
        Get-PnpDevice -PresentOnly |
            Where-Object { $_.InstanceId -match $Pattern }
    )
}

$resolvedExe = (Resolve-Path -LiteralPath $ExePath).Path
$testStartedAt = Get-Date
$existingServers = @(Get-Process viiper -ErrorAction SilentlyContinue)
if ($existingServers.Count -gt 0 -and !$AllowExistingServer) {
    throw "Refusing to run while an existing VIIPER process is active: $($existingServers.Id -join ',')."
}
$existingServerIds = @($existingServers | ForEach-Object { $_.Id })
$previousSmokeFlag = $env:V60_UI_SMOKE
$env:V60_UI_SMOKE = "1"
$process = Start-Process `
    -FilePath $resolvedExe `
    -WorkingDirectory (Split-Path -Parent $resolvedExe) `
    -PassThru
$env:V60_UI_SMOKE = $previousSmokeFlag
$root = $null

try {
    Wait-Until -TimeoutSeconds 15 -Failure "V6 window was not created." -Condition {
        $script:root = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children,
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
                $process.Id)))
        $null -ne $script:root
    }

    Expand-Section -Root $root -Name "系统控制台"
    $hostEdit = Find-Edit -Root $root -Name "VIIPER Host"
    $portEdit = Find-Edit -Root $root -Name "VIIPER Port"
    $logEdit = Find-Edit -Root $root -Name "实时日志"

    Set-Value -Element $hostEdit -Value "127.0.0.1"
    Set-Value -Element $portEdit -Value $ApiPort.ToString()

    $rumbleSliderCondition = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Slider)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            "震动倍率")))
    $rumbleSlider = $root.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $rumbleSliderCondition)
    if ($null -eq $rumbleSlider) {
        throw "Rumble multiplier slider was not found."
    }
    $rumbleRange = [System.Windows.Automation.RangeValuePattern]$rumbleSlider.GetCurrentPattern(
        [System.Windows.Automation.RangeValuePattern]::Pattern)
    $originalRumbleGain = $rumbleRange.Current.Value
    $rumbleRange.SetValue(2.5)
    Wait-Until -TimeoutSeconds 10 -Failure "Rumble multiplier did not update." -Condition {
        (Get-Value $logEdit).Contains("[RUMBLE_GAIN] multiplier=2.5")
    }
    $rumbleRange.SetValue($originalRumbleGain)
    Write-Output "[V60_UI_SMOKE] rumble_multiplier_0_to_3=result=pass"

    Invoke-Button -Root $root -Name "启动本地 VIIPER"
    Wait-Until -TimeoutSeconds 15 -Failure "Local VIIPER did not answer ping." -Condition {
        (Get-Value $logEdit).Contains("[PING]")
    }

    Invoke-Button -Root $root -Name "启动本地 VIIPER"
    Wait-Until -TimeoutSeconds 10 -Failure "Existing VIIPER process was not reused." -Condition {
        $text = Get-Value $logEdit
        $text.Contains("[VIIPER_SERVER] already_running") -or
            $text.Contains("[VIIPER_SERVER] using existing server")
    }

    $modes = @(
        @{
            Button = "启动 新和联胜 / PS5";
            Label = "新和联胜 / PS5";
            Identity = "VID_054C&PID_0CE6";
        },
        @{
            Button = "启动 Pro2 / Nintendo";
            Label = "Pro2 / Nintendo";
            Identity = "VID_057E&PID_2069";
        },
        @{
            Button = "启动 Xbox / XInput";
            Label = "Xbox / XInput";
            Identity = "VID_045E&PID_028E";
        }
    )

    foreach ($mode in $modes) {
        $before = (Get-Value $logEdit).Length
        Invoke-Button -Root $root -Name $mode.Button
        Wait-Until -TimeoutSeconds 20 -Failure "Mode did not start: $($mode.Label)" -Condition {
            $text = Get-Value $logEdit
            $text.Length -gt $before -and
                $text.Substring($before).Contains("[VIIPER] added $($mode.Label)")
        }
        Wait-Until -TimeoutSeconds 12 -Failure "Mode did not report feed rate: $($mode.Label)" -Condition {
            $text = Get-Value $logEdit
            $suffix = $text.Substring([Math]::Min($before, $text.Length))
            $suffix -match ([regex]::Escape($mode.Label) + " frames target_hz=250\.0 actual_hz=([0-9.]+)")
        }

        $text = Get-Value $logEdit
        $suffix = $text.Substring([Math]::Min($before, $text.Length))
        $rates = [regex]::Matches(
            $suffix,
            [regex]::Escape($mode.Label) + " frames target_hz=250\.0 actual_hz=([0-9.]+)")
        $rate = [double]$rates[$rates.Count - 1].Groups[1].Value
        if ($rate -lt $MinimumFeedHz) {
            throw "$($mode.Label) feed rate $rate Hz is below $MinimumFeedHz Hz."
        }

        Wait-Until -TimeoutSeconds 15 -Failure "USB identity not present: $($mode.Identity)" -Condition {
            (Get-PresentIdentity -Pattern $mode.Identity).Count -gt 0
        }

        $stopOffset = (Get-Value $logEdit).Length
        Invoke-Button -Root $root -Name "停止虚拟设备"
        Wait-Until -TimeoutSeconds 15 -Failure "Mode did not clean up: $($mode.Label)" -Condition {
            $text = Get-Value $logEdit
            $text.Length -gt $stopOffset -and
                $text.Substring($stopOffset).Contains("[VIIPER] removed device")
        }
        Write-Output (
            "[V60_UI_SMOKE] mode={0} rate_hz={1:F1} identity={2} result=pass" -f
            $mode.Label,
            $rate,
            $mode.Identity)
    }

    $switchOffset = (Get-Value $logEdit).Length
    Invoke-Button -Root $root -Name "启动 新和联胜 / PS5"
    Wait-Until -TimeoutSeconds 20 -Failure "Direct-switch source mode did not start." -Condition {
        $text = Get-Value $logEdit
        $text.Length -gt $switchOffset -and
            $text.Substring($switchOffset).Contains("[VIIPER] added 新和联胜 / PS5")
    }
    Invoke-Button -Root $root -Name "启动 Xbox / XInput"
    Wait-Until -TimeoutSeconds 25 -Failure "Direct switch from PS5 to Xbox did not complete." -Condition {
        $text = Get-Value $logEdit
        $suffix = $text.Substring([Math]::Min($switchOffset, $text.Length))
        $suffix.Contains("[MODE_SWITCH] from=新和联胜 / PS5 to=Xbox / XInput") -and
            $suffix.Contains("[VIIPER] added Xbox / XInput")
    }
    Wait-Until -TimeoutSeconds 15 -Failure "Xbox identity missing after direct switch." -Condition {
        (Get-PresentIdentity -Pattern "VID_045E&PID_028E").Count -gt 0
    }

    $backgroundOffset = (Get-Value $logEdit).Length
    [void][V60WindowControl]::ShowWindowAsync(
        [IntPtr]$root.Current.NativeWindowHandle,
        6)
    Wait-Until -TimeoutSeconds 15 -Failure "Background cadence was not reported." -Condition {
        $text = Get-Value $logEdit
        $suffix = $text.Substring([Math]::Min($backgroundOffset, $text.Length))
        $suffix -match "Xbox / XInput frames target_hz=250\.0 actual_hz=([0-9.]+)"
    }
    $text = Get-Value $logEdit
    $suffix = $text.Substring([Math]::Min($backgroundOffset, $text.Length))
    $backgroundRates = [regex]::Matches(
        $suffix,
        "Xbox / XInput frames target_hz=250\.0 actual_hz=([0-9.]+)")
    $backgroundRate = [double]$backgroundRates[$backgroundRates.Count - 1].Groups[1].Value
    if ($backgroundRate -lt $MinimumFeedHz) {
        throw "Background feed rate $backgroundRate Hz is below $MinimumFeedHz Hz."
    }
    [void][V60WindowControl]::ShowWindowAsync(
        [IntPtr]$root.Current.NativeWindowHandle,
        9)
    Write-Output (
        "[V60_UI_SMOKE] background_rate_hz={0:F1} result=pass" -f
        $backgroundRate)

    $switchStopOffset = (Get-Value $logEdit).Length
    Invoke-Button -Root $root -Name "停止虚拟设备"
    Wait-Until -TimeoutSeconds 15 -Failure "Direct-switch target did not stop cleanly." -Condition {
        $text = Get-Value $logEdit
        $text.Length -gt $switchStopOffset -and
            $text.Substring($switchStopOffset).Contains("[VIIPER] removed device")
    }
    Write-Output "[V60_UI_SMOKE] direct_mode_switch=result=pass"

    Set-Value -Element $portEdit -Value "70000"
    $invalidOffset = (Get-Value $logEdit).Length
    Invoke-Button -Root $root -Name "Ping VIIPER"
    Wait-Until -TimeoutSeconds 10 -Failure "Invalid port was not rejected." -Condition {
        $text = Get-Value $logEdit
        $text.Length -gt $invalidOffset -and
            $text.Substring($invalidOffset).Contains("Port 必须是 1 到 65535")
    }
    Set-Value -Element $portEdit -Value $ApiPort.ToString()
    Write-Output "[V60_UI_SMOKE] invalid_port=result=pass"

    $scanOffset = (Get-Value $logEdit).Length
    Invoke-Button -Root $root -Name "扫描 Pro2 BLE"
    Wait-Until -TimeoutSeconds 15 -Failure "No-Pro2 scan did not complete." -Condition {
        $text = Get-Value $logEdit
        $text.Length -gt $scanOffset -and
            $text.Substring($scanOffset).Contains("[PRO2_BLE] scan none")
    }
    if ($process.HasExited) {
        throw "V6 process exited during no-Pro2 BLE scan."
    }
    Write-Output "[V60_UI_SMOKE] no_pro2_scan=result=pass"

    $enterOffset = (Get-Value $logEdit).Length
    Invoke-Button -Root $root -Name "连接 Pro2 BLE"
    Wait-Until -TimeoutSeconds 25 -Failure "Enter-game flow did not deploy the selected mode." -Condition {
        $text = Get-Value $logEdit
        $suffix = $text.Substring([Math]::Min($enterOffset, $text.Length))
        $suffix.Contains("[VIIPER] added Xbox / XInput") -and
            $suffix.Contains("[PRO2_BLE] scan none")
    }
    Wait-Until -TimeoutSeconds 8 -Failure "Automatic Pro2 reconnect did not begin a second attempt." -Condition {
        $text = Get-Value $logEdit
        $suffix = $text.Substring([Math]::Min($enterOffset, $text.Length))
        $suffix.Contains("[PRO2_AUTO] attempt=2 begin.")
    }
    Invoke-Button -Root $root -Name "停止自动重连并断开"
    Wait-Until -TimeoutSeconds 12 -Failure "Automatic Pro2 reconnect did not stop on request." -Condition {
        $text = Get-Value $logEdit
        $suffix = $text.Substring([Math]::Min($enterOffset, $text.Length))
        $suffix.Contains("[PRO2_AUTO] cancelled.")
    }
    $enterStopOffset = (Get-Value $logEdit).Length
    Invoke-Button -Root $root -Name "停止虚拟设备"
    Wait-Until -TimeoutSeconds 15 -Failure "Enter-game neutral mode did not stop." -Condition {
        $text = Get-Value $logEdit
        $text.Length -gt $enterStopOffset -and
            $text.Substring($enterStopOffset).Contains("[VIIPER] removed device")
    }
    Write-Output "[V60_UI_SMOKE] enter_game_auto_retry_and_manual_stop=result=pass"

    if (!$SkipServerFaultTest -and $existingServerIds.Count -eq 0) {
        $faultOffset = (Get-Value $logEdit).Length
        Invoke-Button -Root $root -Name "启动 Xbox / XInput"
        Wait-Until -TimeoutSeconds 20 -Failure "Fault-test mode did not start." -Condition {
            $text = Get-Value $logEdit
            $text.Length -gt $faultOffset -and
                $text.Substring($faultOffset).Contains("[VIIPER] added Xbox / XInput")
        }
        $text = Get-Value $logEdit
        $serverMatches = [regex]::Matches(
            $text,
            "\[VIIPER_SERVER\] started pid=([0-9]+)")
        if ($serverMatches.Count -eq 0) {
            throw "Fault test could not identify the VIIPER child PID."
        }
        $serverPid = [int]$serverMatches[$serverMatches.Count - 1].Groups[1].Value
        Stop-Process -Id $serverPid -Force
        Wait-Until -TimeoutSeconds 25 -Failure "V6 did not detect the killed VIIPER stream." -Condition {
            $text = Get-Value $logEdit
            $text.Length -gt $faultOffset -and
                $text.Substring($faultOffset).Contains("loop failed")
        }
        Wait-Until -TimeoutSeconds 35 -Failure "V6 did not auto-recover the killed VIIPER session." -Condition {
            $text = Get-Value $logEdit
            $suffix = $text.Substring([Math]::Min($faultOffset, $text.Length))
            $suffix.Contains("[VIIPER_SERVER] previous process exited") -and
                $suffix.Contains("[VIIPER_RECOVERY] restarted mode=Xbox / XInput")
        }
        Write-Output "[V60_UI_SMOKE] server_fault_auto_recovery=result=pass"
    }

    $windowPattern = [System.Windows.Automation.WindowPattern]$root.GetCurrentPattern(
        [System.Windows.Automation.WindowPattern]::Pattern)
    $windowPattern.Close()
    Wait-Until -TimeoutSeconds 15 -Failure "V6 process did not exit after window close." -Condition {
        $process.Refresh()
        $process.HasExited
    }
    if ($process.ExitCode -ne 0) {
        throw "V6 process exited abnormally with code $($process.ExitCode)."
    }
    Start-Sleep -Milliseconds 800
    if ($existingServerIds.Count -gt 0) {
        foreach ($serverId in $existingServerIds) {
            if ($null -eq (Get-Process -Id $serverId -ErrorAction SilentlyContinue)) {
                throw "V6 stopped externally owned VIIPER process $serverId."
            }
        }
        Write-Output "[V60_UI_SMOKE] external_server_preserved=result=pass"
    }
    else {
        $orphan = Get-Process viiper -ErrorAction SilentlyContinue
        if ($orphan) {
            throw "VIIPER process remained after V6 window closed: $($orphan.Id -join ',')."
        }
    }

    $managerLog = Get-ChildItem `
        "$env:LOCALAPPDATA\PRO2WirelessReceiverControlBoard\v6_logs\manager_*.log" `
        -File `
        -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $testStartedAt.AddSeconds(-2) } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $managerLog) {
        throw "Persistent manager session log was not created."
    }
    $managerText = Get-Content -LiteralPath $managerLog.FullName -Raw
    if (!$managerText.Contains("[START]") -or
        !$managerText.Contains("[SHUTDOWN] complete.")) {
        throw "Persistent manager session log is incomplete: $($managerLog.FullName)"
    }
    Write-Output "[V60_UI_SMOKE] persistent_log=result=pass path=$($managerLog.FullName)"
    Write-Output "[V60_UI_SMOKE] shutdown_cleanup=result=pass"
}
catch {
    Write-Output "==== V60 UI FAILURE DIAGNOSTICS ===="
    $process.Refresh()
    Write-Output "process_exited=$($process.HasExited)"
    if ($process.HasExited) {
        Write-Output "exit_code=$($process.ExitCode)"
    }
    elseif ($null -ne $logEdit) {
        try {
            Write-Output (Get-Value $logEdit)
        }
        catch {
            Write-Output "log_unavailable=$($_.Exception.Message)"
        }
    }
    throw
}
finally {
    if (!$process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        if (!$process.WaitForExit(10000)) {
            $process.Kill()
        }
    }
}
