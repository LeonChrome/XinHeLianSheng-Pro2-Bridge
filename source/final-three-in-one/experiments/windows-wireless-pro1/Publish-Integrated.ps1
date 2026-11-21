#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$ArtifactDirectory=(Join-Path $PSScriptRoot '../../../../artifacts/pro2-v4.0'),
    [string]$Dotnet='dotnet',
    [string]$FirmwareDirectory=(Join-Path $PSScriptRoot '../../../../third_party/realtek-firmware'),
    [switch]$SkipNativeBuild,
    [Alias('UpstreamRealtekExecutable')][string]$Switch1Executable,
    [string]$BtkeyRoot=(Join-Path $PSScriptRoot '../../../../third_party/btkey-btstack'),
    [string]$ToolBin
)
$ErrorActionPreference='Stop'
if(-not(Test-Path -LiteralPath $Dotnet)){
    $resolved=Get-Command $Dotnet -ErrorAction SilentlyContinue
    if($null -eq $resolved){throw '.NET 8 SDK is missing. Add dotnet to PATH or pass -Dotnet with its full path.'}
    $Dotnet=$resolved.Source
}
$env:DOTNET_ROOT=Split-Path -Parent ([IO.Path]::GetFullPath($Dotnet))
$ArtifactDirectory=[IO.Path]::GetFullPath($ArtifactDirectory)
$native=Join-Path $ArtifactDirectory 'native'
New-Item -ItemType Directory -Path $ArtifactDirectory -Force | Out-Null
if(-not $SkipNativeBuild){
    $nativeOptions=@{BuildDirectory=$native}
    if($ToolBin){$nativeOptions.Compiler=Join-Path $ToolBin 'clang.exe'}
    & (Join-Path $PSScriptRoot 'Build-Native.ps1') @nativeOptions
}
if(-not(Test-Path -LiteralPath (Join-Path $native 'pro2-wireless-pro1-radio.exe'))){throw 'Build the native backend first.'}
$bundleRoot=Join-Path $ArtifactDirectory ('bundle-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $bundleRoot | Out-Null
function Copy-File([string]$source,[string]$relative){
    $destination=Join-Path $bundleRoot $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination
}
foreach($name in @('pro2-wireless-pro1-radio.exe','BTstack-LICENSE.txt')){Copy-File (Join-Path $native $name) $name}
if(!$Switch1Executable){
    $switchDir=Join-Path $ArtifactDirectory 'switch1'
    $Switch1Executable=Join-Path $switchDir 'Pro1UpstreamComparisonRealtek.exe'
    if(!$SkipNativeBuild){
        if(!$ToolBin){$ToolBin=Split-Path -Parent (Get-Command mingw32-make -ErrorAction Stop).Source}
        & (Join-Path $PSScriptRoot 'upstream-comparison/Build-Comparison.ps1') -Upstream $BtkeyRoot -ToolBin $ToolBin -Output $switchDir -Adapter Realtek -FirmwareDirectory $FirmwareDirectory
    }elseif(!(Test-Path -LiteralPath $Switch1Executable)){throw 'Provide -Switch1Executable or build both native backends before using -SkipNativeBuild.'}
}
$component=(Get-Item -LiteralPath $Switch1Executable).FullName
$protocol=& $component --protocol-info
if($LASTEXITCODE -ne 0){throw 'Switch 1 backend does not support the v4 relay protocol.'}
$capabilities=$protocol | ConvertFrom-Json
if($capabilities.relay_protocol -ne 4 -or $capabilities.exact_instance -ne $true -or $capabilities.readonly_ntag215 -ne $true){throw 'Switch 1 backend is missing adapter selection or NFC support.'}
& $component --self-test
if($LASTEXITCODE -ne 0){throw 'Switch 1 backend self-test failed.'}
Copy-File $component 'Pro1UpstreamComparisonRealtek.exe'
Copy-File (Join-Path $PSScriptRoot 'RadioDriver.ps1') 'RadioDriver.ps1'
foreach($file in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Notices') -File){Copy-File $file.FullName ('Notices/'+$file.Name)}
$btkey=Join-Path $PSScriptRoot 'native/third_party/btkeylib'
Copy-File (Join-Path $btkey 'LICENSE') 'Notices/btkeyLib-LICENSE.txt'
Copy-File (Join-Path $btkey 'ADAPTATION.md') 'Notices/btkeyLib-ADAPTATION.md'
$firmware=Join-Path $FirmwareDirectory 'rtl8852cu_fw'
if((Get-FileHash -LiteralPath $firmware).Hash -ne '9B8B27741A914C1CECC1AB9837194236890522101A57BA1898EB55F8D538DAE0'){throw 'Realtek firmware hash mismatch.'}
Copy-File $firmware 'firmware/rtl8852cu_fw'
Copy-File (Join-Path $FirmwareDirectory 'LICENCE.rtlwifi_firmware.txt') 'firmware/LICENCE.rtlwifi_firmware.txt'
@{url='https://gitlab.com/kernel-firmware/linux-firmware/-/raw/main/rtl_bt/rtl8852cu_fw.bin';sha256='9B8B27741A914C1CECC1AB9837194236890522101A57BA1898EB55F8D538DAE0';use='controller_RAM_only_not_flash'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $bundleRoot 'firmware/source.json') -Encoding utf8
Copy-File (Join-Path $PSScriptRoot 'INTEGRATED.md') '使用说明.md'
$manifest=[ordered]@{}
foreach($file in Get-ChildItem -LiteralPath $bundleRoot -File -Recurse){$manifest[[IO.Path]::GetRelativePath($bundleRoot,$file.FullName).Replace('\','/')]=(Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant()}
$manifest|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $bundleRoot 'runtime-manifest.json') -Encoding utf8
$bundle=Join-Path $ArtifactDirectory 'wireless-runtime.zip'
if(Test-Path -LiteralPath $bundle){Remove-Item -LiteralPath $bundle}
[IO.Compression.ZipFile]::CreateFromDirectory($bundleRoot,$bundle)
$publish=Join-Path $ArtifactDirectory 'publish'
& $Dotnet publish (Join-Path $PSScriptRoot '../../windows/v60_viiper_app/Y700Switch2V60Viiper.csproj') -c Release -p:PureSoftwareRelease=true -p:CompatibilityRendering=true -p:EmbedUsbipInstaller=true -p:EmbedWirelessRelay=true -p:EmbedUpstreamRealtek=true ('-p:WirelessRelayBundlePath='+$bundle) -p:NuGetAudit=false -o $publish
if($LASTEXITCODE -ne 0){throw 'Integrated assistant publish failed.'}
$exe=Join-Path $publish 'PRO2ControllerAssistant.exe'
@{version=(Get-Item -LiteralPath $exe).VersionInfo.FileVersion;exe=$exe;sha256=(Get-FileHash -LiteralPath $exe).Hash;wireless_bundle_sha256=(Get-FileHash -LiteralPath $bundle).Hash;purpose='PERSONAL_NONCOMMERCIAL';radio_opened=$false;driver_changes=0}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'build.json') -Encoding utf8
Write-Output ('INTEGRATED_EXE='+$exe)
