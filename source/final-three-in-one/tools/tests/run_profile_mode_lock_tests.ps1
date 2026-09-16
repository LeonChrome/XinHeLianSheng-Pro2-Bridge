$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$mainCMake = Get-Content (
    Join-Path $repoRoot "firmware\esp32s3_switch2_bridge\main\CMakeLists.txt") -Raw
$deviceConfig = Get-Content (
    Join-Path $repoRoot "firmware\esp32s3_switch2_bridge\main\config\device_config.c") -Raw
$buildAll = Get-Content (Join-Path $repoRoot "tools\build_all.ps1") -Raw
$package = Get-Content (Join-Path $repoRoot "tools\package_v5_9_manager.ps1") -Raw

if ($mainCMake -notmatch "DEVICE_PROFILE_LOCKED_MODE=1") {
    throw "Release profile mode lock is missing from main CMakeLists.txt."
}
if ($deviceConfig -notmatch "#ifdef DEVICE_PROFILE_LOCKED_MODE" -or
    $deviceConfig -notmatch "release profile locks device mode" -or
    $deviceConfig -notmatch "release profile rejected mode change" -or
    $deviceConfig -notmatch "release profile ignored runtime mode change") {
    throw "Device config does not enforce the release profile mode lock."
}
if ($buildAll -notmatch "(?s)work\\b\\pro2.*DeviceDefaultMode NINTENDO_EXPERIMENT_MODE") {
    throw "build_all.ps1 does not lock the Pro2 profile."
}
if ($package -notmatch "(?s)firmware_build.*pro2_bridge_v5_5.*DeviceDefaultMode NINTENDO_EXPERIMENT_MODE") {
    throw "package_v5_9_manager.ps1 does not lock the Pro2 profile."
}
if ($package -notmatch "(?s)firmware_build.*xinput_bridge_v5_8.*DeviceDefaultMode XINPUT_EXPERIMENT_MODE") {
    throw "package_v5_9_manager.ps1 does not lock the Xbox profile."
}
if ($buildAll -notmatch "(?s)work\\b\\macos.*DeviceDefaultMode GENERIC_HID_MODE") {
    throw "build_all.ps1 does not lock the macOS HID profile."
}
if ($package -notmatch "(?s)firmware_build.*macos_hid_bridge_v5_9.*DeviceDefaultMode GENERIC_HID_MODE") {
    throw "package_v5_9_manager.ps1 does not lock the macOS HID profile."
}
if ($buildAll -notmatch "(?s)work\\b\\dual_pro2.*DeviceDefaultMode DUAL_PRO2_EXPERIMENT_MODE") {
    throw "build_all.ps1 does not lock the Dual Pro2 probe profile."
}
if ($package -notmatch "(?s)firmware_build.*dual_pro2_probe_v5_9.*DeviceDefaultMode DUAL_PRO2_EXPERIMENT_MODE") {
    throw "package_v5_9_manager.ps1 does not lock the Dual Pro2 probe profile."
}

Write-Output "release profile mode lock tests passed"
