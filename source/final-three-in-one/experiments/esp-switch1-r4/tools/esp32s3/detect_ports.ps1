param()

$ErrorActionPreference = "Stop"

Write-Host "ESP32-S3 port detection"
Write-Host "Flashing/logging: connect CH343P Type-C."
Write-Host "HID test: connect ESP32-S3 native USB & OTG Type-C."
Write-Host "Known first-board CH343P ID: USB\VID_1A86&PID_55D3."
Write-Host

$ports = Get-CimInstance Win32_PnPEntity |
    Where-Object { $_.Name -match '\(COM\d+\)' -or $_.PNPClass -eq 'Ports' } |
    Select-Object Name, Manufacturer, DeviceID

if (-not $ports) {
    Write-Warning "No COM devices found. Install CH343 driver or reconnect the CH343P Type-C port."
    exit 1
}

$ports | ForEach-Object {
    $hint = if ($_.Name -match 'CH343|CH340|USB.*Serial|USB-SERIAL|USB 串行|wch|QinHeng' -or $_.DeviceID -match 'VID_1A86&PID_55D3') { '  <-- possible CH343/USB serial' } else { '' }
    Write-Host ("{0} | {1}{2}" -f $_.Name, $_.Manufacturer, $hint)
}
