[CmdletBinding()]
param(
    [switch]$Clean,
    [switch]$ClassicBtOnlyDiagnostic,
    [switch]$ClassicShortReportDiagnostic,
    [ValidateSet(0, 66, 125, 250)]
    [int]$ClassicReportRateLimitHz = 0,
    [ValidateRange(0, 4294967295)]
    [uint32]$PairingRecoveryToken = 0,
    [ValidatePattern("^r[0-9]+$")]
    [string]$Revision = "r37"
)

$ErrorActionPreference = "Stop"

$firmwareRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $firmwareRoot "..\..")).Path
$sdkPath = Join-Path $repoRoot ".toolchain\pico-sdk-2.3.0"
$toolchainPath = Join-Path $repoRoot ".toolchain\arm-gnu-toolchain-14.2.rel1-mingw-w64-x86_64-arm-none-eabi"
$picotoolDir = Join-Path $repoRoot ".toolchain\pt\picotool"
$cmake = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
$ninja = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe"
$vsDevCmd = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat"
$buildPath = Join-Path $repoRoot "build\pico2w-$Revision"
$releasePath = Join-Path $repoRoot "release\pico2w-pro2-wireless-test-$Revision"

foreach ($required in @(
    $sdkPath,
    (Join-Path $toolchainPath "bin\arm-none-eabi-gcc.exe"),
    (Join-Path $picotoolDir "picotoolConfig.cmake"),
    $cmake,
    $ninja,
    $vsDevCmd
)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required Pico build dependency is missing: $required"
    }
}

if ($Clean -and (Test-Path -LiteralPath $buildPath)) {
    $resolvedBuild = (Resolve-Path -LiteralPath $buildPath).Path
    if (-not $resolvedBuild.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to delete a build directory outside the repository: $resolvedBuild"
    }
    Remove-Item -LiteralPath $resolvedBuild -Recurse -Force
}

$driveLetter = $null
foreach ($candidate in "P", "Q", "R", "S", "T") {
    if (-not (Test-Path "$candidate`:\")) {
        $driveLetter = $candidate
        break
    }
}
if ($null -eq $driveLetter) {
    throw "No free temporary drive letter is available for the short Pico build path."
}

$drive = "$driveLetter`:"
& subst.exe $drive $repoRoot
if ($LASTEXITCODE -ne 0) {
    throw "Failed to create temporary build drive $drive"
}

try {
    $sourceShort = "$drive\firmware\pico2w_pro2_wireless_bridge"
    $buildShort = "$drive\build\pico2w-$Revision"
    $sdkShort = "$drive\.toolchain\pico-sdk-2.3.0"
    $toolchainShort = "$drive\.toolchain\arm-gnu-toolchain-14.2.rel1-mingw-w64-x86_64-arm-none-eabi"
    $picotoolShort = "$drive\.toolchain\pt\picotool"
    $hostToolPath = "C:\Program Files\Git\usr\bin"
    $hostPrefix = "set `"PATH=$hostToolPath;%PATH%`" &&"

    $configure = @(
        $hostPrefix,
        "call `"$vsDevCmd`" -arch=x64 -host_arch=x64 -no_logo",
        "&& `"$cmake`" -S `"$sourceShort`" -B `"$buildShort`" -G Ninja",
        "-DCMAKE_MAKE_PROGRAM=`"$ninja`"",
        "-DPICO_BOARD=pico2_w",
        "-DPICO_SDK_PATH=`"$sdkShort`"",
        "-DPICO_TOOLCHAIN_PATH=`"$toolchainShort`"",
        "-Dpicotool_DIR=`"$picotoolShort`"",
        "-DXHLS_FIRMWARE_REVISION=$Revision",
        "-DXHLS_CLASSIC_BT_ONLY_DIAGNOSTIC=$($ClassicBtOnlyDiagnostic.IsPresent ? 'ON' : 'OFF')",
        "-DXHLS_CLASSIC_SHORT_REPORT_DIAGNOSTIC=$($ClassicShortReportDiagnostic.IsPresent ? 'ON' : 'OFF')",
        "-DXHLS_CLASSIC_REPORT_RATE_LIMIT_HZ=$ClassicReportRateLimitHz",
        "-DXHLS_PAIRING_RECOVERY_TOKEN=$PairingRecoveryToken"
    ) -join " "
    & cmd.exe /d /s /c $configure
    if ($LASTEXITCODE -ne 0) {
        throw "Pico CMake configure failed with exit code $LASTEXITCODE"
    }

    $build = "$hostPrefix call `"$vsDevCmd`" -arch=x64 -host_arch=x64 -no_logo && `"$cmake`" --build `"$buildShort`" --parallel"
    & cmd.exe /d /s /c $build
    if ($LASTEXITCODE -ne 0) {
        throw "Pico build failed with exit code $LASTEXITCODE"
    }
} finally {
    & subst.exe $drive /d | Out-Null
}

New-Item -ItemType Directory -Path $releasePath -Force | Out-Null
$baseName = "xhls_pico2w_pro2_wireless_bridge"
$uf2Source = Join-Path $buildPath "$baseName.uf2"
$elfSource = Join-Path $buildPath "$baseName.elf"
$uf2Target = Join-Path $releasePath "XinHeLianSheng-Pico2W-Pro2-Wireless-Bridge-test-$Revision.uf2"
$elfTarget = Join-Path $releasePath "XinHeLianSheng-Pico2W-Pro2-Wireless-Bridge-test-$Revision.elf"
Copy-Item -LiteralPath $uf2Source -Destination $uf2Target -Force
Copy-Item -LiteralPath $elfSource -Destination $elfTarget -Force
Copy-Item -LiteralPath (Join-Path $firmwareRoot "README_ZH.md") -Destination $releasePath -Force

$hashLines = foreach ($file in @($uf2Target, $elfTarget)) {
    $hash = Get-FileHash -LiteralPath $file -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($file))"
}
$hashLines | Set-Content -LiteralPath (Join-Path $releasePath "SHA256SUMS.txt") -Encoding ascii

Write-Host ""
Write-Host "Pico 2 W $Revision package ready:"
Write-Host "  $uf2Target"
Write-Host "  $elfTarget"
Write-Host "  $(Join-Path $releasePath 'SHA256SUMS.txt')"
