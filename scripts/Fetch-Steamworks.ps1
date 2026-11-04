#requires -Version 5.1
param([string]$PackageCacheRoot = (Join-Path $PSScriptRoot '..\work\nuget'))
# Reproduce the exact bundled bridge dependencies; never reads a game installation.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Initialize-Environment.ps1') -PackageCacheRoot $PackageCacheRoot
$archive = Join-Path $PackageCacheRoot 'downloads\Steamworks.NET-Standalone_2025.164.1.zip'
[System.IO.Directory]::CreateDirectory((Split-Path -Parent $archive)) | Out-Null
if (!(Test-Path -LiteralPath $archive)) {
    $partial = $archive + '.partial'
    try {
        Invoke-WebRequest -Uri 'https://github.com/rlabrecque/Steamworks.NET/releases/download/2025.164.1/Steamworks.NET-Standalone_2025.164.1.zip' -OutFile $partial -UseBasicParsing
        Move-Item -LiteralPath $partial -Destination $archive
    }
    finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial } }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($archive)
try {
    $expected = @{
        'Steamworks.NET.dll' = '88542b6163d3bf1597e80ed626e38d9d8ceecf7a363892017dfe158b3cc0a7e2'
        'steam_api64.dll' = 'eb17909a76668cf9ae0b92a618a34a50f6c73d3a6787cb4dd8ce36a8b10bfb75'
    }
    foreach ($name in $expected.Keys) {
        $entry = $zip.GetEntry('Windows-x64/' + $name)
        if ($null -eq $entry) { throw 'Dependency archive member missing.' }
        $inputStream = $entry.Open()
        $memory = New-Object System.IO.MemoryStream
        try { $inputStream.CopyTo($memory); $bytes = $memory.ToArray() }
        finally { $inputStream.Dispose(); $memory.Dispose() }
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant() }
        finally { $sha.Dispose() }
        if ($hash -ne $expected[$name]) { throw ('Dependency SHA256 mismatch: ' + $name) }
        $vendor = Join-Path $script:ProjectRoot 'src\RimWise.SteamBridge\Vendor'
        [System.IO.Directory]::CreateDirectory($vendor) | Out-Null
        [System.IO.File]::WriteAllBytes((Join-Path $vendor $name), $bytes)
        Write-Output ('Verified ' + $name)
    }
}
finally { $zip.Dispose() }
