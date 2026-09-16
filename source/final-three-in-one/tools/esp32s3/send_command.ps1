param(
    [string]$Port,
    [string]$Command = "status",
    [int]$ReadSeconds = 4,
    [switch]$ResetBeforeCommand
)

$ErrorActionPreference = "Stop"

Write-Host "ESP32-S3 serial command probe"
Write-Host "Flashing/logging/control: connect CH343P Type-C."
Write-Host "HID test: connect ESP32-S3 native USB & OTG Type-C."
Write-Host "First-board note: PowerShell/.NET serial reads worked with DTR=False, RTS=False."

if (-not $Port) {
    & (Join-Path $PSScriptRoot "detect_ports.ps1")
    $Port = Read-Host "Enter CH343P COM port, e.g. COM12"
}

if (-not $Port) { throw "No COM port supplied." }

$serial = [System.IO.Ports.SerialPort]::new($Port, 115200, [System.IO.Ports.Parity]::None, 8, [System.IO.Ports.StopBits]::One)
$serial.ReadTimeout = 200
$serial.WriteTimeout = 1000
$serial.DtrEnable = $false
$serial.RtsEnable = $false
$serial.Handshake = [System.IO.Ports.Handshake]::None

try {
    $serial.Open()
    $serial.DtrEnable = $false
    $serial.RtsEnable = $false

    if ($ResetBeforeCommand) {
        Write-Host "Resetting board through DTR/RTS before command..."
        $serial.DtrEnable = $false
        $serial.RtsEnable = $true
        Start-Sleep -Milliseconds 150
        $serial.DtrEnable = $false
        $serial.RtsEnable = $false
        Start-Sleep -Seconds 3
    } else {
        Start-Sleep -Milliseconds 250
    }

    $serial.DiscardInBuffer()
    Write-Host (">>> {0}" -f $Command)
    $serial.WriteLine($Command)

    $deadline = (Get-Date).AddSeconds($ReadSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $chunk = $serial.ReadExisting()
            if ($chunk) {
                Write-Host $chunk -NoNewline
            }
        } catch [TimeoutException] {
        } catch [System.InvalidOperationException] {
            Write-Host ""
            Write-Host "Serial port closed while reading; stopping read loop."
            break
        }
        Start-Sleep -Milliseconds 100
    }
} finally {
    if ($serial.IsOpen) {
        $serial.Close()
    }
}
