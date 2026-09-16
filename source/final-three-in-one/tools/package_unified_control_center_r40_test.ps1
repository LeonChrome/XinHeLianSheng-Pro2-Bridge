[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $repoRoot "windows\unified_control_center\XinHeLianShengControlCenter.csproj"
$publishPath = Join-Path $repoRoot "build\publish-unified-control-center-r40-final-test"
$releasePath = Join-Path $repoRoot "release\unified-control-center-r40-final-test"
$brandName = -join ([char[]](0x65B0, 0x548C, 0x8054, 0x80DC))
$centerName = -join ([char[]](0x4E09, 0x5408, 0x4E00, 0x63A7, 0x5236, 0x4E2D, 0x5FC3))
$targetExe = Join-Path $releasePath "$brandName$centerName-R40-test.exe"

if (Test-Path -LiteralPath $releasePath) {
    throw "R40 unified release directory already exists; refusing to overwrite: $releasePath"
}

function Get-SingleExe([string]$directory, [string]$filter, [string]$label) {
    $files = @(Get-ChildItem -LiteralPath $directory -Filter $filter -File |
        Where-Object { $_.Name -notlike "Y700*" })
    if ($files.Count -ne 1) {
        throw "Expected one EXE for $label in $directory, found $($files.Count)."
    }
    return $files[0].FullName
}

$payloads = @(
    [pscustomobject]@{
        Key = "pico-r40"
        Path = Get-SingleExe (Join-Path $repoRoot "release\pico2w-pro2-wireless-r40-candidate") "*r40.exe" "Pico r40"
        Sha256 = "fae2630d0497ad744c5ee49534247eaae11ca442c4ac3f7460dfb9a42fb748f7"
    },
    [pscustomobject]@{
        Key = "esp-v5919"
        Path = Get-SingleExe (Join-Path $repoRoot "_worktrees\v5.9.18-input-macos-mode-stability\release\v5.9") "*v5.9.19.exe" "ESP v5.9.19"
        Sha256 = "0c438194dd9b3f1a2e39dbb5843486239c14650e583c5809746832f812b5aeba"
    },
    [pscustomobject]@{
        Key = "esp-switch-r4"
        Path = Get-SingleExe (Join-Path $repoRoot "_worktrees\v5.9.18-switch1-pro-test\release\v5.9") "*r4.exe" "ESP Switch r4"
        Sha256 = "97d9730d472adac2226d49e068f97234883f0e133e6a337036e4408ad3b0dce7"
    },
    [pscustomobject]@{
        Key = "viiper-v6232-r2"
        Path = Get-SingleExe (Join-Path $repoRoot "_worktrees\v6.2.32-stick-calibration\release\v6.2.32-test\r2") "*r2.exe" "VIIPER v6.2.32 r2"
        Sha256 = "4025a1cce1a013f84f129d8957076bc51246655428c7207f8dc1a459a8e8a1ee"
    }
)

foreach ($payload in $payloads) {
    $actual = (Get-FileHash -LiteralPath $payload.Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $payload.Sha256) {
        throw "Payload hash mismatch for $($payload.Key): expected=$($payload.Sha256) actual=$actual"
    }
}

& dotnet build $project -c Release --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Unified control center build failed with exit code $LASTEXITCODE"
}

New-Item -ItemType Directory -Path $publishPath -Force | Out-Null
New-Item -ItemType Directory -Path $releasePath | Out-Null

& dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $publishPath
if ($LASTEXITCODE -ne 0) {
    throw "Unified control center publish failed with exit code $LASTEXITCODE"
}

$publishedExe = Join-Path $publishPath "XinHeLianShengControlCenter.exe"
Copy-Item -LiteralPath $publishedExe -Destination $targetExe

$manifestEntries = @()
$bundle = [IO.File]::Open($targetExe, [IO.FileMode]::Open,
    [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    [void]$bundle.Seek(0, [IO.SeekOrigin]::End)
    foreach ($payload in $payloads) {
        [long]$offset = $bundle.Position
        $source = [IO.File]::OpenRead($payload.Path)
        try {
            [long]$length = $source.Length
            $source.CopyTo($bundle, 1048576)
        } finally {
            $source.Dispose()
        }
        $manifestEntries += [ordered]@{
            Key = $payload.Key
            Offset = $offset
            Length = $length
            Sha256 = $payload.Sha256
        }
    }

    $manifest = [ordered]@{ Format = 1; Entries = $manifestEntries }
    $json = $manifest | ConvertTo-Json -Compress -Depth 4
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $manifestBytes = $utf8.GetBytes($json)
    $bundle.Write($manifestBytes, 0, $manifestBytes.Length)
    $lengthBytes = [BitConverter]::GetBytes([long]$manifestBytes.Length)
    $bundle.Write($lengthBytes, 0, $lengthBytes.Length)
    $magic = [Text.Encoding]::ASCII.GetBytes("XHLS-PAYLOAD-v1!")
    $bundle.Write($magic, 0, $magic.Length)
    $bundle.Flush()
} finally {
    $bundle.Dispose()
}

Copy-Item -LiteralPath `
    (Join-Path $repoRoot "windows\unified_control_center\README_ZH.md") `
    -Destination (Join-Path $releasePath "README_ZH.md")

$verifyPath = Join-Path $releasePath "PACKAGE_VERIFY.txt"
$process = Start-Process -FilePath $targetExe `
    -ArgumentList @("--verify-package", $verifyPath) `
    -PassThru -Wait -WindowStyle Hidden
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $verifyPath)) {
    throw "Unified package verification failed. exit=$($process.ExitCode)"
}
$verifyText = Get-Content -LiteralPath $verifyPath -Raw
foreach ($expected in @(
    "result=passed",
    "backend=pico-r40",
    "backend=esp-v5919",
    "backend=esp-switch-r4",
    "backend=viiper-v6232-r2"
)) {
    if ($verifyText -notmatch [regex]::Escape($expected)) {
        throw "Unified package verification is missing: $expected"
    }
}

$files = @(
    $targetExe,
    (Join-Path $releasePath "README_ZH.md"),
    $verifyPath
)
$hashLines = foreach ($file in $files) {
    $hash = Get-FileHash -LiteralPath $file -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($file))"
}
$hashLines | Set-Content -LiteralPath `
    (Join-Path $releasePath "SHA256SUMS.txt") -Encoding utf8

Write-Host ""
Write-Host "Unified R40 test package ready:"
Write-Host "  $targetExe"
Write-Host "  $(Join-Path $releasePath 'PACKAGE_VERIFY.txt')"
Write-Host "  $(Join-Path $releasePath 'SHA256SUMS.txt')"
