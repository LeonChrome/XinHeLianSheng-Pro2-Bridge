param(
    [string]$Port,
    [string]$IdfPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "idf_environment.ps1")
$Root = Get-Y700ShortRepoRoot
$Firmware = Join-Path $Root "firmware\esp32s3_switch2_bridge"
$BuildRoot = Join-Path $Root "work\b\pro2"

Write-Host "Flashing/logging: connect CH343P Type-C."
Write-Host "HID test: connect ESP32-S3 native USB & OTG Type-C."
Write-Host "First-board note: PowerShell/.NET serial reads worked with DTR=False, RTS=False."
Write-Host "If this monitor shows no output, make sure no previous idf_monitor process is holding the port."

if (-not $Port) {
    & (Join-Path $PSScriptRoot "detect_ports.ps1")
    $Port = Read-Host "Enter CH343P COM port, e.g. COM54"
}

if (-not $Port) { throw "No COM port supplied." }

$IdfPath = Resolve-Y700IdfPath -RequestedPath $IdfPath
Import-Y700IdfEnvironment -IdfPath $IdfPath

Push-Location $Firmware
try {
    idf.py -B $BuildRoot -p $Port monitor
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed: idf.py -p $Port monitor"
    }
} finally {
    Pop-Location
}
