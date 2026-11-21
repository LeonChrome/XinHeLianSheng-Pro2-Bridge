[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'RadioDriver.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Driver helper syntax failed.'}
foreach($name in @('Test-RadioHealthy','Test-RadioIdentity','Wait-RadioHealthy','Test-OwnedBackendPath')){
    $definition=$ast.Find({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true)
    Invoke-Expression $definition.Extent.Text
}
# Only pure health functions are loaded; driver entry points and PnP are not.
if(Test-RadioHealthy ([pscustomobject]@{status='Error';problem_code=43})){throw 'Code 43 was accepted as healthy.'}
if(Test-RadioHealthy ([pscustomobject]@{status='OK';problem_code=0;health_stable=$false})){throw 'Expired stability window was accepted.'}
if(-not(Test-RadioHealthy ([pscustomobject]@{status='OK';problem_code=0}))){throw 'Healthy original radio rejected.'}
function Read-Radio {[pscustomobject]@{status='OK';problem_code=0;service='BTHUSB';inf='oem37.inf';version='18.4032.2510.900'}}
$expected=Read-Radio
$short=Wait-RadioHealthy -Seconds 4 -Expected $expected
if(Test-RadioHealthy $short){throw 'Four seconds of Code 0 bypassed the required stability duration.'}
$stable=Wait-RadioHealthy -Seconds 15 -Expected $expected
if(-not(Test-RadioHealthy $stable)){throw 'Sustained healthy original radio did not pass the bounded wait.'}
$mismatch=Wait-RadioHealthy -Seconds 1 -StableSeconds .3 -Expected ([pscustomobject]@{service='WinUSB';inf='oem32.inf';version='6.1'})
if(Test-RadioHealthy $mismatch){throw 'Stable radio with the wrong driver identity was accepted.'}
$exact='USB\VID_0BDA&PID_0852\00E04C000001'
$testBinary='D:\test\Pro1UpstreamComparisonRealtek.exe'
if(-not(Test-OwnedBackendPath $testBinary $exact) -or
    -not(Test-OwnedBackendPath $testBinary 'USB\VID_0BDA&PID_0852\ANOTHER_0852') -or
    (Test-OwnedBackendPath $testBinary 'USB\VID_0BDA&PID_0852\*') -or
    (Test-OwnedBackendPath $testBinary 'USB\VID_8087&PID_0033\ANOTHER') -or
    (Test-OwnedBackendPath $testBinary 'USB\VID_10D7&PID_B012\ACTIONS1234') -or
    (Test-OwnedBackendPath 'Pro1UpstreamComparisonRealtek.exe' $exact) -or
    (Test-OwnedBackendPath 'D:\test\any.exe' $exact)){
    throw 'Comparator allowlist accepted the wrong path or radio.'
}
'PASS: Code 43, 12-second health, driver identity, and exact comparator allowlist. No PnP, radio or driver operations.'
