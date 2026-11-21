[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Upstream,
    [Parameter(Mandatory)][string]$ToolBin,
    [Parameter(Mandatory)][string]$Output,
    [ValidateSet('Actions','Realtek')][string]$Adapter='Actions',
    [string]$FirmwareDirectory
)
$ErrorActionPreference = 'Stop'
$pin = '4b49cb955d08dc80a82673083fc0463cbc0c2540'
$tree = (Resolve-Path -LiteralPath $Upstream).Path
& (Join-Path $PSScriptRoot '../Assert-SourceRevision.ps1') -Root $tree -Commit $pin -ManifestHash '696c23eaadd23ed7f4ff23c34584cd1e2b5b2b1c5b41eb9f5e42cb2182c80561'
$port = Join-Path $tree 'port\windows-winusb'
$bin = (Resolve-Path -LiteralPath $ToolBin).Path
$env:Path = "$bin;$env:Path"
$make = Join-Path $bin 'mingw32-make.exe'
$folder = [IO.Directory]::CreateDirectory($Output).FullName
$overlay = $PSScriptRoot.Replace('\','/')
$target = 'Pro1UpstreamComparison.exe'
$extra = @()
if ($Adapter -eq 'Realtek') {
    if (!$FirmwareDirectory) { throw 'The Realtek build requires -FirmwareDirectory containing rtl8852cu_fw and its licence.' }
    $fw = (Resolve-Path -LiteralPath $FirmwareDirectory).Path
    $image = Join-Path $fw 'rtl8852cu_fw'
    if ((Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash -ne '9B8B27741A914C1CECC1AB9837194236890522101A57BA1898EB55F8D538DAE0') {
        throw 'Unexpected RTL8852CU RAM firmware hash.'
    }
    $licence = Get-Item -LiteralPath (Join-Path $fw 'LICENCE.rtlwifi_firmware.txt') -ErrorAction Stop
    $extra = @('FIRMWARE_DIR='+$fw.Replace('\','/'))
    $target = 'Pro1UpstreamComparisonRealtek.exe'
}
# These Windows-only recipes invoke native programs. Avoid picking up Git Bash
# from PATH for the quoted windres command on machines that have Git installed.
& $make -B -s -C $port -f Makefile -f "$overlay/comparison.mk" "COMPARISON_DIR=$overlay" 'SHELL=cmd.exe' 'CC=clang' @extra -j4 btkeyLib.exe $target
if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }
$exe = Join-Path $folder $target
& (Join-Path $bin 'llvm-strip.exe') -o $exe (Join-Path $port $target)
if ($LASTEXITCODE -ne 0) { throw 'Native packaging failed.' }
Copy-Item -LiteralPath (Join-Path $tree 'LICENSE') -Destination (Join-Path $folder 'BTstack-LICENSE.txt')
if ($Adapter -eq 'Realtek') { Copy-Item -LiteralPath $licence.FullName -Destination $folder }
& $exe --self-test
if ($LASTEXITCODE -ne 0) { throw 'Fixture check failed.' }
Get-FileHash -LiteralPath $exe | Select-Object Hash, Path
