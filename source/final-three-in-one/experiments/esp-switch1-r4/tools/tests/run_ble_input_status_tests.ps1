[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$project = Join-Path $PSScriptRoot "ble_input_status_test\ble_input_status_test.csproj"
$dotnet = Join-Path $repoRoot "work\dotnet\dotnet.exe"
if (!(Test-Path -LiteralPath $dotnet)) {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}

& $dotnet run --project $project -c Release
if ($LASTEXITCODE -ne 0) {
    throw "BLE input status tests failed with exit code $LASTEXITCODE"
}
