param(
    [string]$IdfPath,
    [switch]$SkipFirmwareBuild,
    [switch]$SkipEmbeddedRefresh,
    [switch]$SkipDotnetInstall,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$ManagerRoot = Join-Path $RepoRoot "windows\v55_manager_app"
$ReleaseRoot = Join-Path $RepoRoot "release\v5.9"
$PublishRoot = Join-Path $ReleaseRoot "publish"
$PackageVersion = "5.9.19"
$PackageTag = "v$PackageVersion"
# Keep the script ASCII-safe so Windows PowerShell 5.1 cannot corrupt the Chinese file name.
$ChineseProductName = -join (@(0x65B0, 0x548C, 0x8054, 0x80DC, 0x7248, 0x672C) | ForEach-Object { [char]$_ })
$SingleExeName = "$ChineseProductName-aio-$PackageTag.exe"
$SingleExe = Join-Path $ReleaseRoot $SingleExeName
$LegacySingleExe = Join-Path $ReleaseRoot "Y700Switch2V55Manager-aio-$PackageTag.exe"
$HashFile = Join-Path $ReleaseRoot "SHA256SUMS-$PackageTag.txt"
$ReadmeFile = Join-Path $ReleaseRoot "README-$PackageTag.md"
$ReadmeSource = Join-Path $RepoRoot "tools\release_notes_v5_9_19.zh-CN.md"
$DotnetRoot = Join-Path $RepoRoot "work\dotnet"
. (Join-Path $RepoRoot "tools\esp32s3\idf_environment.ps1")

function Write-Step([string]$Name, [string]$Value) {
    Write-Output "[V5_9_PACKAGE] $Name=$Value"
}

function Remove-TreeWithRetry([string]$Path, [switch]$Required) {
    if (!(Test-Path -LiteralPath $Path)) { return }
    for ($attempt = 1; $attempt -le 8; $attempt++) {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            return
        } catch {
            if ($attempt -lt 8) {
                Start-Sleep -Milliseconds 500
            }
        }
    }
    if ($Required) {
        throw "Unable to remove directory after retries: $Path"
    }
    Write-Step "cleanup_deferred" $Path
}

function Remove-FileWithRetry([string]$Path, [switch]$Required) {
    if (!(Test-Path -LiteralPath $Path)) { return $true }
    for ($attempt = 1; $attempt -le 6; $attempt++) {
        try {
            Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
            return $true
        } catch {
            if ($attempt -lt 6) {
                Start-Sleep -Milliseconds 300
            }
        }
    }
    if ($Required) {
        throw "Unable to remove file after retries: $Path"
    }
    Write-Step "cleanup_skip_locked" $Path
    return $false
}

function Get-CSharpCompiler {
    $candidates = @(
        (Join-Path $env:WINDIR "Microsoft.NET\\Framework64\\v4.0.30319\\csc.exe"),
        (Join-Path $env:WINDIR "Microsoft.NET\\Framework\\v4.0.30319\\csc.exe")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    throw "csc.exe not found in the .NET Framework compiler locations."
}

function Build-CSharpTool([string]$SourcePath, [string]$OutputPath) {
    $compiler = Get-CSharpCompiler
    Write-Step "csharp_tool" ((Split-Path -Leaf $SourcePath) + " -> " + (Split-Path -Leaf $OutputPath))
    if ($DryRun) {
        return
    }

    & $compiler /nologo /target:exe /out:$OutputPath $SourcePath
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $OutputPath)) {
        throw "Failed to compile managed tool: $SourcePath"
    }
}

function Ensure-Dotnet {
    $local = Join-Path $DotnetRoot "dotnet.exe"
    if (Test-Path -LiteralPath $local) {
        return $local
    }

    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    if ($SkipDotnetInstall) {
        throw "dotnet SDK not found and -SkipDotnetInstall was set."
    }

    Write-Step "dotnet" "install_local_8.0"
    if ($DryRun) {
        return $local
    }

    New-Item -ItemType Directory -Force -Path (Join-Path $RepoRoot "work") | Out-Null
    $installer = Join-Path $RepoRoot "work\dotnet-install.ps1"
    Invoke-WebRequest -Uri "https://dot.net/v1/dotnet-install.ps1" -OutFile $installer
    & powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Channel 8.0 -InstallDir $DotnetRoot -Architecture x64
    if (!(Test-Path -LiteralPath $local)) {
        throw "dotnet local install failed: $local"
    }
    return $local
}

