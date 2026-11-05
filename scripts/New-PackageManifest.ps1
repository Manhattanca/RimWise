#requires -Version 5.1
param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$PreviousReleasesDirectory
)
$ErrorActionPreference = 'Stop'

function Test-ProgramPath([string]$Path) {
    if ($Path.Length -gt 240 -or $Path.Contains('\') -or $Path.Contains(':')) { return $false }
    foreach ($part in $Path.Split('/')) {
        if (!$part -or $part -eq '.' -or $part -eq '..' -or $part.EndsWith('.') -or $part.EndsWith(' ') -or
            $part.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0) { return $false }
    }
    return $Path -match '^[^/]+\.(dll|exe|deps\.json|runtimeconfig\.json|md)$' -or
        $Path -cmatch '^(SteamBridge|Decompiler|Diagnostics)/[^/]+\.(dll|exe|deps\.json|runtimeconfig\.json)$' -or
        $Path -cmatch '^Decompiler/runtimes/win/lib/net10\.0/System\.Diagnostics\.EventLog(\.Messages)?\.dll$' -or
        $Path -cmatch '^licenses/[^/]+\.txt$'
}

$root = [System.IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\', '/')
$files = @(Get-ChildItem -LiteralPath $root -File -Recurse | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
    if (Test-ProgramPath $relative) {
        [ordered]@{ Path = $relative; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    } elseif ($relative -notin @('RimWise.package.json', 'SHA256SUMS.txt')) {
        throw "Unclassified package resource: $relative"
    }
})
$current = @{}
foreach ($file in $files) { $current[$file.Path] = $true }
$retired = @{}
function Add-RetiredFile([string]$Path, [string]$Hash) {
    if ((Test-ProgramPath $Path) -and !$current.ContainsKey($Path) -and $Hash -match '^[a-fA-F0-9]{64}$') {
        $retired[$Path + ':' + $Hash] = [ordered]@{ Path = $Path; Sha256 = $Hash }
    }
}
# Only recorded distribution hashes authorize cleanup, never a scan of a player's installation.
if (Test-Path -LiteralPath $PreviousReleasesDirectory) {
    foreach ($release in (Get-ChildItem -LiteralPath $PreviousReleasesDirectory -Directory)) {
        # Carry the compatibility catalog forward even when only the last release is retained.
        $previousManifest = Join-Path $release.FullName 'RimWise.package.json'
        if (Test-Path -LiteralPath $previousManifest) {
            $previous = [System.IO.File]::ReadAllText($previousManifest) | ConvertFrom-Json
            if ($previous.Schema -ne 1 -or $previous.Product -ne 'RimWise') { throw 'Invalid previous package manifest.' }
            foreach ($file in @($previous.Files) + @($previous.Retired)) { Add-RetiredFile $file.Path $file.Sha256 }
        }
        $sums = Join-Path $release.FullName 'SHA256SUMS.txt'
        if (!(Test-Path -LiteralPath $sums)) { continue }
        foreach ($line in [System.IO.File]::ReadLines($sums)) {
            if ($line -match '^([a-fA-F0-9]{64})  (.+)$') {
                $hash = $Matches[1]; $relative = $Matches[2]
                Add-RetiredFile $relative $hash
            }
        }
    }
}
if ($files.Count -gt 4096 -or $retired.Count -gt 16384) { throw 'Package inventory exceeds runtime limits.' }
$manifest = [ordered]@{ Schema = 1; Product = 'RimWise'; Version = $Version;
    Files = $files; Retired = @($retired.Values | Sort-Object { $_.Path }, { $_.Sha256 }) }
$json = ConvertTo-Json -InputObject $manifest -Depth 5 -Compress
[System.IO.File]::WriteAllText((Join-Path $root 'RimWise.package.json'), $json, [System.Text.UTF8Encoding]::new($false))
