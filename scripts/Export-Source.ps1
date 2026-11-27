#requires -Version 7.0
param([string]$Version = '0.6.2', [switch]$SkipArchive)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid version.' }
$target = Join-Path $root ("work\source-export\" + [Guid]::NewGuid().ToString('N') + "\RimWise-v$Version-source")
$archive = [IO.Path]::GetFullPath((Join-Path $root "..\releases\RimWise-v$Version-source.zip"))
if ((Test-Path -LiteralPath $target) -or (!$SkipArchive -and (Test-Path -LiteralPath $archive))) { throw 'Source export already exists.' }
[IO.Directory]::CreateDirectory($target) | Out-Null
$files = [Collections.Generic.List[string]]::new()
foreach ($folder in @('src', 'tests\RimWise.SteamChecks', 'tests\RimWise.MarketplaceChecks', 'tests\RimWise.ImageCompressionChecks',
    'tools\RimWise.MarketplaceCatalog', 'tools\RimWise.MarketplaceReview', 'tools\RimWise.DeltaBuilder', 'marketplace', 'packaging\licenses')) {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root $folder) -File -Recurse) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Source file is a link.' }
        $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        if ($relative -match '(^|/)(bin|obj|work|Data|__pycache__)/') { continue }
        if ($file.Name -like '*_wpftmp.csproj') { continue }
        if ($file.Extension -in @('.dll','.exe','.pdb','.zip','.7z','.key','.pem','.pfx')) { continue }
        if ($relative.StartsWith('src/RimWise.Decompiler/Vendor/') -and $relative -notin @(
            'src/RimWise.Decompiler/Vendor/LICENSE',
            'src/RimWise.Decompiler/Vendor/Services/OwnedAssemblyResolver.cs')) { continue }
        $files.Add($relative)
    }
}
foreach ($name in @('LICENSE','Directory.Build.props','global.json','NuGet.Config',
    'tests/RimWise.Checks/SorterChecks.cs','tests/RimWise.Checks/OrderingCycleChecks.cs','tests/RimWise.Checks/TrialExclusionChecks.cs',
    'tests/Fixtures/JunctionFixture.cs',
    'scripts/Initialize-Environment.ps1','scripts/Build-Source.ps1','scripts/Fetch-Steamworks.ps1',
    'scripts/Build-Installer.ps1','scripts/Build-DeltaUpdate.ps1','scripts/Publish-Preview.ps1','scripts/New-PackageManifest.ps1','scripts/Test-Marketplace.ps1','scripts/Test-MarketplaceCatalog.ps1',
    'packaging/RimWise.iss','packaging/ChineseSimplified.isl',
    'packaging/README.preview.md','packaging/decompiler-runtime-files.txt',
    'docs/THIRD_PARTY.md','docs/MARKETPLACE.md')) { $files.Add($name) }
$records = foreach ($relative in ($files | Sort-Object)) {
    $original = Join-Path $root $relative
    $copy = Join-Path $target $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($copy)) | Out-Null
    Copy-Item -LiteralPath $original -Destination $copy
    if ($relative -in @('scripts/Initialize-Environment.ps1','scripts/Build-Source.ps1','scripts/Fetch-Steamworks.ps1')) {
        $content = [IO.File]::ReadAllText($copy).Replace("'E:\RimWise-research\nuget'", "(Join-Path `$PSScriptRoot '..\work\nuget')")
        $content = $content.Replace('Downloaded dependencies stay on the explicitly approved E: volume.', 'Dependencies use the selected package cache directory.')
        [IO.File]::WriteAllText($copy, $content)
    }
    [ordered]@{ Path = $relative; Sha256 = (Get-FileHash -LiteralPath $copy).Hash.ToLowerInvariant() }
}
Copy-Item -LiteralPath (Join-Path $root 'packaging/README.source.md') -Destination (Join-Path $target 'README.md')
$projects = @('Core','Formats','Integration','SteamBridge','Decompiler','Diagnostics','Desktop') | ForEach-Object {
    '  <Project Path="src/RimWise.' + $_ + '/RimWise.' + $_ + '.csproj" />'
}
[IO.File]::WriteAllLines((Join-Path $target 'RimWise.slnx'), @('<Solution>') + $projects + @(
    '  <Project Path="tests/RimWise.SteamChecks/RimWise.SteamChecks.csproj" />',
    '  <Project Path="tests/RimWise.MarketplaceChecks/RimWise.MarketplaceChecks.csproj" />',
    '  <Project Path="tests/RimWise.ImageCompressionChecks/RimWise.ImageCompressionChecks.csproj" />',
    '  <Project Path="tools/RimWise.MarketplaceReview/RimWise.MarketplaceReview.csproj" />',
    '  <Project Path="tools/RimWise.MarketplaceCatalog/RimWise.MarketplaceCatalog.csproj" />',
    '  <Project Path="tools/RimWise.DeltaBuilder/RimWise.DeltaBuilder.csproj" />','</Solution>'))
[IO.File]::WriteAllText((Join-Path $target '.gitignore'), "work/`nbin/`nobj/`n.vs/`n*.user`n*.key`n*.pem`n*.pfx`nsrc/RimWise.SteamBridge/Vendor/*.dll`n")
# Fail closed before any archive is created. Report paths only, never secret matches.
$privatePatterns = @('(?i)[A-Z]:[\\/]Users[\\/]18515(?:[\\/]|\b)',
    '(?i)[A-Z]:[\\/]rim(?:[\\/]|\b)', '(?i)[A-Z]:[\\/]uma(?:[\\/]|\b)',
    '(?i)sk-[a-z0-9_-]{24,}', '(?i)gh[pousr]_[a-z0-9]{30,}', 'github_pat_[a-zA-Z0-9_]{30,}',
    '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----')
foreach ($file in Get-ChildItem -LiteralPath $target -File -Recurse) {
    if ($file.Name -eq 'AGENTS.md') { throw 'Internal instructions entered the export.' }
    if ($file.Extension -in @('.png','.ico','.gz')) { continue }
    $content = [IO.File]::ReadAllText($file.FullName)
    foreach ($pattern in $privatePatterns) {
        if ($content -match $pattern) {
            throw ('Source privacy review required: ' + [IO.Path]::GetRelativePath($target, $file.FullName))
        }
    }
}
$manifest = [ordered]@{
    Version = $Version
    Purpose = 'MIT source snapshot; third-party components retain their own licenses'
    Excluded = @('Personal data and credentials','Logs and diagnostic archives','Build outputs and caches',
        'Historical quarantined test runner and antivirus investigation samples',
        'Unused upstream decompiler tools and services','Internal case reports and historical scripts',
        'Local work instructions','Vendor binaries retrieved by the pinned build script')
    ExportedFiles = @($records)
}
[IO.File]::WriteAllText((Join-Path $target 'SOURCE-MANIFEST.json'), ($manifest | ConvertTo-Json -Depth 5))
$hashes = foreach ($file in Get-ChildItem -LiteralPath $target -File -Recurse | Sort-Object FullName) {
    (Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant() + '  ' + $file.FullName.Substring($target.Length + 1).Replace('\','/')
}
[IO.File]::WriteAllLines((Join-Path $target 'SHA256SUMS.txt'), [string[]]$hashes)
if ($SkipArchive) { Write-Output "Prepared local source: $target"; return }
$zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -LiteralPath $target -File -Recurse | Sort-Object FullName) {
        $relative = $file.FullName.Substring($target.Length + 1).Replace('\','/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName,
            "RimWise-v$Version-source/$relative", [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally { $zip.Dispose() }
Write-Output "Source: $target"
Write-Output "Archive: $archive"