function Publish-DualNs2ProHostTool([string]$ToolsRoot) {
    $dotnet = Ensure-Dotnet
    $publishRoot = Join-Path $RepoRoot "work\dual_ns2pro_host_publish"
    Write-Step "dual_ns2pro_host" "publish"
    if (!$DryRun) {
        Remove-TreeWithRetry $publishRoot
        $env:DOTNET_ROOT = Split-Path -Parent $dotnet
        $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
        & $dotnet publish (Join-Path $RepoRoot "windows\dual_ns2pro_host\DualNs2ProHost.csproj") `
            -c Release -r win-x64 --self-contained true -o $publishRoot `
            /p:PublishSingleFile=true `
            /p:IncludeNativeLibrariesForSelfExtract=true `
            /p:EnableCompressionInSingleFile=true `
            /p:RestoreIgnoreFailedSources=true
        if ($LASTEXITCODE -ne 0) { throw "DualNs2ProHost publish failed" }

        $hostExe = Join-Path $publishRoot "DualNs2ProHost.exe"
        if (!(Test-Path -LiteralPath $hostExe)) {
            throw "Published DualNs2ProHost.exe not found: $hostExe"
        }
        Copy-Item -LiteralPath $hostExe -Destination (Join-Path $ToolsRoot "DualNs2ProHost.exe") -Force

        $viiperSource = Join-Path $RepoRoot "tools\viiper\haptic-v0.8.0\viiper-haptic.exe"
        if (!(Test-Path -LiteralPath $viiperSource)) {
            throw "Missing VIIPER haptic server: $viiperSource"
        }
        $viiperTargetRoot = Join-Path $ToolsRoot "viiper\haptic-v0.8.0"
        New-Item -ItemType Directory -Force -Path $viiperTargetRoot | Out-Null
        Copy-Item -LiteralPath $viiperSource -Destination (Join-Path $viiperTargetRoot "viiper-haptic.exe") -Force
    }
}

function Add-FirmwareProfilePayload([string]$Profile, [string]$TargetRoot) {
    $buildDir = Join-Path $RepoRoot "work\b\ds5\$Profile"
    if (!(Test-Path -LiteralPath $buildDir)) {
        throw "Missing firmware build directory: $buildDir"
    }

    $profileRoot = Join-Path $TargetRoot $Profile
    New-Item -ItemType Directory -Force -Path (Join-Path $profileRoot "bootloader") | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $profileRoot "partition_table") | Out-Null
    Copy-Item -LiteralPath (Join-Path $buildDir "bootloader\bootloader.bin") -Destination (Join-Path $profileRoot "bootloader\bootloader.bin") -Force
    Copy-Item -LiteralPath (Join-Path $buildDir "partition_table\partition-table.bin") -Destination (Join-Path $profileRoot "partition_table\partition-table.bin") -Force
    Copy-Item -LiteralPath (Join-Path $buildDir "esp32s3_dualsense_identity_experiment.bin") -Destination (Join-Path $profileRoot "esp32s3_dualsense_identity_experiment.bin") -Force
    Copy-Item -LiteralPath (Join-Path $buildDir "flash_args") -Destination (Join-Path $profileRoot "flash_args") -Force

    $assetDefs = @(
        @{ offset = "0x0"; path = "$Profile/bootloader/bootloader.bin" },
        @{ offset = "0x8000"; path = "$Profile/partition_table/partition-table.bin" },
        @{ offset = "0x10000"; path = "$Profile/esp32s3_dualsense_identity_experiment.bin" }
    )

    $assets = @()
    foreach ($asset in $assetDefs) {
        $file = Join-Path $TargetRoot ($asset.path -replace '/', [IO.Path]::DirectorySeparatorChar)
        $assets += [ordered]@{
            offset = $asset.offset
            path = $asset.path
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash.ToLowerInvariant()
        }
    }

    $label = switch ($Profile) {
        "hid_only" { "HID-only recovery"; break }
        "hid_audio_uac1_4ch_dualsense" { "Xin He Lian Sheng / PS5 standard"; break }
        "hid_audio_uac1_4ch_edge" { "Xin He Lian Sheng / PS5 Edge"; break }
        default { "DualSense-like bridge + audio lab"; break }
    }
    return [ordered]@{
        id = $Profile
        label = $label
        app = "esp32s3_dualsense_identity_experiment.bin"
        assets = $assets
    }
}

function Add-Pro2BridgeProfilePayload([string]$TargetRoot) {
    $profile = "pro2_bridge_v5_5"
    $buildDir = Join-Path $RepoRoot "work\b\pro2"
    if (!(Test-Path -LiteralPath $buildDir)) {
        throw "Missing Pro2 bridge firmware build directory: $buildDir"
    }

    $profileRoot = Join-Path $TargetRoot $profile
    New-Item -ItemType Directory -Force -Path (Join-Path $profileRoot "bootloader") | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $profileRoot "partition_table") | Out-Null
    Copy-Item -LiteralPath (Join-Path $buildDir "bootloader\bootloader.bin") -Destination (Join-Path $profileRoot "bootloader\bootloader.bin") -Force
    Copy-Item -LiteralPath (Join-Path $buildDir "partition_table\partition-table.bin") -Destination (Join-Path $profileRoot "partition_table\partition-table.bin") -Force
    Copy-Item -LiteralPath (Join-Path $buildDir "esp32s3_switch2_bridge.bin") -Destination (Join-Path $profileRoot "esp32s3_switch2_bridge.bin") -Force
    if (Test-Path -LiteralPath (Join-Path $buildDir "flash_args")) {
        Copy-Item -LiteralPath (Join-Path $buildDir "flash_args") -Destination (Join-Path $profileRoot "flash_args") -Force
    }

    $assetDefs = @(
        @{ offset = "0x0"; path = "$profile/bootloader/bootloader.bin" },
        @{ offset = "0x8000"; path = "$profile/partition_table/partition-table.bin" },
        @{ offset = "0x10000"; path = "$profile/esp32s3_switch2_bridge.bin" }
    )

    $assets = @()
    foreach ($asset in $assetDefs) {
        $file = Join-Path $TargetRoot ($asset.path -replace '/', [IO.Path]::DirectorySeparatorChar)
        $assets += [ordered]@{
            offset = $asset.offset
            path = $asset.path
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash.ToLowerInvariant()
        }
    }

    return [ordered]@{
        id = $profile
        label = "Pro2 / Nintendo bridge"
        app = "esp32s3_switch2_bridge.bin"
        assets = $assets
    }
}

function Add-XInputBridgeProfilePayload(
    [string]$TargetRoot,
    [string]$Profile = "xinput_bridge_v5_8",
    [string]$BuildDirRelative = "work\b\xinput",
    [string]$Label = "Xbox / XInput bridge"
) {
    $profile = $Profile
    $buildDir = Join-Path $RepoRoot $BuildDirRelative
    if (!(Test-Path -LiteralPath $buildDir)) {
        throw "Missing XInput bridge firmware build directory: $buildDir"
    }

    $profileRoot = Join-Path $TargetRoot $profile
    New-Item -ItemType Directory -Force -Path (Join-Path $profileRoot "bootloader") | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $profileRoot "partition_table") | Out-Null
    Copy-Item -LiteralPath (Join-Path $buildDir "bootloader\bootloader.bin") -Destination (Join-Path $profileRoot "bootloader\bootloader.bin") -Force
    Copy-Item -LiteralPath (Join-Path $buildDir "partition_table\partition-table.bin") -Destination (Join-Path $profileRoot "partition_table\partition-table.bin") -Force
    Copy-Item -LiteralPath (Join-Path $buildDir "esp32s3_switch2_bridge.bin") -Destination (Join-Path $profileRoot "esp32s3_switch2_bridge.bin") -Force
    if (Test-Path -LiteralPath (Join-Path $buildDir "flash_args")) {
        Copy-Item -LiteralPath (Join-Path $buildDir "flash_args") -Destination (Join-Path $profileRoot "flash_args") -Force
    }

    $assetDefs = @(
        @{ offset = "0x0"; path = "$profile/bootloader/bootloader.bin" },
        @{ offset = "0x8000"; path = "$profile/partition_table/partition-table.bin" },
        @{ offset = "0x10000"; path = "$profile/esp32s3_switch2_bridge.bin" }
    )

    $assets = @()
    foreach ($asset in $assetDefs) {
        $file = Join-Path $TargetRoot ($asset.path -replace '/', [IO.Path]::DirectorySeparatorChar)
        $assets += [ordered]@{
            offset = $asset.offset
            path = $asset.path
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash.ToLowerInvariant()
        }
    }

    return [ordered]@{
        id = $profile
        label = $Label
        app = "esp32s3_switch2_bridge.bin"
        assets = $assets
    }
}

function Add-DualPro2ProbeProfilePayload([string]$TargetRoot) {
    return Add-XInputBridgeProfilePayload `
        -TargetRoot $TargetRoot `
        -Profile "dual_pro2_probe_v5_9" `
        -BuildDirRelative "work\b\dual_pro2" `
        -Label "Dual Pro2 BLE capacity probe"
}

function Add-MacOsHidBridgeProfilePayload([string]$TargetRoot) {
    return Add-XInputBridgeProfilePayload `
        -TargetRoot $TargetRoot `
        -Profile "macos_hid_bridge_v5_9" `
        -BuildDirRelative "work\b\macos" `
        -Label "macOS / Standard HID bridge"
}

function Refresh-EmbeddedAssets {
    Write-Step "embedded_assets" "refresh"
    if ($DryRun) { return }

    $firmwareRoot = Join-Path $ManagerRoot "embedded\firmware\v5.9"
    $toolsRoot = Join-Path $ManagerRoot "embedded\tools"
    if (Test-Path -LiteralPath $firmwareRoot) {
        Remove-Item -LiteralPath $firmwareRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $firmwareRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $toolsRoot | Out-Null

    $profiles = @()
    $profiles += Add-FirmwareProfilePayload "hid_audio_uac1_4ch_dualsense" $firmwareRoot
    $profiles += Add-FirmwareProfilePayload "hid_audio_uac1_4ch_edge" $firmwareRoot
    $profiles += Add-FirmwareProfilePayload "hid_only" $firmwareRoot
    $profiles += Add-Pro2BridgeProfilePayload $firmwareRoot
    $profiles += Add-XInputBridgeProfilePayload $firmwareRoot
    $profiles += Add-MacOsHidBridgeProfilePayload $firmwareRoot
    $profiles += Add-DualPro2ProbeProfilePayload $firmwareRoot

    $manifest = [ordered]@{
        packageVersion = "$PackageTag-aio"
        firmwareVersion = "$PackageVersion-manager"
        target = "esp32s3"
        flashMode = "dio"
        flashFreq = "80m"
        flashSize = "16MB"
        defaultProfile = "pro2_bridge_v5_5"
        profiles = $profiles
        notes = "V5.9.19 ESP stability release: keeps the latest live BLE state across notification gaps and sends neutral only after an explicit disconnect/reset, preventing false button release/press edges. Adds a standards-based macOS HID gamepad profile. Release profiles remain compile-time mode locked, and the manager no longer rewrites/reboots the mode after flashing or mistakes fallback text and stale PnP entries for an Xbox device."
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 -Path (Join-Path $firmwareRoot "firmware_manifest.json")

    $esptool = Join-Path $toolsRoot "esptool.exe"
    if (!(Test-Path -LiteralPath $esptool)) { throw "Missing bundled esptool: $esptool" }

    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $RepoRoot "tools\send_v5_5_haptic_audio_test.ps1") -CompileOnly
    Copy-Item -LiteralPath (Join-Path $RepoRoot "tools\SendV55HapticAudioTest.exe") -Destination (Join-Path $toolsRoot "SendV55HapticAudioTest.exe") -Force
    $xinputProbeSource = Join-Path $RepoRoot "tools\SteamXInputRumbleProbe.cs"
    $xinputProbeExe = Join-Path $RepoRoot "tools\SteamXInputRumbleProbe.exe"
    Build-CSharpTool $xinputProbeSource $xinputProbeExe
    Copy-Item -LiteralPath $xinputProbeExe -Destination (Join-Path $toolsRoot "SteamXInputRumbleProbe.exe") -Force
    Publish-DualNs2ProHostTool $toolsRoot

    $icon = Join-Path $ManagerRoot "assets\icon.ico"
    if (!(Test-Path -LiteralPath $icon)) { throw "Missing manager icon: $icon" }
}

if (!$SkipFirmwareBuild) {
    $IdfPath = Resolve-Y700IdfPath -RequestedPath $IdfPath
    Write-Step "firmware_build" "pro2_bridge_v5_5"
    if (!$DryRun) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $RepoRoot "tools\esp32s3\build.ps1") `
            -IdfPath $IdfPath `
            -DeviceDefaultMode NINTENDO_EXPERIMENT_MODE
        if ($LASTEXITCODE -ne 0) { throw "Firmware build failed: pro2_bridge_v5_5" }
    }

    Write-Step "firmware_build" "xinput_bridge_v5_8"
    if (!$DryRun) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $RepoRoot "tools\esp32s3\build.ps1") `
            -IdfPath $IdfPath `
            -BuildDir "work\b\xinput" `
            -DeviceDefaultMode XINPUT_EXPERIMENT_MODE
        if ($LASTEXITCODE -ne 0) { throw "Firmware build failed: xinput_bridge_v5_8" }
    }

    Write-Step "firmware_build" "macos_hid_bridge_v5_9"
    if (!$DryRun) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $RepoRoot "tools\esp32s3\build.ps1") `
            -IdfPath $IdfPath `
            -BuildDir "work\b\macos" `
            -DeviceDefaultMode GENERIC_HID_MODE
        if ($LASTEXITCODE -ne 0) { throw "Firmware build failed: macos_hid_bridge_v5_9" }
    }

    Write-Step "firmware_build" "dual_pro2_probe_v5_9"
    if (!$DryRun) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $RepoRoot "tools\esp32s3\build.ps1") `
            -IdfPath $IdfPath `
            -BuildDir "work\b\dual_pro2" `
            -DeviceDefaultMode DUAL_PRO2_EXPERIMENT_MODE
        if ($LASTEXITCODE -ne 0) { throw "Firmware build failed: dual_pro2_probe_v5_9" }
    }

    $buildScript = Join-Path $RepoRoot "tools\esp32s3\build_v5_5_dualsense_identity.ps1"
    foreach ($profile in @("hid_audio_uac1_4ch_dualsense", "hid_audio_uac1_4ch_edge", "hid_only")) {
        Write-Step "firmware_build" $profile
        if (!$DryRun) {
            & powershell -NoProfile -ExecutionPolicy Bypass -File $buildScript -IdfPath $IdfPath -Profile $profile
            if ($LASTEXITCODE -ne 0) { throw "Firmware build failed: $profile" }
        }
    }
}

