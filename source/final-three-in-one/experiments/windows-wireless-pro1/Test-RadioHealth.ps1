[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'RadioDriver.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Driver helper syntax failed.'}
foreach($name in @('Test-RadioHealthy','Wait-RadioHealthy')){
    $definition=$ast.Find({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true)
    Invoke-Expression $definition.Extent.Text
}
# Only pure health functions are loaded; driver entry points and PnP are not.
if(Test-RadioHealthy ([pscustomobject]@{status='Error';problem_code=43})){throw 'Code 43 was accepted as healthy.'}
if(Test-RadioHealthy ([pscustomobject]@{status='OK';problem_code=0;health_stable=$false})){throw 'Expired stability window was accepted.'}
if(-not(Test-RadioHealthy ([pscustomobject]@{status='OK';problem_code=0}))){throw 'Healthy original radio rejected.'}
function Read-Radio {[pscustomobject]@{status='OK';problem_code=0}}
$short=Wait-RadioHealthy -Seconds 1
if(Test-RadioHealthy $short){throw 'One final healthy read bypassed the required stability duration.'}
$stable=Wait-RadioHealthy -Seconds 5
if(-not(Test-RadioHealthy $stable)){throw 'Sustained healthy radio did not pass the bounded wait.'}
'PASS: 5 radio-health cases; Read-Radio mocked; no PnP, radio or driver operations.'
