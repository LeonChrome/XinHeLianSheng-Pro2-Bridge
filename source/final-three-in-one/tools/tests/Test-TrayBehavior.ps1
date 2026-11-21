param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$Results
)
$ErrorActionPreference = 'Stop'
$Executable = (Get-Item -LiteralPath $Executable).FullName
$root = (New-Item -ItemType Directory -Path (Join-Path $Results ([guid]::NewGuid().ToString('N')))).FullName
$env:V60_UI_SMOKE = '1'
$env:V60_UI_SMOKE_TRAY = '1'
function Start-Assistant([string[]]$Options) {
    $start = @{ FilePath=$Executable; PassThru=$true; WindowStyle='Hidden' }
    if ($Options) { $start.ArgumentList = $Options }
    Start-Process @start
}
function Wait-Success($Process) {
    if (!$Process.WaitForExit(60000)) { throw "UI check timed out (PID $($Process.Id))" }
    if ($Process.ExitCode -ne 0) { throw "UI check exited with $($Process.ExitCode)" }
}
function Expect-Lines([string[]]$Lines) {
    $content = Get-Content -LiteralPath $env:V60_UI_SMOKE_RESULT
    foreach ($line in $Lines) {
        if ($content -notcontains $line) { throw "Expected $line in $env:V60_UI_SMOKE_RESULT" }
    }
}
$env:V60_UI_SMOKE_RESULT = Join-Path $root 'login.txt'
$env:V60_UI_SMOKE_WAIT_ACTIVATION = '1'
$login = Start-Assistant @('--login-tray')
$deadline = [DateTime]::UtcNow.AddSeconds(30)
while (!(Test-Path -LiteralPath $env:V60_UI_SMOKE_RESULT)) {
    if ($login.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'Login instance failed to start' }
    Start-Sleep -Milliseconds 100
}
Wait-Success (Start-Assistant @('--login-tray'))
if ($login.HasExited) { throw 'Duplicate login unexpectedly activated the background window' }
Wait-Success (Start-Assistant @())
Wait-Success $login
$behaviors = @('MINIMIZE_TO_TASKBAR=True','CLOSE_TO_TRAY=True','RESTORED=True')
Expect-Lines (@('INITIAL_VISIBLE=False','INITIAL_TASKBAR=False','SECOND_LAUNCH_VISIBLE=True') + $behaviors)

$env:V60_UI_SMOKE_WAIT_ACTIVATION = '0'
$env:V60_UI_SMOKE_RESULT = Join-Path $root 'manual.txt'
Wait-Success (Start-Assistant @())
Expect-Lines (@('INITIAL_VISIBLE=True','INITIAL_TASKBAR=True') + $behaviors)
Write-Output "PASS login to tray, duplicate login, manual activation, taskbar minimize, close to tray, restore and exit. Results: $root"
