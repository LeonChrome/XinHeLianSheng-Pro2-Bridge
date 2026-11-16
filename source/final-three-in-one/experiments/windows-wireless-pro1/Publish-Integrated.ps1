#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$ArtifactDirectory='D:\CODEX\artifacts\pro2-v3.4.0-integrated',
    [string]$Dotnet='D:\CODEX\.dotnet-sdk\dotnet.exe',
    [string]$FirmwareDirectory='D:\CODEX\.codex-work\realtek-relay-test\firmware',
    [switch]$SkipNativeBuild
)
$ErrorActionPreference='Stop'
$env:DOTNET_ROOT=Split-Path -Parent $Dotnet
$env:DOTNET_CLI_HOME='D:\CODEX\.dotnet-cli-home'
$env:NUGET_PACKAGES='D:\CODEX\.nuget-packages'
$native=Join-Path $ArtifactDirectory 'native'
New-Item -ItemType Directory -Path $ArtifactDirectory -Force | Out-Null
if(-not $SkipNativeBuild){& (Join-Path $PSScriptRoot 'Build-Native.ps1') -BuildDirectory $native}
if(-not(Test-Path -LiteralPath (Join-Path $native 'pro2-wireless-pro1-radio.exe'))){throw 'Build the native backend first.'}
$bundleRoot=Join-Path $ArtifactDirectory ('bundle-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $bundleRoot | Out-Null
function Copy-File([string]$source,[string]$relative){
    $destination=Join-Path $bundleRoot $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination
}
foreach($name in @('pro2-wireless-pro1-radio.exe','BTstack-LICENSE.txt')){Copy-File (Join-Path $native $name) $name}
Copy-File (Join-Path $PSScriptRoot 'RadioDriver.ps1') 'RadioDriver.ps1'
foreach($file in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Notices') -File){Copy-File $file.FullName ('Notices/'+$file.Name)}
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
& $Dotnet publish (Join-Path $PSScriptRoot '../../windows/v60_viiper_app/Y700Switch2V60Viiper.csproj') -c Release -p:PureSoftwareRelease=true -p:CompatibilityRendering=true -p:EmbedUsbipInstaller=true -p:EmbedWirelessRelay=true ('-p:WirelessRelayBundlePath='+$bundle) -p:NuGetAudit=false -o $publish
if($LASTEXITCODE -ne 0){throw 'Integrated assistant publish failed.'}
$exe=Join-Path $publish 'PRO2ControllerAssistant.exe'
@{version='3.4.0';exe=$exe;sha256=(Get-FileHash -LiteralPath $exe).Hash;wireless_bundle_sha256=(Get-FileHash -LiteralPath $bundle).Hash;purpose='LOCAL_PERSONAL_TEST_BUILD';radio_opened=$false;driver_changes=0}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $ArtifactDirectory 'build.json') -Encoding utf8
Write-Output ('INTEGRATED_EXE='+$exe)
