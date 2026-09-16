param(
    [string]$IdfPath,
    [string]$BuildDir = "work\b\pro2",
    [ValidateSet("", "GENERIC_HID_MODE", "NINTENDO_EXPERIMENT_MODE", "XINPUT_EXPERIMENT_MODE", "DUAL_PRO2_EXPERIMENT_MODE")]
    [string]$DeviceDefaultMode = "",
    [switch]$XInputElite
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "idf_environment.ps1")
$Root = Get-Y700ShortRepoRoot
$Firmware = Join-Path $Root "firmware\esp32s3_switch2_bridge"

Write-Host "ESP32-S3 Pro2 Bridge firmware build."
Write-Host "Flashing/logging: connect CH343P Type-C."
Write-Host "HID test: connect ESP32-S3 native USB & OTG Type-C."

$IdfPath = Resolve-Y700IdfPath -RequestedPath $IdfPath
Import-Y700IdfEnvironment -IdfPath $IdfPath

function Invoke-IdfBuild {
    $args = @()
    if ($BuildDir) {
        $resolvedBuildDir = if ([System.IO.Path]::IsPathRooted($BuildDir)) {
            $BuildDir
        } else {
            Join-Path $Root $BuildDir
        }
        $args += @("-B", $resolvedBuildDir)
    }
    if ($DeviceDefaultMode) {
        $args += "-DDEVICE_DEFAULT_MODE=$DeviceDefaultMode"
    }
    if ($XInputElite) {
        $args += "-DXINPUT_ELITE_EXPERIMENT=1"
    }
    $args += "build"

    Write-Host ("idf.py " + ($args -join " "))
    & idf.py @args
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed: idf.py " + ($args -join " ")
    }
}

Push-Location $Firmware
try {
    Invoke-IdfBuild
} finally {
    Pop-Location
}
