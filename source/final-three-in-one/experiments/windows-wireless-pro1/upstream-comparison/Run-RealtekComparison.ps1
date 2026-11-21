#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$BinaryPath,
    [ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedSha256,
    [switch]$Run,
    [switch]$TemporaryWindowsRadio,
    [switch]$HomeTest,
    [string]$ReusePairingStateDirectory,
    [ValidateRange(35,300)][int]$TimeLimitSeconds=210
)
$ErrorActionPreference='Stop'
if(-not $Run){
    Write-Output 'Pass -Run -BinaryPath <Pro1UpstreamComparisonRealtek.exe> -ExpectedSha256 <build hash> to start.'
    return
}
if(-not $BinaryPath -or -not $ExpectedSha256){throw 'Both the exact comparator path and its build hash are required.'}

$instance='USB\VID_0BDA&PID_0852\00E04C000001'
$helperSource=Join-Path (Split-Path -Parent $PSScriptRoot) 'RadioDriver.ps1'
$windowsPowerShell=Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
$binary=(Get-Item -LiteralPath $BinaryPath).FullName
if([IO.Path]::GetFileName($binary) -ine 'Pro1UpstreamComparisonRealtek.exe' -or
    (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash -ine $ExpectedSha256){
    throw 'The exact Realtek comparator filename or SHA256 did not match.'
}

function Read-Result([string]$Path){
    if(Test-Path -LiteralPath $Path){
        try{Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json}
        catch [System.IO.IOException] {}
        catch [System.ArgumentException] {}
    }
}
function Quote-PowerShell([string]$Value){ "'" + $Value.Replace("'","''") + "'" }
function New-PrivateDirectory([string]$Path){
    $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User
    $security=[Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true,$false)
    $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
    [IO.FileSystemAclExtensions]::Create([IO.DirectoryInfo]::new($Path),$security)
}
function Assert-PrivatePairingDirectory([string]$Path){
    $item=Get-Item -LiteralPath $Path
    if(-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){
        throw 'Upstream pairing state must be an ordinary directory.'
    }
    $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User
    $security=Get-Acl -LiteralPath $Path
    $otherAllowed=@($security.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]) |
        Where-Object {$_.AccessControlType -eq 'Allow' -and $_.IdentityReference.Value -ne $sid.Value})
    if(-not $security.AreAccessRulesProtected -or $security.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $sid.Value -or $otherAllowed.Count){
        throw 'Upstream pairing state must be accessible only to the current user.'
    }
    $upstream=Join-Path $Path 'PRO2-Upstream-Comparison'
    foreach($subdirectory in @($upstream,(Join-Path $upstream 'btstack'))){
        if(Test-Path -LiteralPath $subdirectory){
            $subItem=Get-Item -LiteralPath $subdirectory
            if(-not $subItem.PSIsContainer -or ($subItem.Attributes -band [IO.FileAttributes]::ReparsePoint)){
                throw 'Upstream pairing state has an unexpected directory layout.'
            }
            $subSecurity=Get-Acl -LiteralPath $subdirectory
            if(@($subSecurity.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]) |
                Where-Object {$_.AccessControlType -eq 'Allow' -and $_.IdentityReference.Value -ne $sid.Value}).Count){
                throw 'An upstream pairing subdirectory grants access to another user.'
            }
        }
    }
}

$inspectOutput=& $windowsPowerShell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $helperSource -Operation Inspect -InstanceId $instance
if($LASTEXITCODE -ne 0){throw 'Exact Realtek inspection failed. No test started.'}
$inspection=($inspectOutput -join [Environment]::NewLine) | ConvertFrom-Json
if($inspection.radio.instance -ine $instance -or $inspection.radio.hardware -ine 'USB\VID_0BDA&PID_0852'){
    throw 'Inspection did not return the exact Realtek radio.'
}
$alreadyWinUsb=$inspection.radio.service -ieq 'WinUSB'
if($alreadyWinUsb){
    if($inspection.radio.status -ne 'OK' -or $inspection.radio.problem_code -ne 0){
        throw 'The existing WinUSB instance is not healthy.'
    }
}elseif(-not $TemporaryWindowsRadio -or $inspection.radio.service -ine 'BTHUSB' -or -not $inspection.known_binding_available){
    throw 'A healthy WinUSB instance is required. To prepare eligible BTHUSB with its existing trusted package, pass -TemporaryWindowsRadio.'
}

