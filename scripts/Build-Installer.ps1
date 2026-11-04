#requires -Version 7.0
param(
    [string]$Version = '0.5.1',
    [Parameter(Mandatory)][string]$Compiler,
    [Parameter(Mandatory)][string]$RuntimeInstaller,
    [string]$PackageDirectory,
    [string]$OutputDirectory,
    [string]$TestIdentity
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releases = [IO.Path]::GetFullPath((Join-Path $root '..\releases'))
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid version.' }
if (!$PackageDirectory) { $PackageDirectory = Join-Path $releases "v$Version" }
if (!$OutputDirectory) { $OutputDirectory = $releases }
$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$manifest = Get-Content -LiteralPath (Join-Path $PackageDirectory 'RimWise.package.json') -Raw | ConvertFrom-Json
if ($manifest.Product -ne 'RimWise' -or $manifest.Version -ne $Version) { throw 'Package identity mismatch.' }
foreach ($line in Get-Content -LiteralPath (Join-Path $PackageDirectory 'SHA256SUMS.txt')) {
    if ($line -notmatch '^([a-fA-F0-9]{64})  (.+)$') { throw 'Invalid hash entry.' }
    $expected = $Matches[1]; $relative = $Matches[2]
    $path = [IO.Path]::GetFullPath((Join-Path $PackageDirectory $relative))
    if (!$path.StartsWith($PackageDirectory + '\', [StringComparison]::OrdinalIgnoreCase) -or
        (Get-FileHash -LiteralPath $path).Hash -ne $expected) { throw 'Package verification failed.' }
}
$expectedRuntime = '337dacab1636219147b3fe75e451bf5b1c17bca2c86328266d40c4ad1ff0a1e8391c4746ca7a5b702b65b61e8bfe834566cfc1b548cc10aef90b441357b10b69'
if ((Get-FileHash -LiteralPath $RuntimeInstaller -Algorithm SHA512).Hash -ne $expectedRuntime) { throw 'Runtime installer hash mismatch.' }
$signature = Get-AuthenticodeSignature -LiteralPath $RuntimeInstaller
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { throw 'Runtime installer signature mismatch.' }
if (Test-Path -LiteralPath (Join-Path $OutputDirectory "RimWise-v$Version-Setup.exe")) { throw 'Installer already exists; no overwrite.' }
$work = Join-Path $root ('work\installer-build\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work,$OutputDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'packaging\RimWise.iss') -Destination (Join-Path $work 'RimWise.iss')
$retired = [Collections.Generic.List[string]]::new()
# Only versioned installations introduced with 0.5.0 are eligible for cleanup.
foreach ($previous in Get-ChildItem -LiteralPath $releases -Directory) {
    if ($previous.Name -notmatch '^v(\d+\.\d+\.\d+)$') { continue }
    $previousVersion = $Matches[1]
    if ([version]$previousVersion -lt [version]'0.5.0' -or [version]$previousVersion -ge [version]$Version) { continue }
    $sums = Join-Path $previous.FullName 'SHA256SUMS.txt'
    if (!(Test-Path -LiteralPath $sums)) { continue }
    $entries = @(Get-Content -LiteralPath $sums) + @((Get-FileHash -LiteralPath $sums).Hash + '  SHA256SUMS.txt')
    foreach ($entry in $entries) {
        if ($entry -notmatch '^([a-fA-F0-9]{64})  ([a-zA-Z0-9_./-]+)$') { throw 'Unsafe retired package entry.' }
        $hash = $Matches[1]; $relative = $Matches[2]
        if ([IO.Path]::IsPathRooted($relative) -or $relative.Split('/') -contains '..') { throw 'Unsafe retired path.' }
        $retired.Add("RemoveKnownOldFile('versions\$previousVersion\$($relative.Replace('/','\'))', '$hash');")
    }
}
[IO.File]::WriteAllLines((Join-Path $work 'RetiredInstallerFiles.iss'), $retired)
$chinese = Join-Path $root 'packaging\ChineseSimplified.isl'
if (!(Test-Path -LiteralPath $chinese)) { throw 'Compiler ChineseSimplified.isl is missing.' }
$arguments = @("/DAppVersion=$Version", "/DPackageDirectory=$PackageDirectory", "/DOutputDirectory=$OutputDirectory",
    "/DRuntimeInstaller=$RuntimeInstaller", "/DChineseMessages=$chinese", "/DLicensePath=$(Join-Path $root 'LICENSE')",
    "/DIconPath=$(Join-Path $root 'src\RimWise.Desktop\Resources\Branding\RimWise.ico')")
if ($TestIdentity) {
    if ($TestIdentity -notmatch '^[a-zA-Z0-9-]+$') { throw 'Invalid test identity.' }
    $arguments += @("/DAppIdValue=RimWise.InstallerTest.$TestIdentity", "/DAppNameValue=RimWise Installer Test $TestIdentity")
}
& $Compiler @arguments (Join-Path $work 'RimWise.iss')
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed: $LASTEXITCODE" }
Get-FileHash -LiteralPath (Join-Path $OutputDirectory "RimWise-v$Version-Setup.exe") -Algorithm SHA256
