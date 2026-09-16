param(
    [string]$IdfPath,
    [switch]$Clean,
    [switch]$Package
)

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $RepoRoot "tools\esp32s3\idf_environment.ps1")
$IdfPath = Resolve-Y700IdfPath -RequestedPath $IdfPath

$common = @{
    IdfPath = $IdfPath
}

Write-Host "[Y700_BUILD_ALL] profile=pro2_bridge_v5_5"
& (Join-Path $RepoRoot "tools\esp32s3\build.ps1") @common `
    -BuildDir "work\b\pro2" `
    -DeviceDefaultMode NINTENDO_EXPERIMENT_MODE
if ($LASTEXITCODE -ne 0) {
    throw "Firmware build failed: pro2_bridge_v5_5"
}

Write-Host "[Y700_BUILD_ALL] profile=xinput_bridge_v5_8"
& (Join-Path $RepoRoot "tools\esp32s3\build.ps1") @common `
    -BuildDir "work\b\xinput" `
    -DeviceDefaultMode XINPUT_EXPERIMENT_MODE
if ($LASTEXITCODE -ne 0) {
    throw "Firmware build failed: xinput_bridge_v5_8"
}

Write-Host "[Y700_BUILD_ALL] profile=macos_hid_bridge_v5_9"
& (Join-Path $RepoRoot "tools\esp32s3\build.ps1") @common `
    -BuildDir "work\b\macos" `
    -DeviceDefaultMode GENERIC_HID_MODE
if ($LASTEXITCODE -ne 0) {
    throw "Firmware build failed: macos_hid_bridge_v5_9"
}

Write-Host "[Y700_BUILD_ALL] profile=dual_pro2_probe_v5_9"
& (Join-Path $RepoRoot "tools\esp32s3\build.ps1") @common `
    -BuildDir "work\b\dual_pro2" `
    -DeviceDefaultMode DUAL_PRO2_EXPERIMENT_MODE
if ($LASTEXITCODE -ne 0) {
    throw "Firmware build failed: dual_pro2_probe_v5_9"
}

$dualSenseBuild = Join-Path $RepoRoot "tools\esp32s3\build_v5_5_dualsense_identity.ps1"
foreach ($profile in @("hid_audio_uac1_4ch_ds5like", "hid_only")) {
    Write-Host "[Y700_BUILD_ALL] profile=$profile"
    & $dualSenseBuild -IdfPath $IdfPath -Profile $profile -Clean:$Clean
    if ($LASTEXITCODE -ne 0) {
        throw "Firmware build failed: $profile"
    }
}

if ($Package) {
    Write-Host "[Y700_BUILD_ALL] package=v5.9.19"
    & (Join-Path $RepoRoot "tools\package_v5_9_manager.ps1") `
        -IdfPath $IdfPath `
        -SkipFirmwareBuild
    if ($LASTEXITCODE -ne 0) {
        throw "Manager packaging failed."
    }
}

Write-Host "[Y700_BUILD_ALL] result=passed"

