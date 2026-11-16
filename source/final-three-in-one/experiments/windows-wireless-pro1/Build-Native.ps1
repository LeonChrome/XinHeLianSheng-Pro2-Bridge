[CmdletBinding()]
param(
    [string]$BtstackRoot='D:\CODEX\.codex-work\btstack',
    [string]$Compiler='D:\CODEX\.codex-work\tools\llvm-mingw-20260922-ucrt-x86_64\bin\clang.exe',
    [string]$BuildDirectory='D:\CODEX\artifacts\pro2-v3.4.0\native'
)
$ErrorActionPreference='Stop'
if(-not(Test-Path -LiteralPath $Compiler)){throw 'Portable clang is missing.'}
if(-not(Test-Path -LiteralPath (Join-Path $BtstackRoot 'LICENSE'))){throw 'BTstack checkout and original license required.'}
$null=New-Item -ItemType Directory -Path $BuildDirectory -Force
$native=Join-Path $PSScriptRoot 'native'
$headers=@($native,'src','platform/windows','platform/embedded','platform/posix','chipset/realtek','3rd-party/bluedroid/encoder/include','3rd-party/bluedroid/decoder/include','3rd-party/rijndael','3rd-party/yxml')
$flags=@('-O2','-std=c11','-Wall','-Wextra','-ffunction-sections','-fdata-sections','-Wno-unused-parameter','-Wno-deprecated-declarations','-D_WIN32_WINNT=0x0601','-D_CRT_SECURE_NO_WARNINGS')
foreach($header in $headers){$resolved=if($header -eq $native){$header}else{Join-Path $BtstackRoot $header};$flags+=('-I'+$resolved)}
$core=@('ad_parser','btstack_hid','btstack_hid_parser','btstack_linked_list','btstack_linked_queue','btstack_memory','btstack_memory_pool','btstack_ring_buffer','btstack_run_loop','btstack_run_loop_base','btstack_util','hci','hci_cmd','hci_dump','hci_event','hci_event_builder','l2cap','l2cap_signaling')
$classic=@('btstack_link_key_db_memory','sdp_client','sdp_server','sdp_util','device_id_server')
$sources=@($core|ForEach-Object{Join-Path $BtstackRoot ('src/'+$_+'.c')})
$sources+=@($classic|ForEach-Object{Join-Path $BtstackRoot ('src/classic/'+$_+'.c')})
$sources+=@((Join-Path $BtstackRoot 'platform/windows/btstack_run_loop_windows.c'),(Join-Path $BtstackRoot 'chipset/realtek/btstack_chipset_realtek.c'),(Join-Path $native 'target_transport.c'),(Join-Path $native 'hid_device_checked.c'),(Join-Path $native 'wireless_pro1.c'))
$flags+=('-I'+(Join-Path $BtstackRoot 'src/classic'))
$objects=@()
foreach($source in $sources){
    $object=Join-Path $BuildDirectory ([IO.Path]::GetFileNameWithoutExtension($source)+'.o')
    # All sources are rebuilt: an experiment must not inherit stale config.
    $sourceFlags=@($flags)
    if([IO.Path]::GetFileName($source) -eq 'btstack_chipset_realtek.c'){$sourceFlags+=@('-include',(Join-Path $native 'utf8_files.h'))}
    & $Compiler @sourceFlags '-c' $source '-o' $object
    if($LASTEXITCODE -ne 0){throw ('Compile failed: '+$source)}
    $objects+=$object
}
$exe=Join-Path $BuildDirectory 'pro2-wireless-pro1-radio.exe'
& $Compiler @objects '-municode' '-Wl,--gc-sections' '-lsetupapi' '-lwinusb' '-lws2_32' '-o' $exe
if($LASTEXITCODE -ne 0){throw 'Native radio link failed.'}
& $exe '--self-test'
if($LASTEXITCODE -ne 0){throw 'Native offline checks failed.'}
Copy-Item -LiteralPath (Join-Path $BtstackRoot 'LICENSE') -Destination (Join-Path $BuildDirectory 'BTstack-LICENSE.txt')
$revision=& git -C $BtstackRoot rev-parse HEAD
$sourceHashes=@{};Get-ChildItem -LiteralPath $native -File|ForEach-Object{$sourceHashes[$_.Name]=(Get-FileHash -LiteralPath $_.FullName).Hash}
@{executable=$exe;sha256=(Get-FileHash -LiteralPath $exe).Hash;btstack_revision=$revision;sources=$sourceHashes;purpose='LOCAL_PERSONAL_EXPERIMENT_ONLY';system_changes=0}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $BuildDirectory 'build.json') -Encoding UTF8
Write-Output ('RADIO_EXE='+$exe)
