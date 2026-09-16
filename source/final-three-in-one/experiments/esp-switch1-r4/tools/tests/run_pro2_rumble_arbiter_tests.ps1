[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$sourceDir = Join-Path $repoRoot "firmware\esp32s3_dualsense_identity_experiment\main"
$testSource = Join-Path $PSScriptRoot "pro2_rumble_arbiter_test.c"
$arbiterSource = Join-Path $sourceDir "pro2_rumble_arbiter.c"
$intentSource = Join-Path $sourceDir "dualsense_rumble_intent.c"
$buildDir = Join-Path $repoRoot "work\b\tests\pro2_rumble_arbiter"
$exePath = Join-Path $buildDir "pro2_rumble_arbiter_test.exe"

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
        "/I$sourceDir" `
        $testSource `
        $arbiterSource `
        $intentSource `
        "/Fe:$exePath"
    if ($LASTEXITCODE -ne 0) {
        throw "Native arbiter test compilation failed with exit code $LASTEXITCODE"
    }
    & $exePath
    if ($LASTEXITCODE -ne 0) {
        throw "Native arbiter tests failed with exit code $LASTEXITCODE"
    }
} finally {
    Pop-Location
}
