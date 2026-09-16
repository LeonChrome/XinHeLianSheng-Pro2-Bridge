[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$bridgeDir = Join-Path $repoRoot "firmware\esp32s3_switch2_bridge\main\bridge"
$dualsenseDir = Join-Path $repoRoot "firmware\esp32s3_dualsense_identity_experiment\main"
$testSource = Join-Path $PSScriptRoot "dualsense_report_mapper_test.c"
$mapperSource = Join-Path $dualsenseDir "dualsense_report_mapper.c"
$axisSource = Join-Path $bridgeDir "gamepad_axis_math.c"
$stateSource = Join-Path $bridgeDir "internal_gamepad_state.c"
$buildDir = Join-Path $repoRoot "work\b\tests\dualsense_report_mapper"
$exePath = Join-Path $buildDir "dualsense_report_mapper_test.exe"

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw "Visual Studio locator not found: $vswhere"
}
$vsPath = & $vswhere -latest -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath
if (-not $vsPath) {
    throw "Visual Studio C++ build tools are not installed"
}
$vcVars = Join-Path $vsPath "VC\Auxiliary\Build\vcvars64.bat"
$environmentCapture = Join-Path $PSScriptRoot "capture_msvc_environment.cmd"

$environmentLines = & $env:ComSpec /d /c $environmentCapture $vcVars
if ($LASTEXITCODE -ne 0) {
    throw "Visual Studio environment setup failed with exit code $LASTEXITCODE"
}
foreach ($line in $environmentLines) {
    $separator = $line.IndexOf("=")
    if ($separator -gt 0) {
        $name = $line.Substring(0, $separator)
        $value = $line.Substring($separator + 1)
        Set-Item -Path "Env:$name" -Value $value
    }
}

New-Item -ItemType Directory -Path $buildDir -Force | Out-Null
$cl = (Get-Command cl.exe -ErrorAction Stop).Source
Push-Location $buildDir
try {
    & $cl /nologo /W4 /WX /std:c11 `
        "/I$dualsenseDir" `
        "/I$bridgeDir" `
        $testSource `
        $mapperSource `
        $axisSource `
        $stateSource `
        "/Fe:$exePath"
    if ($LASTEXITCODE -ne 0) {
        throw "DualSense mapper test compilation failed with exit code $LASTEXITCODE"
    }
    & $exePath
    if ($LASTEXITCODE -ne 0) {
        throw "DualSense mapper tests failed with exit code $LASTEXITCODE"
    }
} finally {
    Pop-Location
}
