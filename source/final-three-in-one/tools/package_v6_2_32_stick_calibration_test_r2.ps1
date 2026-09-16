param(
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$Version = "6.2.32-stick-calibration-test-r2"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$Project = Join-Path $RepoRoot "windows\v60_viiper_app\Y700Switch2V60Viiper.csproj"
$TestProject = Join-Path $RepoRoot "tools\tests\v60_packet_mapper_test\V60PacketMapperTest.csproj"
$ReleaseRoot = Join-Path $RepoRoot "release\v6.2.32-test\r2"
$PublishRoot = Join-Path $ReleaseRoot "publish"
$ChineseBrand = -join (@(0x65B0, 0x548C, 0x8054, 0x80DC) | ForEach-Object { [char]$_ })
$ChineseVersionWord = -join (@(0x7248, 0x672C) | ForEach-Object { [char]$_ })
$ExeName = "${ChineseBrand}VIIPER${ChineseVersionWord}-aio-v$Version.exe"
$ExePath = Join-Path $ReleaseRoot $ExeName
$HashPath = Join-Path $ReleaseRoot "SHA256SUMS-v$Version.txt"

function Assert-UnderRepo([string]$Path) {
    $full = [System.IO.Path]::GetFullPath($Path)
    $prefix = $RepoRoot.TrimEnd('\') + '\'
    if (!$full.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing filesystem operation outside repository: $full"
    }
}

Assert-UnderRepo $ReleaseRoot
Assert-UnderRepo $PublishRoot

if (!$SkipTests) {
    & dotnet run --project $TestProject -c Debug
    if ($LASTEXITCODE -ne 0) { throw "v60_packet_mapper_test failed" }
}

New-Item -ItemType Directory -Force -Path $ReleaseRoot | Out-Null
if (Test-Path -LiteralPath $PublishRoot) {
    Remove-Item -LiteralPath $PublishRoot -Recurse -Force
}

& dotnet publish $Project `
    -c Release -r win-x64 --self-contained true -o $PublishRoot `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:EnableCompressionInSingleFile=true
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$PublishedExe = Join-Path $PublishRoot "Y700Switch2V60Viiper.exe"
if (!(Test-Path -LiteralPath $PublishedExe)) {
    throw "Published exe not found: $PublishedExe"
}

Copy-Item -LiteralPath $PublishedExe -Destination $ExePath -Force
$Hash = Get-FileHash -Algorithm SHA256 -LiteralPath $ExePath
[System.IO.File]::WriteAllText(
    $HashPath,
    ($Hash.Hash.ToLowerInvariant() + "  " + $ExeName + "`r`n"),
    [System.Text.Encoding]::UTF8)
Remove-Item -LiteralPath $PublishRoot -Recurse -Force

Write-Output "[V6_2_32_TEST_R2_PACKAGE] exe=$ExePath"
Write-Output "[V6_2_32_TEST_R2_PACKAGE] sha256=$($Hash.Hash.ToLowerInvariant())"
