param(
    [Parameter(Mandatory = $true)]
    [string]$IdfPath
)

$ErrorActionPreference = "Stop"
$idfCmake = Join-Path $IdfPath "tools\cmake\idf.cmake"
if (!(Test-Path -LiteralPath $idfCmake)) {
    throw "ESP-IDF CMake module was not found: $idfCmake"
}

$content = [IO.File]::ReadAllText($idfCmake)
$patched = $content.Replace(
    'get_filename_component(_idf_path "${CMAKE_CURRENT_LIST_DIR}/../.." REALPATH)',
    'get_filename_component(_idf_path "${CMAKE_CURRENT_LIST_DIR}/../.." ABSOLUTE)'
).Replace(
    'get_filename_component(idf_path "${idf_path}" REALPATH)',
    'get_filename_component(idf_path "${idf_path}" ABSOLUTE)'
)

$expected = @(
    'get_filename_component(_idf_path "${CMAKE_CURRENT_LIST_DIR}/../.." ABSOLUTE)',
    'get_filename_component(idf_path "${idf_path}" ABSOLUTE)'
)
foreach ($line in $expected) {
    if (!$patched.Contains($line)) {
        throw "ESP-IDF v5.4.2 short-path patch no longer matches tools/cmake/idf.cmake."
    }
}

if ($patched -ne $content) {
    [IO.File]::WriteAllText($idfCmake, $patched, [Text.UTF8Encoding]::new($false))
    Write-Host "[Y700_ENV] patched_idf=subst_path_preservation"
}

& git -C $IdfPath update-index --assume-unchanged tools/cmake/idf.cmake
if ($LASTEXITCODE -ne 0) {
    throw "Unable to mark the project-local ESP-IDF path patch as assumed unchanged."
}
