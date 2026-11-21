#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$FromVersion,
    [Parameter(Mandatory)][string]$ToVersion,
    [Parameter(Mandatory)][string]$Compiler,
    [string]$ReleasesDirectory = (Join-Path $PSScriptRoot '..\..\releases'),
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$stable = '\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\z'
if ($FromVersion -notmatch $stable -or $ToVersion -notmatch $stable) { throw 'Invalid versions.' }
$from = [version]$FromVersion
$to = [version]$ToVersion
if ($to.Major -ne $from.Major -or $to.Minor -ne $from.Minor -or ($to.Build - $from.Build) -ne 1) {
    throw 'Delta packages require consecutive patch versions of the same major and minor release.'
}
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$ReleasesDirectory = [IO.Path]::GetFullPath($ReleasesDirectory)
if (!$OutputDirectory) { $OutputDirectory = $ReleasesDirectory }
$name = "RimWise-v$FromVersion-to-v$ToVersion-Delta.zip"
$output = Join-Path $OutputDirectory $name
if (Test-Path -LiteralPath $output) { throw 'Existing release asset will not be overwritten.' }
$work = Join-Path $root ('work\delta-build\' + [guid]::NewGuid().ToString('N'))
$target = Join-Path $ReleasesDirectory "v$ToVersion"
$base = Join-Path $ReleasesDirectory "v$FromVersion"
& (Join-Path $PSScriptRoot 'Build-Installer.ps1') -Version $ToVersion -Compiler $Compiler -PackageDirectory $target `
    -ExternalPayload -OutputDirectory $work
. (Join-Path $PSScriptRoot 'Initialize-Environment.ps1')
$candidate = Join-Path $work $name
Invoke-ProjectDotnet -Arguments @('run','--project',(Join-Path $root 'tools\RimWise.DeltaBuilder\RimWise.DeltaBuilder.csproj'),
    '--configuration','Release','--',$FromVersion,$ToVersion,$base,$target,
    (Join-Path $work "RimWise-v$ToVersion-Setup.exe"),$candidate)
$zip = Get-Item -LiteralPath (Join-Path $ReleasesDirectory "RimWise-v$ToVersion.zip")
$exe = Get-Item -LiteralPath (Join-Path $ReleasesDirectory "RimWise-v$ToVersion-Setup.exe")
if ((Get-Item -LiteralPath $candidate).Length -ge [Math]::Min($zip.Length, $exe.Length)) {
    Write-Warning 'The verified delta is not smaller than the full release assets; do not publish it.'
    return
}
[IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($OutputDirectory)) | Out-Null
[IO.File]::Copy($candidate, $output, $false)
Get-FileHash -LiteralPath $output
