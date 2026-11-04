#requires -Version 5.1
param([string]$PackageCacheRoot = (Join-Path $PSScriptRoot '..\work\nuget'))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Initialize-Environment.ps1') -PackageCacheRoot $PackageCacheRoot
Push-Location -LiteralPath $script:ProjectRoot
try {
    # Public source exports omit vendor binaries; retrieve only pinned upstream files.
    if (!(Test-Path -LiteralPath 'src/RimWise.SteamBridge/Vendor/Steamworks.NET.dll') -or
        !(Test-Path -LiteralPath 'src/RimWise.SteamBridge/Vendor/steam_api64.dll')) {
        & (Join-Path $PSScriptRoot 'Fetch-Steamworks.ps1') -PackageCacheRoot $PackageCacheRoot
    }
    Invoke-ProjectDotnet -Arguments @('restore', 'src/RimWise.Desktop/RimWise.Desktop.csproj', '--source', 'https://api.nuget.org/v3/index.json', '--disable-parallel')
    Invoke-ProjectDotnet -Arguments @('build', 'src/RimWise.Desktop/RimWise.Desktop.csproj', '--no-restore', '-c', 'Release', '--disable-build-servers', '-m:1')
    Invoke-ProjectDotnet -Arguments @('restore', 'tests/RimWise.SteamChecks/RimWise.SteamChecks.csproj', '--source', 'https://api.nuget.org/v3/index.json', '--disable-parallel')
    Invoke-ProjectDotnet -Arguments @('build', 'tests/RimWise.SteamChecks/RimWise.SteamChecks.csproj', '--no-restore', '-c', 'Release', '--disable-build-servers', '-m:1')
    # Process lifecycle checks relaunch this runner; use its apphost, not dotnet.exe.
    & (Join-Path $script:WorkRoot 'bin/RimWise.SteamChecks/Release/net10.0/RimWise.SteamChecks.exe')
    if ($LASTEXITCODE -ne 0) { throw "Source regression checks failed with exit code $LASTEXITCODE" }
}
finally { Pop-Location }
