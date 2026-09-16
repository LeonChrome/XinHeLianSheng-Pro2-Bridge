[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$bridgeDir = Join-Path $repoRoot "firmware\esp32s3_switch2_bridge\main\bridge"
$testSource = Join-Path $PSScriptRoot "switch1_rumble_test.c"
$rumbleSource = Join-Path $bridgeDir "normalized_rumble.c"
$buildDir = Join-Path $repoRoot "work\b\tests\switch1_rumble"
$exePath = Join-Path $buildDir "switch1_rumble_test.exe"

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
        Set-Item -Path ("Env:" + $line.Substring(0, $separator)) `
            -Value $line.Substring($separator + 1)
    }
}

New-Item -ItemType Directory -Path $buildDir -Force | Out-Null
$cl = Get-ChildItem -LiteralPath (Join-Path $vsPath "VC\Tools\MSVC") `
    -Recurse -Filter cl.exe | Where-Object {
        $_.FullName -like "*\bin\Hostx64\x64\cl.exe"
    } | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $cl) {
    throw "cl.exe was not found under $vsPath"
}
Push-Location $buildDir
try {
    & $cl /nologo /W4 /WX /std:c11 "/I$bridgeDir" `
        $testSource $rumbleSource "/Fe:$exePath"
    if ($LASTEXITCODE -ne 0) {
        throw "Switch 1 rumble test compilation failed with exit code $LASTEXITCODE"
    }
    & $exePath
    if ($LASTEXITCODE -ne 0) {
        throw "Switch 1 rumble tests failed with exit code $LASTEXITCODE"
    }
} finally {
    Pop-Location
}