if (!$SkipEmbeddedRefresh) {
    Refresh-EmbeddedAssets
} else {
    Write-Step "embedded_assets" "preserved"
}

$dotnet = Ensure-Dotnet
Write-Step "dotnet" $dotnet

if (!$DryRun) {
    New-Item -ItemType Directory -Force -Path $ReleaseRoot | Out-Null
    Remove-TreeWithRetry $PublishRoot -Required
    [void](Remove-FileWithRetry $SingleExe -Required)
    [void](Remove-FileWithRetry $LegacySingleExe -Required)
    Get-ChildItem -LiteralPath $ReleaseRoot -Filter "*aio-$PackageTag.exe" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -ne $SingleExe -and $_.FullName -ne $LegacySingleExe } |
        ForEach-Object { [void](Remove-FileWithRetry $_.FullName) }
    Get-ChildItem -LiteralPath $ReleaseRoot -Filter "SHA256SUMS-$PackageTag*.txt" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -ne $HashFile } |
        ForEach-Object { [void](Remove-FileWithRetry $_.FullName) }

    $env:DOTNET_ROOT = Split-Path -Parent $dotnet
    $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
    & $dotnet publish (Join-Path $ManagerRoot "Y700Switch2V55Manager.csproj") `
        -c Release -r win-x64 --self-contained true -o $PublishRoot `
        /p:PublishSingleFile=true `
        /p:IncludeNativeLibrariesForSelfExtract=true `
        /p:EnableCompressionInSingleFile=true `
        /p:RestoreIgnoreFailedSources=true
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

    $publishedExe = Join-Path $PublishRoot "Y700Switch2V55Manager.exe"
    if (!(Test-Path -LiteralPath $publishedExe)) {
        throw "Published exe not found: $publishedExe"
    }
    Copy-Item -LiteralPath $publishedExe -Destination $SingleExe -Force
    Copy-Item -LiteralPath $publishedExe -Destination $LegacySingleExe -Force

    if (!(Test-Path -LiteralPath $ReadmeSource)) {
        throw "Release notes not found: $ReadmeSource"
    }
    Copy-Item -LiteralPath $ReadmeSource -Destination $ReadmeFile -Force

    $verifyLog = Join-Path $RepoRoot "work\v5_9_19_manager_package_verify.txt"
    $verifyDataRoot = Join-Path $RepoRoot "work\package-verify-data"
    Remove-Item -LiteralPath $verifyLog -Force -ErrorAction SilentlyContinue
    Remove-TreeWithRetry $verifyDataRoot
    $previousDataRoot = $env:PRO2_BRIDGE_DATA_ROOT
    try {
        $env:PRO2_BRIDGE_DATA_ROOT = $verifyDataRoot
        $verifyProcess = Start-Process -FilePath $SingleExe `
            -ArgumentList @("--verify-package", ('"{0}"' -f $verifyLog)) `
            -Wait -PassThru
    } finally {
        $env:PRO2_BRIDGE_DATA_ROOT = $previousDataRoot
    }
    if ($verifyProcess.ExitCode -ne 0) {
        $verifyDetails = if (Test-Path -LiteralPath $verifyLog) {
            Get-Content -LiteralPath $verifyLog -Raw
        } else {
            "verification log was not created"
        }
        throw "Published manager package verification failed (exit=$($verifyProcess.ExitCode)):`n$verifyDetails"
    }
    $verifyDetails = Get-Content -LiteralPath $verifyLog -Raw
    $verifyData = @{}
    foreach ($line in Get-Content -LiteralPath $verifyLog) {
        $parts = $line -split "=", 2
        if ($parts.Count -eq 2) {
            $verifyData[$parts[0].Trim()] = $parts[1].Trim()
        }
    }
    if ($verifyData["result"] -ne "passed" -or
        $verifyData["profiles"] -ne "hid_audio_uac1_4ch_dualsense,hid_audio_uac1_4ch_edge,hid_only,pro2_bridge_v5_5,xinput_bridge_v5_8,macos_hid_bridge_v5_9,dual_pro2_probe_v5_9" -or
        $verifyData["asset_count"] -ne "21" -or
        $verifyData["dual_ns2pro_host_exists"] -ne "true") {
        throw "Published manager package verification returned unexpected data:`n$verifyDetails"
    }
    Write-Step "package_verify" "passed"

    $hashTargets = @($SingleExe, $LegacySingleExe)
    $hashLines = foreach ($target in $hashTargets) {
        $hash = Get-FileHash -Algorithm SHA256 -LiteralPath $target
        "{0}  {1}" -f $hash.Hash.ToLowerInvariant(), (Split-Path -Leaf $target)
    }
    [System.IO.File]::WriteAllText($HashFile, (($hashLines -join "`r`n") + "`r`n"), [System.Text.Encoding]::UTF8)
    Remove-TreeWithRetry $verifyDataRoot
    Remove-TreeWithRetry $PublishRoot
    Write-Step "exe" (($SingleExe.Substring($RepoRoot.Length + 1)) -replace '\\','/')
    Write-Step "github_exe" (($LegacySingleExe.Substring($RepoRoot.Length + 1)) -replace '\\','/')
    Write-Step "readme" (($ReadmeFile.Substring($RepoRoot.Length + 1)) -replace '\\','/')
    Write-Step "sha256_file" (($HashFile.Substring($RepoRoot.Length + 1)) -replace '\\','/')
}



