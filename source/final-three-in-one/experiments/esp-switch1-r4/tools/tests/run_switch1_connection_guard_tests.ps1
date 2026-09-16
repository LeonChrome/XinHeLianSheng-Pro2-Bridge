$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$manager = Get-Content (
    Join-Path $repoRoot "windows\v55_manager_app\MainViewModel.cs") -Raw
$bleCentral = Get-Content (
    Join-Path $repoRoot "firmware\esp32s3_switch2_bridge\main\ble\ble_central.c") -Raw
$control = Get-Content (
    Join-Path $repoRoot "firmware\esp32s3_switch2_bridge\main\control\control_protocol.c") -Raw

if ($manager -notmatch "FindExternalViiperProcesses" -or
    $manager -notmatch "EnsureNoExternalViiperBleOwner" -or
    $manager -notmatch "BLE_OWNER_CONFLICT") {
    throw "Manager does not guard ESP BLE operations from an external VIIPER owner."
}
if ($manager -notmatch "serialReportedMode == DeviceUiMode.Switch1Pro" -or
    $manager -notmatch "USB_IDENTITY_IGNORED") {
    throw "Manager does not protect serial-confirmed Switch1 mode from a virtual 057E:2069."
}
$autoTaskStart = $bleCentral.IndexOf("static void ble_auto_reconnect_task")
$autoTaskEnd = $bleCentral.IndexOf("static void schedule_auto_reconnect", $autoTaskStart)
if ($autoTaskStart -lt 0 -or $autoTaskEnd -le $autoTaskStart) {
    throw "BLE reconnect supervisor function is missing."
}
$autoTaskBody = $bleCentral.Substring($autoTaskStart, $autoTaskEnd - $autoTaskStart)
if ($bleCentral -notmatch "s_auto_reconnect_stop_requested" -or
    $bleCentral -match "vTaskDelete\(s_auto_reconnect_task\)" -or
    $autoTaskBody -match 's_auto_reconnect_task = NULL|vTaskDelete\(') {
    throw "BLE reconnect cancellation is not cooperative."
}
if ($bleCentral -notmatch "BLE_STATE_STOPPING" -or
    $bleCentral -notmatch "BLE active scan reused" -or
    $bleCentral -match "s_auto_connect_selected_task") {
    throw "BLE GAP scan cancellation is not guarded by the stopping state machine."
}
if ($bleCentral -notmatch '(?s)BLE_GAP_EVENT_DISC_COMPLETE.*s_auto_scan_target_valid.*ble_central_connect_addr') {
    throw "BLE target connection must be owned by the DISC_COMPLETE path."
}
if ($manager -match 'summary\.Contains\("XInput"') {
    throw "USB mode detection can mistake the empty-device help text for Xbox."
}
if ($manager -notmatch "WaitForBleTransportToSettleAsync" -or
    $manager -notmatch "firmwareAutoReconnect") {
    throw "Manager does not wait for GAP stop or defer reconnect ownership to firmware auto mode."
}
if ($control -notmatch '(?s)device_config_save_ble_target\(""\).*ble_central_disconnect\(\)') {
    throw "ble forget must clear the saved target before stopping the live link."
}

Write-Output "Switch 1 connection guard tests passed"
