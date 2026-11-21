param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][string]$Commit,
    [Parameter(Mandatory)][string]$ManifestHash
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath (Join-Path $Root '.git')) {
    $revision = & git -C $Root rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $revision -ne $Commit) { throw "Expected BTstack commit $Commit." }
    & git -C $Root diff --quiet HEAD --
    if ($LASTEXITCODE -ne 0) { throw 'Tracked BTstack sources differ from the pinned commit.' }
    return
}
$manifest = Join-Path $Root 'BUILD-INPUTS.sha256'
if ((Get-FileHash -LiteralPath $manifest).Hash -ne $ManifestHash) { throw "Invalid build-input manifest for BTstack $Commit." }
foreach ($line in [IO.File]::ReadLines($manifest)) {
    $path = Join-Path $Root $line.Substring(66)
    if ((Get-FileHash -LiteralPath $path).Hash -ne $line.Substring(0,64)) {
        throw "Upstream file differs from BTstack $Commit at $path"
    }
}
