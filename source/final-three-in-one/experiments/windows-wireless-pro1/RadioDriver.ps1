# Windows PowerShell 5.1. Exact-instance driver selection, fresh backup, guarded
# restoration. No driver-signature/trust changes and no downloads.
[CmdletBinding()]
param(
    [ValidateSet('Inspect','Prepare','Restore')][string]$Operation='Inspect',
    [Parameter(Mandatory=$true)][string]$InstanceId,
    [string]$StateDirectory,
    [int]$OwnerPid=0,
    [long]$OwnerStartTicks=0,
    [string]$BackendPath,
    [ValidateRange(0,60)][int]$MaxMinutes=12
)
$ErrorActionPreference='Stop'
# A PowerShell 7 parent can pass its module path through the .NET app into
# Windows PowerShell 5.1. Pin inbox modules before Authenticode/PnP commands;
# otherwise it attempts to load incompatible PowerShell 7 security assemblies.
$env:PSModulePath=(Join-Path $PSHOME 'Modules')+[IO.Path]::PathSeparator+(Join-Path $env:ProgramFiles 'WindowsPowerShell\Modules')
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1') -ErrorAction Stop
$stateRoot=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'PRO2WirelessReceiverControlBoard\radio'
$script:restartStages=@{}
$hardware=if($InstanceId -match '^USB\\VID_0BDA&PID_0852\\[A-Z0-9&_.-]+$'){'USB\VID_0BDA&PID_0852'}elseif($InstanceId -match '^USB\\VID_10D7&PID_B012\\[A-Z0-9&_.-]+$'){'USB\VID_10D7&PID_B012'}else{throw 'Unsupported exact radio instance.'}
function Read-Radio {
    $entry=Get-PnpDevice -InstanceId $InstanceId -PresentOnly -ErrorAction Stop
    $key=Get-ItemProperty -LiteralPath ('Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Enum\'+$InstanceId)
    if(-not @($key.HardwareID|Where-Object{$_ -ieq $hardware}).Count){throw 'Selected instance hardware IDs changed.'}
    $driver=Get-ItemProperty -LiteralPath ('Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Class\'+$key.Driver)
    $problem=Get-PnpDeviceProperty -InstanceId $InstanceId -KeyName 'DEVPKEY_Device_ProblemCode' -ErrorAction Stop
    [pscustomobject][ordered]@{instance=$InstanceId;hardware=$hardware;status=$entry.Status;problem_code=[int]$problem.Data;problem_name=[string]$entry.Problem;service=$key.Service;inf=$driver.InfPath;version=$driver.DriverVersion;provider=$driver.ProviderName;date=$driver.DriverDate;windows=[Environment]::OSVersion.Version.ToString()}
}
function Test-RadioHealthy($radio){$radio.status -eq 'OK' -and $radio.problem_code -eq 0 -and (-not $radio.PSObject.Properties['health_stable'] -or $radio.health_stable)}
function Test-RadioIdentity($Radio,$Expected){
    $Radio.service -ieq $Expected.service -and $Radio.inf -ieq $Expected.inf -and $Radio.version -eq $Expected.version
}
function Wait-RadioHealthy([int]$Seconds=22,[double]$StableSeconds=12,$Expected=$null){
    $watch=[Diagnostics.Stopwatch]::StartNew();$stableSince=-1.0;$last=$null
    while($watch.Elapsed.TotalSeconds -lt $Seconds){
        try{
            $last=Read-Radio
            if((Test-RadioHealthy $last) -and (-not $Expected -or (Test-RadioIdentity $last $Expected))){
                if($stableSince -lt 0){$stableSince=$watch.Elapsed.TotalSeconds}
                if($watch.Elapsed.TotalSeconds-$stableSince -ge $StableSeconds){return $last}
            }else{$stableSince=-1}
        }catch{$stableSince=-1}
        Start-Sleep -Milliseconds 300
    }
    if($last){$last|Add-Member -NotePropertyName health_stable -NotePropertyValue $false -Force;return $last};throw 'Selected radio did not reappear after the bounded wait.'
}
function Restart-SelectedRadio([string]$Stage){
    if($script:restartStages.ContainsKey($Stage)){throw 'The one selected-radio restart for this stage has already been used.'}
    $current=Read-Radio
    if($current.service -ine 'BTHUSB' -or $current.problem_code -notin @(10,43)){throw ('Radio restart not applicable: service='+$current.service+' status='+$current.status+' problem_code='+$current.problem_code)}
    $script:restartStages[$Stage]=$true
    Save-Json ($Stage+'-restart-before.json') $current
    Log ('Restarting selected instance only; stage='+$Stage+' instance='+$InstanceId+' problem_code='+$current.problem_code)
    $result=& (Join-Path $env:windir 'System32\pnputil.exe') /restart-device $InstanceId 2>&1
    $exitCode=$LASTEXITCODE;Log ($result -join [Environment]::NewLine)
    if($exitCode -ne 0){throw ('Selected-radio restart did not complete; exit='+$exitCode+'. No automatic Windows reboot.')}
}
function Require-Admin {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    if(-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator privileges required.'}
}
function Check-StatePath {
    $root=[IO.Path]::GetFullPath($stateRoot).TrimEnd('\')+'\'
    $full=[IO.Path]::GetFullPath($StateDirectory)
    if(-not $full.StartsWith($root,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetDirectoryName($full).TrimEnd('\') -ine $root.TrimEnd('\')){throw 'State must be a direct private child of the radio backup root.'}
    $walk=New-Object IO.DirectoryInfo($full)
    while($walk -and $walk.FullName.Length -ge $root.TrimEnd('\').Length){if($walk.Exists -and ($walk.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Reparse points are not allowed for driver backup state.'};$walk=$walk.Parent}
}
function Save-Json($name,$value){$value|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $StateDirectory $name) -Encoding UTF8}
function Log($value){Add-Content -LiteralPath (Join-Path $StateDirectory 'driver.log') -Value ((Get-Date -Format 'HH:mm:ss.fff')+' '+$value) -Encoding UTF8}
function Read-Baseline {
    $record=Get-Content -LiteralPath (Join-Path $StateDirectory 'baseline.json') -Raw -Encoding UTF8|ConvertFrom-Json
    if($record.adapter.instance -ine $InstanceId -or $record.adapter.hardware -ine $hardware -or $record.adapter.service -ine 'BTHUSB'){throw 'Backup does not belong to this system radio.'}
    if($record.adapter.windows -ne [Environment]::OSVersion.Version.ToString()){throw 'Windows build changed since backup; manual review required.'}
    $allowed=[IO.Path]::GetFullPath($StateDirectory).TrimEnd('\')+'\'
    foreach($file in $record.files){$path=[IO.Path]::GetFullPath((Join-Path $StateDirectory $file.relative));if(-not $path.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase) -or (Get-FileHash -LiteralPath $path).Hash -ne $file.sha256){throw 'Driver backup is missing or changed.'}}
    $record
}
function Find-WinUsb {
    $repository=Join-Path $env:windir 'System32\DriverStore\FileRepository'
    $filter=if($hardware -eq 'USB\VID_0BDA&PID_0852'){'pro2_relay_old_realtek_test.inf_amd64_*'}else{'pro2_relay_usb_10d7_b012.inf_amd64_*'}
    $packages=@(Get-ChildItem -LiteralPath $repository -Directory -Filter $filter)
    if($packages.Count -ne 1){throw 'No unique previously installed, exact-target WinUSB package. No driver was downloaded or installed.'}
    $infs=@(Get-ChildItem -LiteralPath $packages[0].FullName -Filter '*.inf' -File)
    $cats=@(Get-ChildItem -LiteralPath $packages[0].FullName -Filter '*.cat' -File)
    if($infs.Count -ne 1 -or $cats.Count -ne 1){throw 'Unexpected WinUSB package layout.'}
    $sig=Get-AuthenticodeSignature -LiteralPath $cats[0].FullName
    $expected='CN='+$hardware+' (libwdi autogenerated)'
    if($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -ne $expected){throw 'Installed exact-target catalog is not currently trusted.'}
    $text=Get-Content -LiteralPath $infs[0].FullName -Raw
    $deviceId=$hardware.Substring(4)
    if($text -notmatch ('(?im)^\s*DeviceID\s*=\s*"'+[regex]::Escape($deviceId)+'"\s*$') -or $text -notmatch '(?im)^\s*ServiceBinary\s*=\s*%12%\\WinUSB.sys\s*$' -or $text -notmatch '(?im)^\s*Include\s*=\s*winusb.inf\s*$' -or $text -match '(?i)MS_COMP_WINUSB'){throw 'WinUSB INF is not the exact-target Microsoft service binding.'}
    $expanded=$text.Replace('%DeviceID%',$deviceId)
    $ids=@([regex]::Matches($expanded,'(?i)USB\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}[^,\s"]*')|ForEach-Object Value|Sort-Object -Unique)
    if($ids.Count -ne 1 -or $ids[0] -ine $hardware){throw 'WinUSB INF matches other hardware.'}
    [pscustomobject]@{inf=$infs[0].FullName;catalog=$cats[0].FullName;directory=$packages[0].FullName}
}
function Install-Exact([string]$inf,[bool]$ProbeOnly=$false) {
    if(-not ('Pro2ExactRadioDriver' -as [type])){
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class Pro2ExactRadioDriver {
 [StructLayout(LayoutKind.Sequential)] struct Device {public uint Size;public Guid Class;public uint Node;public IntPtr Reserved;}
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct Params {public uint Size,Flags,FlagsEx;public IntPtr Parent,Callback,Context,Queue,ClassReserved;public uint Reserved;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string Path;}
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct Driver {public uint Size,Type;public IntPtr Reserved;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string Description;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string Manufacturer;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string Provider;public System.Runtime.InteropServices.ComTypes.FILETIME Date;public ulong Version;}
 [DllImport("setupapi.dll",SetLastError=true)]static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr cls,IntPtr parent);
 [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool SetupDiOpenDeviceInfoW(IntPtr set,string instance,IntPtr parent,uint flags,ref Device device);
 [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool SetupDiGetDeviceInstallParamsW(IntPtr set,ref Device device,ref Params value);
 [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool SetupDiSetDeviceInstallParamsW(IntPtr set,ref Device device,ref Params value);
 [DllImport("setupapi.dll",SetLastError=true)]static extern bool SetupDiBuildDriverInfoList(IntPtr set,ref Device device,uint type);
 [DllImport("setupapi.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool SetupDiEnumDriverInfoW(IntPtr set,ref Device device,uint type,uint index,ref Driver driver);
 [DllImport("setupapi.dll",SetLastError=true)]static extern bool SetupDiDestroyDriverInfoList(IntPtr set,ref Device device,uint type);
 [DllImport("setupapi.dll",SetLastError=true)]static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
 [DllImport("newdev.dll",SetLastError=true)]static extern bool DiInstallDevice(IntPtr parent,IntPtr set,ref Device device,ref Driver driver,uint flags,[MarshalAs(UnmanagedType.Bool)]out bool reboot);
 static void Check(bool ok,string step){if(!ok)throw new Win32Exception(Marshal.GetLastWin32Error(),step);}
 public static bool Install(string instance,string inf,bool apply){
  if(inf.Length>=260)throw new ArgumentException("INF path too long; no install attempted.");
  IntPtr set=SetupDiCreateDeviceInfoList(IntPtr.Zero,IntPtr.Zero);if(set==new IntPtr(-1))throw new Win32Exception(Marshal.GetLastWin32Error());
  var device=new Device{Size=(uint)Marshal.SizeOf(typeof(Device))};bool built=false;
  try{Check(SetupDiOpenDeviceInfoW(set,instance,IntPtr.Zero,0,ref device),"Open exact radio instance");
   var value=new Params{Size=(uint)Marshal.SizeOf(typeof(Params))};Check(SetupDiGetDeviceInstallParamsW(set,ref device,ref value),"Read install params");
   value.Flags|=0x00010000;value.Path=inf;Check(SetupDiSetDeviceInstallParamsW(set,ref device,ref value),"Select single INF");
   Check(SetupDiBuildDriverInfoList(set,ref device,2),"Build exact compatible-driver list");built=true;
   var driver=new Driver{Size=(uint)Marshal.SizeOf(typeof(Driver))};Check(SetupDiEnumDriverInfoW(set,ref device,2,0,ref driver),"Find compatible driver");
   if(!apply)return false;
   bool reboot;Check(DiInstallDevice(IntPtr.Zero,set,ref device,ref driver,0,out reboot),"Install driver on selected instance");return reboot;
  }finally{if(built)SetupDiDestroyDriverInfoList(set,ref device,2);SetupDiDestroyDeviceInfoList(set);}
 }
}
'@
    }
    [Pro2ExactRadioDriver]::Install($InstanceId,$inf,(-not $ProbeOnly))
}
function Test-OwnedBackendPath([string]$Path,[string]$RadioInstance){
    if(-not $Path -or -not [IO.Path]::IsPathRooted($Path)){return $false}
    $name=[IO.Path]::GetFileName($Path)
    $name -ieq 'pro2-wireless-pro1-radio.exe' -or
        ($name -ieq 'Pro1UpstreamComparisonRealtek.exe' -and $RadioInstance -match '^USB\\VID_0BDA&PID_0852\\[A-Z0-9&_.-]+$')
}
function Stop-OwnedRadio {
    $file=Join-Path $StateDirectory 'radio-process.json'
    if(-not(Test-Path -LiteralPath $file)){return}
    $record=Get-Content -LiteralPath $file -Raw -Encoding UTF8|ConvertFrom-Json
    $process=Get-Process -Id $record.pid -ErrorAction SilentlyContinue
    $baseline=Read-Baseline
    $expected=$baseline.native_executable
    if(-not(Test-OwnedBackendPath $expected $InstanceId) -or $record.exe -ine $expected){throw 'Recorded native process path is invalid.'}
    if($process -and $process.Path -ieq $expected -and $process.StartTime.ToUniversalTime().Ticks -eq $record.start_ticks){
        if((Get-FileHash -LiteralPath $expected).Hash -ne $baseline.native_sha256){throw 'Owned native backend changed since preparation; refusing to terminate another program.'}
        Log 'Stopping exact owned radio backend before driver restoration.'
        Stop-Process -InputObject $process -Force
        Start-Sleep -Milliseconds 500
    }
}
function Restore-Radio {
    $baseline=Read-Baseline
    Stop-OwnedRadio
    $before=Read-Radio
    if((Test-RadioIdentity $before $baseline.adapter) -and (Test-RadioHealthy $before)){
        $before=Wait-RadioHealthy -Seconds 14 -Expected $baseline.adapter
        if(Test-RadioHealthy $before){
            Save-Json 'restore-result.json' ([ordered]@{success=$true;status='ALREADY_RESTORED';after=$before;stable_seconds=12;reboot_required=$false});return
        }
    }
    if($before.service -ine 'WinUSB' -and $before.inf -ine $baseline.adapter.inf){throw 'Unexpected driver changed by another application; restore refused.'}
    $inf=Join-Path $env:windir ('INF\'+$baseline.adapter.inf)
    if(-not(Test-Path -LiteralPath $inf) -or (Get-FileHash -LiteralPath $inf).Hash -ne $baseline.original_inf_sha256){throw 'Original staged INF changed; backup retained, manual review required.'}
    Log ('Restoring original driver on exact instance: '+$baseline.adapter.inf)
    $reboot=Install-Exact $inf
    $after=Wait-RadioHealthy -Expected $baseline.adapter
    if(-not(Test-RadioHealthy $after) -and $after.service -ieq 'BTHUSB' -and $after.inf -ieq $baseline.adapter.inf -and $after.problem_code -in @(10,43) -and -not $script:restartStages.ContainsKey('restore')){
        Restart-SelectedRadio 'restore';$after=Wait-RadioHealthy -Expected $baseline.adapter
    }
    $ok=(Test-RadioIdentity $after $baseline.adapter) -and (Test-RadioHealthy $after)
    Save-Json 'restore-result.json' ([ordered]@{success=$ok;status=if($ok){'ORIGINAL_DRIVER_RESTORED'}else{'RESTORE_NEEDS_ATTENTION'};after=$after;stable_seconds=if($ok){12}else{0};reboot_required=$reboot})
    Log ('Restore completed: '+$ok+' reboot_required='+$reboot)
    if(-not $ok){throw 'Original driver not active yet. See restore result; no automatic reboot.'}
}

if($Operation -eq 'Inspect'){
    $result=[ordered]@{radio=(Read-Radio);known_binding_available=$false;error='';driver_changes=0}
    try{$package=Find-WinUsb;$null=Install-Exact $package.inf $true;$result.known_binding_available=$true;$result.binding_inf=[IO.Path]::GetFileName($package.inf);$result.exact_instance_driver_list='matched_without_install'}catch{$result.error=$_.Exception.Message}
    $result|ConvertTo-Json -Depth 5;exit 0
}
Require-Admin
Check-StatePath
if(-not(Test-Path -LiteralPath $StateDirectory -PathType Container)){throw 'Private driver state must be created by the application first.'}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$acl=Get-Acl -LiteralPath $StateDirectory
if(-not $acl.AreAccessRulesProtected -or @($acl.Access|Where-Object{$_.AccessControlType -eq 'Allow' -and $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -ne $identity.User.Value}).Count){throw 'Driver state directory is not private to the current user.'}
$lockName='Local\PRO2RadioDriver_'+([BitConverter]::ToString(([Security.Cryptography.SHA256]::Create()).ComputeHash([Text.Encoding]::UTF8.GetBytes($InstanceId.ToUpperInvariant())))).Replace('-','')
$mutex=New-Object Threading.Mutex($false,$lockName)
$owned=$false;$changed=$false;$restorationAttempted=$false
try{
    try{$owned=$mutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$owned=$true}
    if(-not $owned){throw 'Selected radio is already managed by another driver session.'}
    if($Operation -eq 'Restore'){Restore-Radio;exit 0}
    $before=Read-Radio
    Save-Json 'prepare-before.json' $before
    Log ('Prepare before: service='+$before.service+' inf='+$before.inf+' status='+$before.status+' problem_code='+$before.problem_code)
    if($before.service -ine 'BTHUSB' -or $before.inf -notmatch '^oem\d+\.inf$'){throw ('Selected system Bluetooth driver is not eligible: service='+$before.service+' inf='+$before.inf+' status='+$before.status+' problem_code='+$before.problem_code+'. No binding attempted.')}
    if(-not(Test-RadioHealthy $before) -and $before.problem_code -notin @(10,43)){throw ('Selected radio is not ready: status='+$before.status+' problem_code='+$before.problem_code+'. No binding attempted.')}
    if(Test-Path -LiteralPath (Join-Path $StateDirectory 'baseline.json')){throw 'Never overwrite an existing driver baseline.'}
    if($OwnerPid -le 0 -or $OwnerStartTicks -le 0){throw 'Owner process identity required.'}
    $owner=Get-Process -Id $OwnerPid
    if($owner.StartTime.ToUniversalTime().Ticks -ne $OwnerStartTicks){throw 'Owner process identity changed.'}
    $package=Find-WinUsb
    $backup=Join-Path $StateDirectory 'original';New-Item -ItemType Directory -Path $backup|Out-Null
    $export=& (Join-Path $env:windir 'System32\pnputil.exe') /export-driver $before.inf $backup 2>&1
    Log ($export -join [Environment]::NewLine)
    if($LASTEXITCODE -ne 0){throw 'Original driver export failed; no binding attempted.'}
    $originalInf=Join-Path $env:windir ('INF\'+$before.inf)
    $originalHash=(Get-FileHash -LiteralPath $originalInf).Hash
    $matched=@(Get-ChildItem -LiteralPath $backup -Recurse -Filter '*.inf' -File|Where-Object{(Get-FileHash -LiteralPath $_.FullName).Hash -eq $originalHash})
    if($matched.Count -ne 1){throw 'Exported original INF is not an exact match.'}
    $reg=& (Join-Path $env:windir 'System32\reg.exe') export ('HKLM\SYSTEM\CurrentControlSet\Enum\'+$InstanceId) (Join-Path $backup 'device-enum.reg') /y 2>&1
    if($LASTEXITCODE -ne 0){throw 'Original device configuration export failed.'}
    $files=@(Get-ChildItem -LiteralPath $backup -File -Recurse|ForEach-Object{[pscustomobject]@{relative=$_.FullName.Substring($StateDirectory.TrimEnd('\').Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
    $nativeExecutable=if($BackendPath){[IO.Path]::GetFullPath($BackendPath)}else{Join-Path (Split-Path -Parent $owner.Path) 'pro2-wireless-pro1-radio.exe'}
    if(-not(Test-OwnedBackendPath $nativeExecutable $InstanceId) -or -not(Test-Path -LiteralPath $nativeExecutable -PathType Leaf)){throw 'Exact native radio backend is missing.'}
    Save-Json 'baseline.json' ([ordered]@{adapter=$before;original_inf_sha256=$originalHash;files=$files;created=(Get-Date).ToString('o');owner_pid=$OwnerPid;owner_start_ticks=$OwnerStartTicks;owner_exe=$owner.Path;native_executable=$nativeExecutable;native_sha256=(Get-FileHash -LiteralPath $nativeExecutable).Hash;binding_inf=$package.inf;binding_inf_sha256=(Get-FileHash -LiteralPath $package.inf).Hash;binding_catalog_sha256=(Get-FileHash -LiteralPath $package.catalog).Hash})
    Log ('Fresh current-Windows baseline saved; original='+$before.inf+' version='+$before.version)
    $null=Read-Baseline
    if(-not(Test-RadioHealthy $before)){
        Restart-SelectedRadio 'prepare';$recovered=Wait-RadioHealthy -Expected $before
        Save-Json 'prepare-recovery-result.json' $recovered
        if(-not(Test-RadioHealthy $recovered) -or $recovered.service -ine $before.service -or $recovered.inf -ine $before.inf -or $recovered.version -ne $before.version){throw ('Selected radio recovery failed: service='+$recovered.service+' inf='+$recovered.inf+' status='+$recovered.status+' problem_code='+$recovered.problem_code+'. Original driver retained; no WinUSB binding attempted.')}
        Log 'Selected radio recovered; original driver identity unchanged.'
    }
    $changed=$true
    $reboot=Install-Exact $package.inf
    Start-Sleep -Seconds 2
    $after=Read-Radio
    if($after.service -ine 'WinUSB' -or $after.status -ne 'OK' -or $reboot){throw ('WinUSB not ready without reboot; reboot_required='+$reboot)}
    Save-Json 'ready.json' ([ordered]@{success=$true;status='EXACT_INSTANCE_WINUSB_READY';before=$before;after=$after;reboot_required=$reboot;guard_pid=$PID})
    Log 'WinUSB ready; watching owner and restore request. No signing/trust policy changes.'
    $watch=[Diagnostics.Stopwatch]::StartNew()
    while($MaxMinutes -eq 0 -or $watch.Elapsed.TotalMinutes -lt $MaxMinutes){
        if(Test-Path -LiteralPath (Join-Path $StateDirectory 'restore.request')){Log 'Restore requested by owner.';break}
        $owner=Get-Process -Id $OwnerPid -ErrorAction SilentlyContinue
        if(-not $owner -or $owner.StartTime.ToUniversalTime().Ticks -ne $OwnerStartTicks){Log 'Owner exited; restore automatically.';break}
        Start-Sleep -Milliseconds 500
    }
    $restorationAttempted=$true
    Restore-Radio
}catch{
    $errorMessage=$_.Exception.Message;Log ('FAILED: '+$errorMessage)
    if($changed -and -not $restorationAttempted){try{Restore-Radio}catch{Log ('AUTO_RESTORE_FAILED: '+$_.Exception.Message)}}
    if($owned -and ($changed -or $Operation -eq 'Restore') -and -not(Test-Path -LiteralPath (Join-Path $StateDirectory 'restore-result.json'))){
        Save-Json 'restore-result.json' ([ordered]@{success=$false;error=$errorMessage})
    }
    if(-not(Test-Path -LiteralPath (Join-Path $StateDirectory 'ready.json'))){Save-Json 'error.json' ([ordered]@{success=$false;error=$errorMessage})}
    exit 1
}finally{
    if($owned){$mutex.ReleaseMutex()};$mutex.Dispose()
}
