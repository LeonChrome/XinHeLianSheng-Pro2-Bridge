$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$appMain = Get-Content (
    Join-Path $repoRoot "firmware\esp32s3_switch2_bridge\main\app_main.c") -Raw
$bleCentral = Get-Content (
    Join-Path $repoRoot "firmware\esp32s3_switch2_bridge\main\ble\ble_central.c") -Raw
$descriptors = Get-Content (
    Join-Path $repoRoot "firmware\esp32s3_switch2_bridge\main\usb\usb_descriptors.c") -Raw
$manager = Get-Content (
    Join-Path $repoRoot "windows\v55_manager_app\MainViewModel.cs") -Raw
$inspector = Get-Content (
    Join-Path $repoRoot "windows\v55_manager_app\DeviceInspector.cs") -Raw
$package = Get-Content (
    Join-Path $repoRoot "tools\package_v5_9_manager.ps1") -Raw

if ($appMain -match "BLE_LIVE_STALE_US" -or
    $appMain -notmatch "bool using_live = live_valid;" -or
    $appMain -notmatch "false release followed by") {
    throw "Live input must hold the last state across BLE notification gaps."
}

$explicitClearSites = ([regex]::Matches(
    $bleCentral,
    "switch2_state_clear_live\(\);")).Count
if ($explicitClearSites -lt 4) {
    throw "BLE disconnect/reset/recovery paths no longer explicitly clear live input."
}

if ($descriptors -notmatch "TUD_HID_REPORT_DESC_GAMEPAD" -or
    $descriptors -notmatch "Pro2 Bridge Gamepad" -or
    $package -notmatch "macos_hid_bridge_v5_9" -or
    $package -notmatch "DeviceDefaultMode GENERIC_HID_MODE") {
    throw "The macOS standards-based HID profile is incomplete."
}

if ($manager -match "ModeCommandForProfile" -or
    $manager -match 'UsbStatus\.Contains\("XInput"' -or
    $manager -notmatch "PendingExpectedUsbMarker" -or
    $manager -notmatch "VID_CAFE&PID_4037") {
    throw "Manager mode-stability guards are incomplete."
}

if ($inspector -notmatch "Present,ConfigManagerErrorCode" -or
    $inspector -notmatch "!present \|\| errorCode != 0") {
    throw "USB inspection must exclude stale/non-present PnP identities."
}

Write-Output "V5.9.19 input, macOS HID, and mode-stability tests passed"