$local=[Environment]::GetFolderPath('LocalApplicationData')
$root=Join-Path $local 'PRO2WirelessReceiverControlBoard\radio'
New-Item -ItemType Directory -Path $root -Force | Out-Null
foreach($dir in @($root,(Split-Path -Parent $root))){
    if((Get-Item -LiteralPath $dir).Attributes -band [IO.FileAttributes]::ReparsePoint){
        throw 'The private radio backup directory cannot be a reparse point.'
    }
}
$pairingState=Join-Path $root 'upstream-realtek-00E04C000001'
if($ReusePairingStateDirectory){
    $pairingState=[IO.Path]::GetFullPath($ReusePairingStateDirectory)
    if([IO.Path]::GetDirectoryName($pairingState) -ine [IO.Path]::GetFullPath($root)){
        throw 'Reuse requires a previous private comparison directory within the radio backup directory.'
    }
}elseif(-not(Test-Path -LiteralPath $pairingState)){
    New-PrivateDirectory $pairingState
}
Assert-PrivatePairingDirectory $pairingState

$state=Join-Path $root ((Get-Date -Format 'yyyyMMdd-HHmmss-fff')+'-realtek-'+[Guid]::NewGuid().ToString('N').Substring(0,6))
New-PrivateDirectory $state
$staged=Join-Path $state 'Pro1UpstreamComparisonRealtek.exe'
Copy-Item -LiteralPath $binary -Destination $staged
if((Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash -ine $ExpectedSha256){
    throw 'Comparator copy failed verification.'
}
$log=Join-Path $state 'comparison.log'
Write-Output ('REALTEK_TEST_LOG='+$log)
Write-Output ('REALTEK_UPSTREAM_PAIRING_STATE='+$pairingState)
$guard=$null
$child=$null
$exitCode=1
$restoreError=$null
try{
    if(-not $alreadyWinUsb){
        $helper=Join-Path $state 'RadioDriver.ps1'
        Copy-Item -LiteralPath $helperSource -Destination $helper
        $restoreCommand="& "+(Quote-PowerShell $helper)+' -Operation Restore -InstanceId '+(Quote-PowerShell $instance)+
            ' -StateDirectory '+(Quote-PowerShell $state)
        $restoreEncoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($restoreCommand))
        $restoreFile=Join-Path $state 'Restore-Bluetooth.cmd'
        Set-Content -LiteralPath $restoreFile -Encoding Ascii -Value @(
            '@echo off',
            ('"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -Command "Start-Process -FilePath ([IO.Path]::Combine([Environment]::SystemDirectory,''WindowsPowerShell\v1.0\powershell.exe'')) -Verb RunAs -ArgumentList ''-NoProfile'',''-ExecutionPolicy'',''Bypass'',''-EncodedCommand'','''+$restoreEncoded+'''"')
        )
        $owner=Get-Process -Id $PID
        $command="& "+(Quote-PowerShell $helper)+' -Operation Prepare -InstanceId '+(Quote-PowerShell $instance)+
            ' -StateDirectory '+(Quote-PowerShell $state)+' -BackendPath '+(Quote-PowerShell $staged)+
            ' -OwnerPid '+$PID+' -OwnerStartTicks '+$owner.StartTime.ToUniversalTime().Ticks+' -MaxMinutes 6'
        $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
        $guard=Start-Process -FilePath $windowsPowerShell -Verb RunAs -WindowStyle Hidden -PassThru -ArgumentList @(
            '-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-EncodedCommand',$encoded
        )
        $timer=[Diagnostics.Stopwatch]::StartNew()
        do {
            $failure=Read-Result (Join-Path $state 'error.json')
            if($failure){throw ('Realtek preparation failed: '+$failure.error)}
            $ready=Read-Result (Join-Path $state 'ready.json')
            if($ready -and $ready.success -and $ready.guard_pid -eq $guard.Id -and -not $guard.HasExited){break}
            if($guard.HasExited -or $timer.Elapsed.TotalSeconds -ge 90){throw 'Realtek preparation did not confirm an active driver lease.'}
            Start-Sleep -Milliseconds 250
        }while($true)
        $baseline=Read-Result (Join-Path $state 'baseline.json')
        if(-not $baseline -or $baseline.native_executable -ine $staged -or
            $baseline.native_sha256 -ine $ExpectedSha256 -or $baseline.adapter.instance -ine $instance){
            throw 'The driver lease did not record this exact comparator and device.'
        }
        Write-Output ('REALTEK_DRIVER_LEASE='+$state)
    }

    $start=[Diagnostics.ProcessStartInfo]::new($staged)
    $start.UseShellExecute=$false
    $start.CreateNoWindow=$true
    $start.WorkingDirectory=$state
    $start.Environment['LOCALAPPDATA']=$pairingState
    $start.ArgumentList.Add('--log')
    $start.ArgumentList.Add($log)
    if($HomeTest){$start.ArgumentList.Add('--home-test')}
    $child=[Diagnostics.Process]::Start($start)
    if(-not $child){throw 'Could not start the verified comparator.'}
    if($guard){
        $record=[ordered]@{pid=$child.Id;start_ticks=$child.StartTime.ToUniversalTime().Ticks;exe=$staged}
        $pending=Join-Path $state 'radio-process.pending'
        $record | ConvertTo-Json -Compress | Set-Content -LiteralPath $pending -Encoding utf8BOM
        Move-Item -LiteralPath $pending -Destination (Join-Path $state 'radio-process.json')
    }
    $timer=[Diagnostics.Stopwatch]::StartNew()
    while(-not $child.HasExited){
        if($guard -and $guard.HasExited){throw 'The driver lease ended before the comparator.'}
        if($timer.Elapsed.TotalSeconds -ge $TimeLimitSeconds){throw 'The comparator observation reached its time limit.'}
        Start-Sleep -Milliseconds 250
    }
    $exitCode=$child.ExitCode
}finally{
    try{
        if($child -and -not $child.HasExited){
            try{$child.Kill()}catch{if(-not $child.HasExited){throw}}
            if(-not $child.WaitForExit(5000)){Write-Warning 'Owned comparator did not exit in five seconds.'}
        }
    }finally{
        if($guard){
            Set-Content -LiteralPath (Join-Path $state 'restore.request') -Value 'comparison_owner_stop'
            $timer=[Diagnostics.Stopwatch]::StartNew()
            do{
                $restore=Read-Result (Join-Path $state 'restore-result.json')
                if($restore -or $guard.HasExited -or $timer.Elapsed.TotalSeconds -ge 75){break}
                Start-Sleep -Milliseconds 250
            }while($true)
            if($restore -and $restore.success -and $restore.stable_seconds -ge 12){
                Write-Output ('REALTEK_DRIVER_RESTORED_STABLE_SECONDS='+$restore.stable_seconds)
            }elseif($null -ne (Read-Result (Join-Path $state 'ready.json')) -or $restore -or -not $guard.HasExited){
                $restoreError='Realtek restoration was not verified. See '+(Join-Path $state 'driver.log')+' and '+$restoreFile
                Write-Warning $restoreError
            } else {
                Write-Output 'The driver lease never became ready. See driver.log for preparation and any recovery attempt.'
            }
            $guard.Dispose()
        }
        if($child){$child.Dispose()}
    }
}
if($restoreError){exit 1}
Write-Output ('REALTEK_COMPARISON_EXIT='+$exitCode)
exit $exitCode
