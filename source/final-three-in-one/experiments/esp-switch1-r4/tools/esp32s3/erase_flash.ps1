param(
    [string]$Port,
    [string]$IdfPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "idf_environment.ps1")
$Root = Get-Y700ShortRepoRoot
$Firmware = Join-Path $Root "firmware\esp32s3_switch2_bridge"

Write-Host "PENDING_HARDWARE_TEST: erase_flash is not verified until the ESP32-S3 board arrives."
Write-Host "Flashing/logging: connect CH343P Type-C."
Write-Host "HID test: connect ESP32-S3 native USB & OTG Type-C."

if (-not $Port) {
    & (Join-Path $PSScriptRoot "detect_ports.ps1")
    $Port = Read-Host "Enter CH343P COM port, e.g. COM54"
}

if (-not $Port) { throw "No COM port supplied." }

$IdfPath = Resolve-Y700IdfPath -RequestedPath $IdfPath
Import-Y700IdfEnvironment -IdfPath $IdfPath

Push-Location $Firmware
try {
    idf.py -p $Port erase-flash
} finally {
    Pop-Location
}
