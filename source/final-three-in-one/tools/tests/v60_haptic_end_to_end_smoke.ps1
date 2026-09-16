param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath,
    [int]$ApiPort = 3242
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Wait-Until {
    param(
        [scriptblock]$Condition,
        [int]$TimeoutSeconds = 20,
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

function Get-Value {
    param([System.Windows.Automation.AutomationElement]$Element)

    $pattern = [System.Windows.Automation.ValuePattern]$Element.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)
    return $pattern.Current.Value
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

function Set-Value {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [string]$Value
    )

    $pattern = [System.Windows.Automation.ValuePattern]$Element.GetCurrentPattern(
        [System.Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue($Value)
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

$resolvedExe = (Resolve-Path -LiteralPath $ExePath).Path
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$audioProject = Join-Path $repoRoot "tools\tests\v60_haptic_audio_smoke\V60HapticAudioSmoke.csproj"
$startedAt = Get-Date
$existingViiper = @(Get-Process viiper -ErrorAction SilentlyContinue)
if ($existingViiper.Count -gt 0) {
    throw "Refusing to run with an existing VIIPER process: $($existingViiper.Id -join ',')."
}

$process = Start-Process `
    -FilePath $resolvedExe `
    -WorkingDirectory (Split-Path -Parent $resolvedExe) `
    -PassThru
$root = $null
$logEdit = $null

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
    Set-Value -Element (Find-Edit -Root $root -Name "VIIPER Host") -Value "127.0.0.1"
    Set-Value -Element (Find-Edit -Root $root -Name "VIIPER Port") -Value $ApiPort.ToString()
    $logEdit = Find-Edit -Root $root -Name "实时日志"

    Invoke-Button -Root $root -Name "启动本地 VIIPER"
    Wait-Until -Failure "Local VIIPER did not answer ping." -Condition {
        (Get-Value $logEdit).Contains("[PING]")
    }

    Invoke-Button -Root $root -Name "启动 新和联胜 / PS5"
    Wait-Until -TimeoutSeconds 30 -Failure "PS5 HD virtual device did not start." -Condition {
        $text = Get-Value $logEdit
        $text.Contains("[VIIPER] added 新和联胜 / PS5") -and
            $text.Contains("stream connected")
    }
    Wait-Until -TimeoutSeconds 20 -Failure "DualSense audio endpoint did not enumerate." -Condition {
        @(
            Get-PnpDevice -PresentOnly -Class AudioEndpoint -ErrorAction SilentlyContinue |
                Where-Object { $_.FriendlyName -like "*DualSense Wireless Controller*" }
        ).Count -gt 0
    }

    $audioOutput = dotnet run --project $audioProject -c Release 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Audio smoke failed:`n$($audioOutput -join [Environment]::NewLine)"
    }

    Wait-Until -TimeoutSeconds 15 -Failure "Manager did not receive DualSense HD audio feedback." -Condition {
        $text = Get-Value $logEdit
        $text.Contains("DualSense HD audio") -and
            $text.Contains("source=dualsense-hd-audio")
    }

    $viiperLog = Get-ChildItem `
        "$env:LOCALAPPDATA\PRO2WirelessReceiverControlBoard\v6_logs\viiper_server_*.log" `
        -File `
        -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $startedAt.AddSeconds(-2) } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $viiperLog) {
        throw "VIIPER server log was not created."
    }
    $viiperText = Get-Content -LiteralPath $viiperLog.FullName -Raw
    if (!$viiperText.Contains("haptic feedback stream is live") -or
        !$viiperText.Contains("kind=2")) {
        throw "VIIPER did not emit a kind=2 haptic frame: $($viiperLog.FullName)"
    }

    Invoke-Button -Root $root -Name "停止虚拟设备"
    Write-Output ($audioOutput -join [Environment]::NewLine)
    Write-Output "[V60_HAPTIC_E2E] viiper_kind2=result=pass"
    Write-Output "[V60_HAPTIC_E2E] manager_hd_scheduler=result=pass"
}
finally {
    if (!$process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        if (!$process.WaitForExit(10000)) {
            $process.Kill()
        }
    }
}
