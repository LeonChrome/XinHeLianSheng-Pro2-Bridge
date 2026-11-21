[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $OutputDirectory){throw 'Use a new fixture directory.'}
New-Item -ItemType Directory -Path $OutputDirectory|Out-Null
$tokens=$null;$parseErrors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'RadioDriver.ps1'),[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw 'Driver helper did not parse.'}
$function=$ast.Find({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Stop-OwnedRadio'},$true)
if(-not $function){throw 'Owned-process guard not found.'}
# Load only this function. No entry point, PnP, driver install or UAC is run.
Invoke-Expression $function.Extent.Text
$allow=$ast.Find({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-OwnedBackendPath'},$true)
Invoke-Expression $allow.Extent.Text
$InstanceId='USB\VID_0BDA&PID_0852\ANOTHER_0852'
$StateDirectory=$OutputDirectory
$expected=Join-Path $OutputDirectory 'pro2-wireless-pro1-radio.exe'
[IO.File]::WriteAllText($expected,'inert fixture; never executed')
$baseline=[pscustomobject]@{native_executable=$expected;native_sha256=(Get-FileHash -LiteralPath $expected).Hash;owner_exe='D:\Different Owner\PRO2ControllerAssistant.exe'}
$start=[DateTime]::UtcNow
$record=[pscustomobject]@{pid=123;start_ticks=$start.Ticks;exe=$expected}
$record|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $StateDirectory 'radio-process.json') -Encoding UTF8
$script:fakeProcess=$null;$script:stopped=0
function Read-Baseline {$baseline}
function Get-Process {param([int]$Id,[object]$ErrorAction) $script:fakeProcess}
function Stop-Process {param([object]$InputObject,[switch]$Force) $script:stopped++}
function Start-Sleep {param([int]$Milliseconds)}
function Log {param($Message)}
Stop-OwnedRadio
if($script:stopped -ne 0){throw 'Must not terminate a missing process.'}
$script:fakeProcess=[pscustomobject]@{Id=123;Path=$expected;StartTime=$start.AddSeconds(1)}
Stop-OwnedRadio
if($script:stopped -ne 0){throw 'Reused PID must not be terminated.'}
$script:fakeProcess.StartTime=$start
Stop-OwnedRadio
if($script:stopped -ne 1){throw 'Exact owned child in extracted runtime directory was not recognized.'}
$record.exe='D:\Other\pro2-wireless-pro1-radio.exe'
$record|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $StateDirectory 'radio-process.json') -Encoding UTF8
$rejected=$false;try{Stop-OwnedRadio}catch{$rejected=$true}
if(-not $rejected -or $script:stopped -ne 1){throw 'Mismatched child record was not rejected.'}
$record.exe=$expected;$record|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $StateDirectory 'radio-process.json') -Encoding UTF8
[IO.File]::WriteAllText($expected,'changed fixture')
$rejected=$false;try{Stop-OwnedRadio}catch{$rejected=$true}
if(-not $rejected -or $script:stopped -ne 1){throw 'Changed backend was not rejected.'}
'PASS: 5 restoration process-ownership cases; all process calls mocked; radio_opened=false driver_changes=0.'
