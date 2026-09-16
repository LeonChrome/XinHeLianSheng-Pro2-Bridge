[CmdletBinding()]
param(
    [switch]$SkipFirmwareBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$revision = "r40"
$firmwareName = "XinHeLianSheng-Pico2W-Pro2-Wireless-Bridge-test-r40.uf2"
$firmwarePath = Join-Path $repoRoot "release\pico2w-pro2-wireless-test-r40\$firmwareName"
$project = Join-Path $repoRoot "windows\pico2w_flasher_app\Pico2WFlasherApp.csproj"
$publishPath = Join-Path $repoRoot "build\publish-pico2w-r40"
$releasePath = Join-Path $repoRoot "release\pico2w-pro2-wireless-r40-candidate"
$brandName = -join ([char[]](0x65B0, 0x548C, 0x8054, 0x80DC))
$toolName = -join ([char[]](0x4E00, 0x952E, 0x5237, 0x673A, 0x5DE5, 0x5177))
$targetExe = Join-Path $releasePath "$brandName-Pico2W$toolName-r40.exe"

if (Test-Path -LiteralPath $releasePath) {
    throw "R40 release directory already exists; refusing to overwrite: $releasePath"
}

if (-not $SkipFirmwareBuild) {
    & powershell.exe -ExecutionPolicy Bypass -File `
        (Join-Path $repoRoot "firmware\pico2w_pro2_wireless_bridge\build_r6.ps1") `
        -Revision $revision
    if ($LASTEXITCODE -ne 0) {
        throw "Pico 2 W firmware build failed with exit code $LASTEXITCODE"
    }
}

if (-not (Test-Path -LiteralPath $firmwarePath)) {
    throw "Firmware UF2 is missing: $firmwarePath"
}

& dotnet run --no-restore --project `
    (Join-Path $repoRoot "tools\tests\pico2w_flasher_test\Pico2WFlasherTest.csproj") `
    -c Release
if ($LASTEXITCODE -ne 0) {
    throw "Pico flasher tests failed with exit code $LASTEXITCODE"
}

New-Item -ItemType Directory -Path $publishPath -Force | Out-Null
New-Item -ItemType Directory -Path $releasePath | Out-Null

& dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $publishPath
if ($LASTEXITCODE -ne 0) {
    throw "Pico flasher publish failed with exit code $LASTEXITCODE"
}

$publishedExe = Join-Path $publishPath "XinHeLianShengPico2WFlasher.exe"
Copy-Item -LiteralPath $publishedExe -Destination $targetExe
Copy-Item -LiteralPath $firmwarePath -Destination (Join-Path $releasePath $firmwareName)
foreach ($document in @("README_ZH.md", "FINAL_ACCEPTANCE_ZH.md", "PLATFORM_PAIRING_ZH.md")) {
    Copy-Item -LiteralPath `
        (Join-Path $repoRoot "windows\pico2w_flasher_app\$document") `
        -Destination (Join-Path $releasePath $document)
}

$verifyPath = Join-Path $releasePath "PACKAGE_VERIFY.txt"
$process = Start-Process -FilePath $targetExe `
    -ArgumentList @("--verify-package", $verifyPath) `
    -PassThru -Wait -WindowStyle Hidden
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $verifyPath)) {
    throw "Published EXE package verification failed. exit=$($process.ExitCode)"
}
$verifyText = Get-Content -LiteralPath $verifyPath -Raw
if ($verifyText -notmatch "result=passed" -or
    $verifyText -notmatch "revision=r40" -or
    $verifyText -notmatch "preserves_settings=true") {
    throw "Published EXE package verification returned unexpected data: $verifyText"
}

$files = @(
    $targetExe,
    (Join-Path $releasePath $firmwareName),
    (Join-Path $releasePath "README_ZH.md"),
    (Join-Path $releasePath "FINAL_ACCEPTANCE_ZH.md"),
    (Join-Path $releasePath "PLATFORM_PAIRING_ZH.md"),
    $verifyPath
)
$hashLines = foreach ($file in $files) {
    $hash = Get-FileHash -LiteralPath $file -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($file))"
}
$hashLines | Set-Content -LiteralPath `
    (Join-Path $releasePath "SHA256SUMS.txt") -Encoding utf8

Write-Host ""
Write-Host "Pico 2 W r40 candidate package ready:"
Write-Host "  $targetExe"
Write-Host "  $firmwarePath"
Write-Host "  $(Join-Path $releasePath 'SHA256SUMS.txt')"
