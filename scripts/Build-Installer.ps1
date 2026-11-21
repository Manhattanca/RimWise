#requires -Version 7.0
param(
    [string]$Version = '0.6.2',
    [Parameter(Mandatory)][string]$Compiler,
    [string]$RuntimeInstaller,
    [string]$PackageDirectory,
    [string]$OutputDirectory,
    [string]$TestIdentity,
    [switch]$ExternalPayload,
    [switch]$LegacyTestLayout
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
$current = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $PackageDirectory 'SHA256SUMS.txt')) {
    if ($line -notmatch '^([a-fA-F0-9]{64})  ([a-zA-Z0-9_./-]+)$') { throw 'Invalid hash entry.' }
    $expected = $Matches[1]; $relative = $Matches[2]
    $path = [IO.Path]::GetFullPath((Join-Path $PackageDirectory $relative))
    if (!$path.StartsWith($PackageDirectory + '\', [StringComparison]::OrdinalIgnoreCase) -or
        (Get-FileHash -LiteralPath $path).Hash -ne $expected) { throw 'Package verification failed.' }
    if ($relative.Split('/') | Where-Object { !$_ -or $_ -in '.', '..' -or $_.EndsWith('.') }) { throw 'Invalid package path.' }
    $current.Add($relative, $expected.ToLowerInvariant())
}
$current.Add('SHA256SUMS.txt', (Get-FileHash -LiteralPath (Join-Path $PackageDirectory 'SHA256SUMS.txt')).Hash.ToLowerInvariant())
if (@(Compare-Object @($current.Keys | Sort-Object) @(Get-ChildItem -LiteralPath $PackageDirectory -Recurse -File | ForEach-Object {
    [IO.Path]::GetRelativePath($PackageDirectory, $_.FullName).Replace('\','/')
} | Sort-Object)).Count) { throw 'Package has files outside the inventory.' }
if ($LegacyTestLayout -and (!$TestIdentity -or $ExternalPayload)) { throw 'Legacy layout is for isolated tests only.' }
if (!$ExternalPayload) {
$expectedRuntime = '337dacab1636219147b3fe75e451bf5b1c17bca2c86328266d40c4ad1ff0a1e8391c4746ca7a5b702b65b61e8bfe834566cfc1b548cc10aef90b441357b10b69'
if ((Get-FileHash -LiteralPath $RuntimeInstaller -Algorithm SHA512).Hash -ne $expectedRuntime) { throw 'Runtime installer hash mismatch.' }
$signature = Get-AuthenticodeSignature -LiteralPath $RuntimeInstaller
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { throw 'Runtime installer signature mismatch.' }
}
if (Test-Path -LiteralPath (Join-Path $OutputDirectory "RimWise-v$Version-Setup.exe")) { throw 'Installer already exists; no overwrite.' }
$work = Join-Path $root ('work\installer-build\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work,$OutputDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'packaging\RimWise.iss') -Destination (Join-Path $work 'RimWise.iss')
$retired = [Collections.Generic.List[string]]::new()
$emptyDirs = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$allowed = @{}
foreach ($name in $current.Keys) { $allowed[$name] = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase); $null = $allowed[$name].Add($current[$name]) }
foreach ($previous in Get-ChildItem -LiteralPath $releases -Directory) {
    if ($previous.Name -notmatch '^v(\d+\.\d+\.\d+)$') { continue }
    $previousVersion = $Matches[1]
    if ([version]$previousVersion -gt [version]$Version) { continue }
    $sums = Join-Path $previous.FullName 'SHA256SUMS.txt'
    if (!(Test-Path -LiteralPath $sums)) { continue }
    $entries = @(Get-Content -LiteralPath $sums) + @((Get-FileHash -LiteralPath $sums).Hash + '  SHA256SUMS.txt')
    foreach ($entry in $entries) {
        if ($entry -notmatch '^([a-fA-F0-9]{64})  ([a-zA-Z0-9_./-]+)$') { throw 'Unsafe retired package entry.' }
        $hash = $Matches[1]; $relative = $Matches[2]
        if ([IO.Path]::IsPathRooted($relative) -or ($relative.Split('/') | Where-Object { !$_ -or $_ -in '.', '..' -or $_.EndsWith('.') })) { throw 'Unsafe retired path.' }
        if ($allowed.ContainsKey($relative)) { $null = $allowed[$relative].Add($hash.ToLowerInvariant()) }
        $local = $relative.Replace('/','\')
        if ([version]$previousVersion -ge [version]'0.5.0' -and [version]$previousVersion -le [version]'0.6.4') {
            $old = "versions\$previousVersion\$local"
            $retired.Add("RemoveKnownOldFile('$old', '$hash');")
            $parent = [IO.Path]::GetDirectoryName($old)
            while ($parent) { $null = $emptyDirs.Add($parent); $parent = [IO.Path]::GetDirectoryName($parent) }
        }
        if (!$current.ContainsKey($relative)) { $retired.Add("RemoveKnownOldFile('$local', '$hash');") }
    }
}
foreach ($directory in ($emptyDirs | Sort-Object Length -Descending)) { $retired.Add("RemoveKnownEmptyDir('$directory');") }
[IO.File]::WriteAllLines((Join-Path $work 'RetiredInstallerFiles.iss'), $retired)
$checks = foreach ($name in ($current.Keys | Sort-Object)) {
    $local = $name.Replace('/','\')
    "CheckPayloadFile('$local', '|$($allowed[$name] -join '|')|', '$($current[$name])');"
}
[IO.File]::WriteAllLines((Join-Path $work 'CheckPayload.iss'), [string[]]$checks)
if ($ExternalPayload) {
    $external = foreach ($name in ($current.Keys | Sort-Object)) {
        $local = $name.Replace('/','\')
        $parent = [IO.Path]::GetDirectoryName($local)
        $destination = if ($parent) { '{app}\' + $parent } else { '{app}' }
        'Source: "{{src}}\payload\{0}"; DestDir: "{1}"; Flags: external ignoreversion; ExternalSize: {2}' -f $local,$destination,(Get-Item -LiteralPath (Join-Path $PackageDirectory $local)).Length
    }
    [IO.File]::WriteAllLines((Join-Path $work 'ExternalFiles.iss'), [string[]]$external)
}
$chinese = Join-Path $root 'packaging\ChineseSimplified.isl'
if (!(Test-Path -LiteralPath $chinese)) { throw 'Compiler ChineseSimplified.isl is missing.' }
$arguments = @("/DAppVersion=$Version", "/DPackageDirectory=$PackageDirectory", "/DOutputDirectory=$OutputDirectory",
    "/DRuntimeInstaller=$RuntimeInstaller", "/DChineseMessages=$chinese", "/DLicensePath=$(Join-Path $root 'LICENSE')",
    "/DIconPath=$(Join-Path $root 'src\RimWise.Desktop\Resources\Branding\RimWise.ico')")
if ($TestIdentity) {
    if ($TestIdentity -notmatch '^[a-zA-Z0-9-]+$') { throw 'Invalid test identity.' }
    $arguments += @("/DAppIdValue=RimWise.InstallerTest.$TestIdentity", "/DAppNameValue=RimWise Installer Test $TestIdentity",
        "/DActiveMutexValue=Local\RimWise.InstallerTest.$TestIdentity.Active")
}
if ($ExternalPayload) { $arguments += '/DExternalPayload=1' }
if ($LegacyTestLayout) { $arguments += '/DLegacyTestLayout=1' }
& $Compiler @arguments (Join-Path $work 'RimWise.iss')
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed: $LASTEXITCODE" }
Get-FileHash -LiteralPath (Join-Path $OutputDirectory "RimWise-v$Version-Setup.exe") -Algorithm SHA256
