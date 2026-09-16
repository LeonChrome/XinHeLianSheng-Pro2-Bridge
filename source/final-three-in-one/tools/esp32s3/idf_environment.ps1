$script:Y700RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script:Y700LocalIdfRelativePath = ".toolchain\esp-idf-v5.4.2"
$script:Y700LocalToolsRelativePath = ".toolchain\espressif-tools"
$script:Y700LocalPythonRelativePath = ".toolchain\python-3.11.9\python.exe"

function Get-Y700ShortRepoRoot {
    if ($script:Y700ShortRepoRoot) {
        return $script:Y700ShortRepoRoot
    }

    if ($env:OS -ne "Windows_NT") {
        $script:Y700ShortRepoRoot = $script:Y700RepoRoot
        return $script:Y700ShortRepoRoot
    }

    $repoRoot = [IO.Path]::GetFullPath($script:Y700RepoRoot).TrimEnd('\')
    $mappings = @{}
    foreach ($line in (& subst 2>$null)) {
        if ($line -match '^([A-Z]):\\: => (.+)$') {
            $mappings[$matches[1]] = [IO.Path]::GetFullPath($matches[2]).TrimEnd('\')
        }
    }

    foreach ($driveLetter in @("Y", "X", "W", "V", "U")) {
        if ($mappings.ContainsKey($driveLetter) -and
            $mappings[$driveLetter].Equals($repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
            $script:Y700ShortRepoRoot = "${driveLetter}:\"
            return $script:Y700ShortRepoRoot
        }
    }

    foreach ($driveLetter in @("Y", "X", "W", "V", "U")) {
        $driveRoot = "${driveLetter}:\"
        if (!$mappings.ContainsKey($driveLetter) -and !(Test-Path -LiteralPath $driveRoot)) {
            & subst "${driveLetter}:" $repoRoot
            if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $driveRoot)) {
                throw "Unable to create the ESP-IDF short-path mapping at ${driveLetter}:."
            }
            $script:Y700ShortRepoRoot = $driveRoot
            Write-Host "[Y700_ENV] short_root=$driveRoot => $repoRoot"
            return $script:Y700ShortRepoRoot
        }
    }

    throw "No free drive letter was available for the ESP-IDF Windows short-path mapping."
}

function ConvertTo-Y700ShortPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $repoRoot = [IO.Path]::GetFullPath($script:Y700RepoRoot).TrimEnd('\')
    if ($fullPath.Equals($repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
        return (Get-Y700ShortRepoRoot)
    }

    $repoPrefix = "$repoRoot\"
    if ($fullPath.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        return Join-Path (Get-Y700ShortRepoRoot) $fullPath.Substring($repoPrefix.Length)
    }

    return $fullPath
}

function Resolve-Y700IdfPath {
    param([string]$RequestedPath)

    $localIdfPath = Join-Path $script:Y700RepoRoot $script:Y700LocalIdfRelativePath
    $candidates = @()
    if ($RequestedPath) {
        $candidates += $RequestedPath
    }
    $candidates += $localIdfPath
    if ($env:IDF_PATH) {
        $candidates += $env:IDF_PATH
    }
    $candidates += "C:\Espressif\v5.4.2\esp-idf"

    foreach ($candidate in $candidates) {
        if (!$candidate) {
            continue
        }

        $candidatePath = [IO.Path]::GetFullPath($candidate)
        if (Test-Path -LiteralPath (Join-Path $candidatePath "export.ps1")) {
            return ConvertTo-Y700ShortPath $candidatePath
        }
    }

    throw "ESP-IDF 5.4.2 was not found. Run tools\setup_dev_environment.ps1."
}

function Get-Y700LocalPythonEnvironment {
    $localToolsPath = Join-Path (Get-Y700ShortRepoRoot) $script:Y700LocalToolsRelativePath
    $pythonEnvRoot = Join-Path $localToolsPath "python_env"
    if (!(Test-Path -LiteralPath $pythonEnvRoot)) {
        return $null
    }

    $environment = Get-ChildItem -LiteralPath $pythonEnvRoot -Directory |
        Where-Object { $_.Name -like "idf5.4_py*_env" } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (!$environment) {
        return $null
    }

    return Join-Path $pythonEnvRoot $environment.Name
}

function Import-Y700IdfEnvironment {
    param([string]$IdfPath)

    $resolvedIdfPath = Resolve-Y700IdfPath -RequestedPath $IdfPath
    $shortRepoRoot = Get-Y700ShortRepoRoot
    $shortLocalIdf = Join-Path $shortRepoRoot $script:Y700LocalIdfRelativePath
    $usingLocalIdf = [IO.Path]::GetFullPath($resolvedIdfPath).Equals(
        [IO.Path]::GetFullPath($shortLocalIdf),
        [StringComparison]::OrdinalIgnoreCase)

    if ($usingLocalIdf) {
        & (Join-Path $PSScriptRoot "patch_idf_short_paths.ps1") -IdfPath $resolvedIdfPath

        $env:IDF_TOOLS_PATH = Join-Path $shortRepoRoot $script:Y700LocalToolsRelativePath
        $localPythonEnv = Get-Y700LocalPythonEnvironment
        if (!$localPythonEnv) {
            throw "The project-local ESP-IDF Python environment is missing. Run tools\setup_dev_environment.ps1."
        }
        $env:IDF_PYTHON_ENV_PATH = $localPythonEnv
    }

    $alreadyActive = $env:Y700_IDF_ACTIVE_PATH -and
        [IO.Path]::GetFullPath($env:Y700_IDF_ACTIVE_PATH).Equals(
            [IO.Path]::GetFullPath($resolvedIdfPath),
            [StringComparison]::OrdinalIgnoreCase)

    $exportScript = Join-Path $resolvedIdfPath "export.ps1"
    if (!$alreadyActive) {
        . $exportScript
        $env:Y700_IDF_ACTIVE_PATH = $resolvedIdfPath
    }

    if ($usingLocalIdf) {
        $env:IDF_PATH = $resolvedIdfPath
        $env:Y700_IDF_ENTRYPOINT = Join-Path $resolvedIdfPath "tools\idf.py"
        $env:Y700_IDF_WRAPPER = Join-Path $PSScriptRoot "idf_short_path_wrapper.py"
        $env:Y700_IDF_PYTHON = Join-Path $env:IDF_PYTHON_ENV_PATH "Scripts\python.exe"

        function global:Invoke-Y700Idf {
            & $env:Y700_IDF_PYTHON $env:Y700_IDF_WRAPPER @args
        }
        Set-Alias -Name "idf.py" -Value "Invoke-Y700Idf" -Scope Global
    }

    $idfCommand = Get-Command idf.py -ErrorAction SilentlyContinue
    if (!$idfCommand) {
        throw "idf.py was not added to PATH by $exportScript."
    }

    $version = (& idf.py --version | Out-String).Trim()
    if ($version -notmatch "v?5\.4\.2") {
        throw "Expected ESP-IDF 5.4.2, but found '$version' at $resolvedIdfPath."
    }

    Write-Host "[Y700_ENV] repo=$shortRepoRoot"
    Write-Host "[Y700_ENV] idf=$resolvedIdfPath"
    Write-Host "[Y700_ENV] tools=$env:IDF_TOOLS_PATH"
    Write-Host "[Y700_ENV] python=$env:IDF_PYTHON_ENV_PATH"
}

function Get-Y700IdfPython {
    if ($env:IDF_PYTHON_ENV_PATH) {
        $candidate = Join-Path $env:IDF_PYTHON_ENV_PATH "Scripts\python.exe"
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    $localPythonEnv = Get-Y700LocalPythonEnvironment
    if ($localPythonEnv) {
        $candidate = Join-Path $localPythonEnv "Scripts\python.exe"
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    $localPythonPath = Join-Path (Get-Y700ShortRepoRoot) $script:Y700LocalPythonRelativePath
    if (Test-Path -LiteralPath $localPythonPath) {
        return $localPythonPath
    }

    throw "The ESP-IDF Python executable was not found. Run tools\setup_dev_environment.ps1."
}
