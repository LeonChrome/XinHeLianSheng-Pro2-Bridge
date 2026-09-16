param(
    [string]$IdfPath,
    [ValidateSet(
        "hid_only",
        "hid_composite_dummy_interface_class_00",
        "hid_composite_dummy_interface_class_ef",
        "hid_audio_control_only",
        "hid_audio_streaming_alt0_only",
        "hid_audio_uac1_2ch",
        "hid_audio_uac1_4ch_dualsense",
        "hid_audio_uac1_4ch_ds5like",
        "hid_audio_uac1_4ch_edge",
        "hid_audio_uac2_2ch",
        "hid_audio_uac2_4ch",
        "hid_audio_uac2",
        "hid_audio_uac1_fallback"
    )]
    [string]$Profile = "hid_audio_uac2_4ch",
    [switch]$Clean
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "idf_environment.ps1")
$RepoRoot = Get-Y700ShortRepoRoot
$FirmwareRoot = Join-Path $RepoRoot "firmware\esp32s3_dualsense_identity_experiment"
$RequestedProfile = $Profile
if ($Profile -eq "hid_audio_uac2") {
    Write-Warning "hid_audio_uac2 is an alias for hid_audio_uac2_4ch"
    $Profile = "hid_audio_uac2_4ch"
}
if ($Profile -eq "hid_audio_uac1_fallback") {
    Write-Warning "hid_audio_uac1_fallback is an alias for hid_audio_uac1_2ch"
    $Profile = "hid_audio_uac1_2ch"
}
$BuildRoot = Join-Path $RepoRoot ("work\b\ds5\{0}" -f $Profile)

$IdfPath = Resolve-Y700IdfPath -RequestedPath $IdfPath
Import-Y700IdfEnvironment -IdfPath $IdfPath

Write-Output "[V5_5_DS5_BUILD] firmware=firmware/esp32s3_dualsense_identity_experiment"
Write-Output "[V5_5_DS5_BUILD] build_dir=work/b/ds5/$Profile"
Write-Output "[V5_5_DS5_BUILD] identity=dualsense_experimental"
Write-Output "[V5_5_DS5_BUILD] requested_profile=$RequestedProfile"
Write-Output "[V5_5_DS5_BUILD] profile=$Profile"
Write-Output "[V5_5_DS5_BUILD] v5_2_default_unchanged=true"

Push-Location $FirmwareRoot
try {
    $sdkconfig = Join-Path $FirmwareRoot "sdkconfig"
    $needsTargetReset = !(Test-Path -LiteralPath $sdkconfig)
    if (!$needsTargetReset) {
        $needsTargetReset = !(Select-String -LiteralPath $sdkconfig -Pattern '^CONFIG_BT_ENABLED=y$' -Quiet)
    }
    if ($needsTargetReset) {
        if (Test-Path -LiteralPath $sdkconfig) {
            Write-Output "[V5_5_DS5_BUILD] sdkconfig_reset=phase2_bluetooth_defaults"
            Remove-Item -LiteralPath $sdkconfig -Force
        }
        if (Test-Path -LiteralPath (Join-Path $BuildRoot "CMakeCache.txt")) {
            & idf.py -B $BuildRoot -D "V5_5_DS5_PROFILE=$Profile" reconfigure
        } else {
            & idf.py -B $BuildRoot set-target esp32s3
        }
        if ($LASTEXITCODE -ne 0) {
            throw "ESP-IDF target/configure failed: $LASTEXITCODE"
        }
    }
    if ($Clean) {
        & idf.py -B $BuildRoot fullclean
        if ($LASTEXITCODE -ne 0) {
            throw "idf.py fullclean failed: $LASTEXITCODE"
        }
    }
    & idf.py -B $BuildRoot -D "V5_5_DS5_PROFILE=$Profile" build
    if ($LASTEXITCODE -ne 0) {
        throw "idf.py build failed: $LASTEXITCODE"
    }
} finally {
    Pop-Location
}

Write-Output "[V5_5_DS5_BUILD] result=passed"
