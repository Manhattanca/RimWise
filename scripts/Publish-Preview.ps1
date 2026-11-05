#requires -Version 5.1
param([string]$Version = '0.5.5')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Initialize-Environment.ps1')

if ($Version -notmatch '^\d+\.\d+(?:\.\d+)?(?:-[a-zA-Z0-9]+(?:[.-][a-zA-Z0-9]+)*)?$') {
    throw 'Invalid version name.'
}
$releaseRoot = Join-Path ([System.IO.Path]::GetFullPath((Join-Path $script:ProjectRoot '..'))) 'releases'
$releaseDirectory = Join-Path $releaseRoot ('v' + $Version)
$archive = Join-Path $releaseRoot ('RimWise-v' + $Version + '.zip')
if ((Test-Path -LiteralPath $releaseDirectory) -or (Test-Path -LiteralPath $archive)) {
    throw 'Release already exists. Use a new version; existing releases are not overwritten.'
}
$staging = Join-Path $script:WorkRoot ('publish\' + [Guid]::NewGuid().ToString('N'))
Push-Location -LiteralPath $script:ProjectRoot
try {
    Invoke-ProjectDotnet -Arguments @('publish', 'src/RimWise.Desktop/RimWise.Desktop.csproj',
        '--configuration', 'Release', '--self-contained', 'false', '--output', $staging,
        '--disable-build-servers', '--nologo', '-v:minimal', '-m:1', '-p:DebugType=None', '-p:DebugSymbols=false')

    $readmePath = Join-Path $script:ProjectRoot 'packaging\README.preview.md'
    $readme = [System.IO.File]::ReadAllText($readmePath)
    foreach ($required in @('RimWise.exe', 'dotnet/runtime', 'dotnet/wpf',
        'mozillazg/pinyin-data', 'mozillazg/phrase-pinyin-data', 'chromium/chromium',
        'rlabrecque/Steamworks.NET', 'Valve Corporation', 'RimSort/Community-Rules-Database',
        'RimSort/Steam-Workshop-Database', 'CC0-1.0', 'pardeike/DecompilerServer',
        'icsharpcode/ILSpy', 'modelcontextprotocol/csharp-sdk', 'oleg-st/ZstdSharp', $Version.Split('-')[0])) {
        if (-not $readme.Contains($required)) { throw "README is missing: $required" }
    }
    if ($readme.Contains('{{')) { throw 'README still contains template placeholders.' }

    $published = @(Get-ChildItem -LiteralPath $staging -File)
    $builtVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $staging 'RimWise.dll')).ProductVersion.Split('+')[0]
    if ($builtVersion -ne $Version) { throw "Built version $builtVersion does not match package version $Version." }
    $runtimeNames = @('RimWise.exe', 'RimWise.dll', 'RimWise.deps.json', 'RimWise.runtimeconfig.json',
        'RimWise.Core.dll', 'RimWise.Formats.dll', 'RimWise.Integration.dll', 'ZstdSharp.dll')
    if (@(Compare-Object ($runtimeNames | Sort-Object) ($published.Name | Sort-Object)).Count -ne 0) {
        throw 'Application publish contents do not match the fixed runtime list.'
    }
    $bridgeDirectory = Join-Path $staging 'SteamBridge'
    $bridgeNames = @('RimWise.SteamBridge.exe', 'RimWise.SteamBridge.dll',
        'RimWise.SteamBridge.deps.json', 'RimWise.SteamBridge.runtimeconfig.json',
        'Steamworks.NET.dll', 'steam_api64.dll')
    $bridgeFiles = @(Get-ChildItem -LiteralPath $bridgeDirectory -File)
    if (@(Compare-Object @('Decompiler','Diagnostics','SteamBridge') @((Get-ChildItem -LiteralPath $staging -Directory).Name | Sort-Object)).Count -ne 0 -or
        @(Get-ChildItem -LiteralPath $bridgeDirectory -Directory).Count -ne 0 -or
        @(Compare-Object ($bridgeNames | Sort-Object) ($bridgeFiles.Name | Sort-Object)).Count -ne 0) {
        throw 'Steam bridge publish contents do not match the fixed runtime list.'
    }
    $decompilerDirectory = Join-Path $staging 'Decompiler'
    $diagnosticDirectory = Join-Path $staging 'Diagnostics'
    $diagnosticNames = @('RimWise.Diagnostics.exe','RimWise.Diagnostics.dll','RimWise.Diagnostics.deps.json',
        'RimWise.Diagnostics.runtimeconfig.json','RimWise.Core.dll','RimWise.Formats.dll','RimWise.Integration.dll','ZstdSharp.dll')
    if (@(Compare-Object ($diagnosticNames | Sort-Object) @((Get-ChildItem -LiteralPath $diagnosticDirectory -File).Name | Sort-Object)).Count -or
        @(Get-ChildItem -LiteralPath $diagnosticDirectory -Directory).Count) { throw 'Unexpected diagnostic tool files.' }
    $decompilerNames = [IO.File]::ReadAllLines((Join-Path $script:ProjectRoot 'packaging\decompiler-runtime-files.txt'))
    $decompilerFiles = @(Get-ChildItem -LiteralPath $decompilerDirectory -File -Recurse | ForEach-Object {
        $_.FullName.Substring($decompilerDirectory.Length + 1).Replace('\', '/')
    })
    if (@(Compare-Object ($decompilerNames | Sort-Object) ($decompilerFiles | Sort-Object)).Count -ne 0) {
        throw 'Local decompiler contents do not match the reviewed runtime list.'
    }
    [System.IO.Directory]::CreateDirectory($releaseDirectory) | Out-Null
    foreach ($file in $published) {
        $name = $file.Name
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $releaseDirectory $name)
    }
    Copy-Item -LiteralPath $bridgeDirectory -Destination (Join-Path $releaseDirectory 'SteamBridge') -Recurse
    Copy-Item -LiteralPath $decompilerDirectory -Destination (Join-Path $releaseDirectory 'Decompiler') -Recurse
    Copy-Item -LiteralPath $diagnosticDirectory -Destination (Join-Path $releaseDirectory 'Diagnostics') -Recurse
    Copy-Item -LiteralPath $readmePath -Destination (Join-Path $releaseDirectory 'README.md')
    # Acceptance records and recovery design notes belong in development/docs only.
    $licenseDirectory = Join-Path $script:ProjectRoot 'packaging\licenses'
    foreach ($requiredLicense in @('dotnet-runtime-LICENSE.txt', 'dotnet-THIRD-PARTY-NOTICES.txt',
        'pinyin-data-LICENSE.txt', 'phrase-pinyin-data-LICENSE.txt', 'Unicode-LICENSE.txt', 'chromium-LICENSE.txt',
        'Steamworks.NET-LICENSE.txt', 'Valve-Steamworks-NOTICE.txt',
        'RimSort-Community-Rules-Database-LICENSE.txt', 'RimSort-Steam-Workshop-Database-LICENSE.txt',
        'DecompilerServer-LICENSE.txt', 'ILSpy-LICENSE.txt', 'ILSpy-MIT-LICENSE.txt', 'ILSpy-THIRD-PARTY-NOTICES.txt',
        'ModelContextProtocol-LICENSE.txt', 'ModelContextProtocol-THIRD-PARTY-NOTICES.txt',
        'dotnet-AI-Abstractions-LICENSE.txt', 'dotnet-AI-Abstractions-THIRD-PARTY-NOTICES.txt',
        'dotnet-Extensions-LICENSE.txt', 'dotnet-Extensions-THIRD-PARTY-NOTICES.txt', 'ZstdSharp-LICENSE.txt', 'zstd-LICENSE.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $licenseDirectory $requiredLicense))) { throw 'License file missing.' }
    }
    Copy-Item -LiteralPath $licenseDirectory -Destination (Join-Path $releaseDirectory 'licenses') -Recurse
    Copy-Item -LiteralPath (Join-Path $script:ProjectRoot 'LICENSE') -Destination (Join-Path $releaseDirectory 'licenses\RimWise-LICENSE.txt')

    & (Join-Path $PSScriptRoot 'New-PackageManifest.ps1') -PackageDirectory $releaseDirectory -Version $Version -PreviousReleasesDirectory $releaseRoot

    $hashes = Get-ChildItem -LiteralPath $releaseDirectory -File -Recurse | Sort-Object FullName | ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $hash + '  ' + $_.FullName.Substring($releaseDirectory.Length + 1).Replace('\', '/')
    }
    [System.IO.File]::WriteAllLines((Join-Path $releaseDirectory 'SHA256SUMS.txt'), [string[]]$hashes)
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    # Use portable ZIP separators even under Windows PowerShell 5.1 / .NET Framework.
    $zip = [System.IO.Compression.ZipFile]::Open($archive, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in (Get-ChildItem -LiteralPath $releaseDirectory -File -Recurse | Sort-Object FullName)) {
            $entryName = $file.FullName.Substring($releaseDirectory.Length + 1).Replace('\', '/')
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $entryName) | Out-Null
        }
    }
    finally { $zip.Dispose() }
    Write-Output ('Executable: ' + (Join-Path $releaseDirectory 'RimWise.exe'))
    Write-Output ('Archive: ' + $archive)
}
finally { Pop-Location }
