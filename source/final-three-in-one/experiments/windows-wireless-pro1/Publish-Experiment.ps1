#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$ArtifactDirectory='D:\CODEX\artifacts\pro2-v3.4.0',
    [string]$Dotnet='D:\CODEX\.dotnet-sdk\dotnet.exe',
    [string]$NativeDirectory='D:\CODEX\artifacts\pro2-v3.4.0\native',
    [string]$FirmwareDirectory='D:\CODEX\.codex-work\realtek-relay-test\firmware'
)
$ErrorActionPreference='Stop'
$env:DOTNET_ROOT=Split-Path -Parent $Dotnet
$env:DOTNET_CLI_HOME='D:\CODEX\.dotnet-cli-home'
$env:NUGET_PACKAGES='D:\CODEX\.nuget-packages'
$publish=Join-Path $ArtifactDirectory 'publish'
$null=New-Item -ItemType Directory -Path $publish -Force
& $Dotnet publish (Join-Path $PSScriptRoot 'WirelessPro1Experiment.csproj') -c Release -p:PureSoftwareRelease=true -p:CompatibilityRendering=true -p:EmbedUsbipInstaller=false -p:NuGetAudit=false -o $publish
if($LASTEXITCODE -ne 0){throw 'Managed experiment publish failed.'}
foreach($name in @('pro2-wireless-pro1-radio.exe','BTstack-LICENSE.txt')){
    Copy-Item -LiteralPath (Join-Path $NativeDirectory $name) -Destination (Join-Path $publish $name)
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $publish '使用说明.md')
$notices=Join-Path $publish 'Notices';$null=New-Item -ItemType Directory -Path $notices -Force
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Notices') -File|ForEach-Object{Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $notices $_.Name)}
if(Test-Path -LiteralPath (Join-Path $FirmwareDirectory 'rtl8852cu_fw')){
    $firmware=Join-Path $publish 'firmware';$null=New-Item -ItemType Directory -Path $firmware -Force
    if((Get-FileHash -LiteralPath (Join-Path $FirmwareDirectory 'rtl8852cu_fw')).Hash -ne '9B8B27741A914C1CECC1AB9837194236890522101A57BA1898EB55F8D538DAE0'){throw 'Realtek firmware hash mismatch.'}
    foreach($name in @('rtl8852cu_fw','LICENCE.rtlwifi_firmware.txt')){Copy-Item -LiteralPath (Join-Path $FirmwareDirectory $name) -Destination (Join-Path $firmware $name)}
    @{url='https://gitlab.com/kernel-firmware/linux-firmware/-/raw/main/rtl_bt/rtl8852cu_fw.bin';local_file='rtl8852cu_fw';sha256='9B8B27741A914C1CECC1AB9837194236890522101A57BA1898EB55F8D538DAE0';use='controller_RAM_only_not_flash'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $firmware 'source.json') -Encoding UTF8
}
# Referenced WinExe apphosts are not entry points of this experimental bundle.
foreach($name in @('PRO2ControllerAssistant.exe','PRO2ControllerAssistant.pdb','PRO2UsbWirelessPro1.pdb')){
    $extra=Join-Path $publish $name;if(Test-Path -LiteralPath $extra){Remove-Item -LiteralPath $extra}
}
$sums=Get-ChildItem -LiteralPath $publish -File -Recurse|Where-Object{$_.Name -ne 'SHA256SUMS.txt'}|ForEach-Object{(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()+'  '+[IO.Path]::GetRelativePath($publish,$_.FullName)}
$sums|Set-Content -LiteralPath (Join-Path $publish 'SHA256SUMS.txt') -Encoding UTF8
Write-Output ('EXPERIMENT_FOLDER='+$publish)
