#requires -Version 5.1
param([string]$PackageCacheRoot = (Join-Path $PSScriptRoot '..\work\nuget'))
$ErrorActionPreference = 'Stop'
$script:ProjectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$script:WorkRoot = Join-Path $script:ProjectRoot 'work'
$environmentPaths = @{
    DOTNET_CLI_HOME = 'dotnet-home'
    NUGET_PACKAGES = 'nuget\packages'
    NUGET_HTTP_CACHE_PATH = 'nuget\http-cache'
    NUGET_PLUGINS_CACHE_PATH = 'nuget\plugin-cache'
    TEMP = 'temp'
    TMP = 'temp'
    APPDATA = 'appdata\roaming'
    LOCALAPPDATA = 'appdata\local'
}
foreach ($entry in $environmentPaths.GetEnumerator()) {
    # Dependencies use the selected package cache directory.
    $directory = if ($entry.Key.StartsWith('NUGET_')) {
        Join-Path $PackageCacheRoot ($entry.Value.Substring('nuget\'.Length))
    } else { Join-Path $script:WorkRoot $entry.Value }
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    [Environment]::SetEnvironmentVariable($entry.Key, $directory, 'Process')
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'
$env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = '1'
$env:MSBUILDDISABLENODEREUSE = '1'

function Invoke-ProjectDotnet {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
