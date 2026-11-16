#requires -Version 7.0
param([switch]$SkipBuild, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Initialize-Environment.ps1') -PackageCacheRoot (Join-Path $PSScriptRoot '..\work\nuget')
if (!$OutputDirectory) { $OutputDirectory = Join-Path $script:WorkRoot ('marketplace-checks\中文 路径 [测试] &\' + [Guid]::NewGuid().ToString('N')) }
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
if (!$SkipBuild) {
    Invoke-ProjectDotnet -Arguments @('build', (Join-Path $script:ProjectRoot 'src\RimWise.Desktop\RimWise.Desktop.csproj'), '-c', 'Debug', '--disable-build-servers', '-m:1', '-v:minimal')
    Invoke-ProjectDotnet -Arguments @('build', (Join-Path $script:ProjectRoot 'tests\RimWise.MarketplaceChecks\RimWise.MarketplaceChecks.csproj'), '-c', 'Debug', '--disable-build-servers', '-m:1', '-v:minimal')
}
$backend = Join-Path $script:WorkRoot 'bin\RimWise.MarketplaceChecks\Debug\net10.0\RimWise.MarketplaceChecks.exe'
& $backend (Join-Path $OutputDirectory 'backend')
if ($LASTEXITCODE -ne 0) { throw 'Marketplace transport and validation checks failed.' }
$application = Join-Path $script:WorkRoot 'bin\RimWise.Desktop\Debug\net10.0-windows\RimWise.exe'
$info = [Diagnostics.ProcessStartInfo]::new()
$info.FileName = $application
$info.WorkingDirectory = $script:ProjectRoot
$info.UseShellExecute = $false
$info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$info.ArgumentList.Add('--marketplace-smoke')
$info.ArgumentList.Add((Join-Path $OutputDirectory 'ui'))
$process = [Diagnostics.Process]::Start($info)
try {
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(3)
    while (!$process.WaitForExit(5000)) {
        if ([DateTimeOffset]::UtcNow -ge $deadline) { $process.Kill(); throw 'Marketplace UI check timed out.' }
    }
    if ($process.ExitCode -ne 0) {
        $failure = Join-Path $OutputDirectory 'ui\failure.txt'
        if (Test-Path -LiteralPath $failure) { Get-Content -LiteralPath $failure }
        throw 'Marketplace UI checks failed.'
    }
    $result = [IO.File]::ReadAllText((Join-Path $OutputDirectory 'ui\result.json')) | ConvertFrom-Json
    Write-Output ('UI passed: ' + $result.Passed + '; checks: ' + $result.Checks.Count)
}
finally { $process.Dispose() }
Write-Output "Results: $OutputDirectory"
