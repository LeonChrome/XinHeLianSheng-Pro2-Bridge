param([string]$IdfPath)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "idf_environment.ps1")
$IdfPath = Resolve-Y700IdfPath -RequestedPath $IdfPath
Import-Y700IdfEnvironment -IdfPath $IdfPath

$commands = @(
    @{ Name = "idf.py"; Args = @("--version") },
    @{ Name = "cmake"; Args = @("--version") },
    @{ Name = "ninja"; Args = @("--version") },
    @{ Name = "xtensa-esp32s3-elf-gcc"; Args = @("--version") }
)

foreach ($item in $commands) {
    $command = Get-Command $item.Name -ErrorAction SilentlyContinue
    if (!$command) {
        throw "Required command is missing after ESP-IDF export: $($item.Name)"
    }
    $output = (& $item.Name @($item.Args) | Out-String).Trim()
    $firstLine = if ($item.Name -eq "idf.py") {
        ($output -split '\r?\n' | Where-Object { $_ -match 'ESP-IDF.*5\.4\.2' } | Select-Object -First 1)
    } else {
        ($output -split '\r?\n' | Select-Object -First 1)
    }
    Write-Host "[Y700_CHECK] $($item.Name)=$firstLine"
}

$idfPython = Get-Y700IdfPython
Write-Host "[Y700_CHECK] idf_python=$idfPython"
& $idfPython -m esptool version
if ($LASTEXITCODE -ne 0) {
    throw "esptool validation failed: $LASTEXITCODE"
}

$ports = Get-CimInstance Win32_PnPEntity |
    Where-Object { $_.Name -match "CH343.*\(COM\d+\)" -or $_.DeviceID -match "VID_1A86&PID_55D3" }
if ($ports) {
    foreach ($port in $ports) {
        Write-Host "[Y700_CHECK] control_port=$($port.Name)"
    }
} else {
    Write-Warning "No CH343P control port is currently connected."
}

Write-Host "[Y700_CHECK] result=passed"
