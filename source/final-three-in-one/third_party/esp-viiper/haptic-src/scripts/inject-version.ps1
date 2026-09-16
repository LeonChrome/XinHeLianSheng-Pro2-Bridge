param(
    [string]$Version,
    [string]$InputJson,
    [string]$OutputJson
)

$ver = $Version.TrimStart('v')
$parts = $ver -split '[.-]'
$major = [int]$parts[0]
$minor = if ($parts.Length -gt 1) { [int]$parts[1] } else { 0 }
$patch = if ($parts.Length -gt 2) { [int]$parts[2] } else { 0 }
$build = 0
if ($parts.Length -gt 3) {
    $buildStr = $parts[3]
    if ($buildStr -match '^\d+$') {
        $build = [int]$buildStr
    }
}

$json = Get-Content $InputJson -Raw | ConvertFrom-Json
$json.FixedFileInfo.FileVersion.Major = $major
$json.FixedFileInfo.FileVersion.Minor = $minor
$json.FixedFileInfo.FileVersion.Patch = $patch
$json.FixedFileInfo.FileVersion.Build = $build
$json.FixedFileInfo.ProductVersion.Major = $major
$json.FixedFileInfo.ProductVersion.Minor = $minor
$json.FixedFileInfo.ProductVersion.Patch = $patch
$json.FixedFileInfo.ProductVersion.Build = $build
$json.StringFileInfo.FileVersion = "$major.$minor.$patch.$build"
$json.StringFileInfo.ProductVersion = $ver

$json | ConvertTo-Json -Depth 10 | Set-Content $OutputJson
