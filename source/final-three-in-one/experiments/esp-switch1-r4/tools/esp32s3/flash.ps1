param(
    [string]$Port,
    [string]$IdfPath,
    [int]$Baud = 460800,
    [switch]$NoStub
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "idf_environment.ps1")
$Root = Get-Y700ShortRepoRoot
$Firmware = Join-Path $Root "firmware\esp32s3_switch2_bridge"
$BuildRoot = Join-Path $Root "work\b\pro2"

Write-Host "Flashing/logging: connect CH343P Type-C."
Write-Host "HID test: connect ESP32-S3 native USB & OTG Type-C."
Write-Host "First-board note: flashing was observed once on COM12; use the COM port detected on this machine."
Write-Host "If flashing reports serial noise/corruption, retry with -Baud 115200."
Write-Host "If it still reports serial noise/corruption, retry with -NoStub -Baud 115200."

if (-not $Port) {
    & (Join-Path $PSScriptRoot "detect_ports.ps1")
    $Port = Read-Host "Enter CH343P COM port, e.g. COM54"
}

if (-not $Port) { throw "No COM port supplied." }

$IdfPath = Resolve-Y700IdfPath -RequestedPath $IdfPath
Import-Y700IdfEnvironment -IdfPath $IdfPath

function Invoke-IdfCommand {
    param([string[]]$Arguments)
    & idf.py @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed: idf.py $($Arguments -join ' ')"
    }
}

Push-Location $Firmware
try {
    $Sdkconfig = Join-Path $Firmware "sdkconfig"
    if (!(Test-Path -LiteralPath $Sdkconfig) -or !(Select-String -Path $Sdkconfig -Pattern 'CONFIG_IDF_TARGET="esp32s3"' -Quiet)) {
        Invoke-IdfCommand @("-B", $BuildRoot, "set-target", "esp32s3")
    }
    if ($NoStub) {
        Invoke-IdfCommand @("-B", $BuildRoot, "build")
        Push-Location $BuildRoot
        try {
            $IdfPython = Get-Y700IdfPython
            & $IdfPython -m esptool --chip esp32s3 --no-stub -p $Port -b $Baud --before default_reset --after hard_reset write_flash "@flash_args"
            if ($LASTEXITCODE -ne 0) {
                throw "no-stub esptool flash failed: $LASTEXITCODE"
            }
        } finally {
            Pop-Location
        }
    } else {
        Invoke-IdfCommand @("-B", $BuildRoot, "-p", $Port, "-b", "$Baud", "flash")
    }
} finally {
    Pop-Location
}
