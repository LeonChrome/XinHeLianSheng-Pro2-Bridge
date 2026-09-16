[CmdletBinding()]
param(
    [string]$Port = "COM5",
    [Parameter(Mandatory = $true)][string]$Command,
    [ValidateRange(1, 30)][int]$ReadSeconds = 4
)

$ErrorActionPreference = "Stop"
$serial = [IO.Ports.SerialPort]::new(
    $Port, 115200, [IO.Ports.Parity]::None, 8,
    [IO.Ports.StopBits]::One)
$serial.NewLine = "`n"
$serial.ReadTimeout = 100
$serial.WriteTimeout = 1000
$serial.DtrEnable = $true
$serial.RtsEnable = $true

try {
    $serial.Open()
    Start-Sleep -Milliseconds 400
    $serial.DiscardInBuffer()
    Write-Host "> $Command"
    $serial.WriteLine($Command)
    $deadline = [DateTime]::UtcNow.AddSeconds($ReadSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $chunk = $serial.ReadExisting()
            if ($chunk) {
                Write-Host $chunk -NoNewline
            }
        } catch [InvalidOperationException] {
            break
        }
        Start-Sleep -Milliseconds 80
    }
} finally {
    if ($serial.IsOpen) {
        try { $serial.Close() } catch {}
    }
    $serial.Dispose()
}
