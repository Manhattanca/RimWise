#requires -Version 7.0
param([string]$OutputDirectory, [string]$RunnerPath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Initialize-Environment.ps1') -PackageCacheRoot (Join-Path $PSScriptRoot '..\work\nuget')
if (!$OutputDirectory) { $OutputDirectory = Join-Path $script:WorkRoot ('marketplace-catalog-checks\中文 [路径] &\' + [Guid]::NewGuid().ToString('N')) }
$runner = if ($RunnerPath) { $RunnerPath } else { Join-Path $script:WorkRoot 'bin\RimWise.MarketplaceCatalog\Debug\net10.0\RimWise.MarketplaceCatalog.exe' }
$baseline = Join-Path $OutputDirectory 'base'
$candidate = Join-Path $OutputDirectory 'candidate'
$relative = 'marketplace/manifests/exampleauthor/test-collection/1.0.0.json'
[IO.Directory]::CreateDirectory((Join-Path $baseline 'marketplace')) | Out-Null
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName((Join-Path $candidate $relative))) | Out-Null
$manifest = [ordered]@{ schemaVersion = 1; id = 'test-collection'; version = '1.0.0'; title = '合成排序'; author = 'ExampleAuthor'; description = '合成校验数据，无真实模组订阅。'; gameVersion = '1.7'; requiredDlc = @(); workshopCollectionId = $null; mods = @(@{packageId='ludeon.rimworld';workshopId=$null}) }
$bytes = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Depth 8))
$file = Join-Path $candidate $relative
[IO.File]::WriteAllBytes($file, $bytes)
$changed = Join-Path $OutputDirectory 'changed-paths.bin'
[IO.File]::WriteAllText($changed, $relative + [char]0)
$env:MARKETPLACE_CHANGED_PATHS_FILE = $changed
$env:MARKETPLACE_AUTHOR = 'ExampleAuthor'
$checks = [Collections.Generic.List[string]]::new()
try {
    & $runner validate-submission $baseline $candidate
    if ($LASTEXITCODE -ne 0) { throw 'Valid new author submission rejected.' }
    $checks.Add('New manifest accepted with trusted matching author')
    $env:MARKETPLACE_AUTHOR = 'OtherAuthor'
    & $runner validate-submission $baseline $candidate
    if ($LASTEXITCODE -eq 0) { throw 'Impersonation accepted.' }
    $checks.Add('Different PR author rejected')
    $env:MARKETPLACE_AUTHOR = 'ExampleAuthor'
    [IO.File]::WriteAllText($changed, $relative + [char]0 + 'src/changed.cs' + [char]0)
    & $runner validate-submission $baseline $candidate
    if ($LASTEXITCODE -eq 0) { throw 'Non-manifest modification accepted.' }
    $checks.Add('Source or workflow changes rejected in marketplace submissions')
    [IO.File]::WriteAllText($changed, $relative + [char]0)
    $otherRelative = 'marketplace/manifests/anotherauthor/test-collection/1.0.0.json'
    $otherManifest = [ordered]@{}
    foreach ($key in $manifest.Keys) { $otherManifest[$key] = $manifest[$key] }
    $otherManifest.author = 'AnotherAuthor'
    $otherFile = Join-Path $baseline $otherRelative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($otherFile)) | Out-Null
    [IO.File]::WriteAllBytes($otherFile, [Text.Encoding]::UTF8.GetBytes(($otherManifest | ConvertTo-Json -Depth 8)))
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName((Join-Path $candidate $otherRelative))) | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $candidate $otherRelative), [IO.File]::ReadAllBytes($otherFile))
    & $runner validate-submission $baseline $candidate
    if ($LASTEXITCODE -eq 0) { throw 'Sorting identifier collision across authors accepted.' }
    $checks.Add('A sorting identifier already owned by another author is rejected')
    [IO.File]::Delete((Join-Path $candidate $otherRelative))
    [IO.File]::Delete($otherFile)
    $otherManifest.id = 'another-order'
    $otherRelative = 'marketplace/manifests/anotherauthor/another-order/1.0.0.json'
    $otherFile = Join-Path $baseline $otherRelative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($otherFile)) | Out-Null
    [IO.File]::WriteAllBytes($otherFile, [Text.Encoding]::UTF8.GetBytes(($otherManifest | ConvertTo-Json -Depth 8)))
    & $runner build $candidate
    if ($LASTEXITCODE -ne 0) { throw 'Catalogue build failed.' }
    $index = [IO.File]::ReadAllText((Join-Path $candidate 'marketplace/index.json')) | ConvertFrom-Json
    if ($index.entries.Count -ne 1 -or $index.entries[0].sha256 -cne (Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant()) { throw 'Exact byte SHA mismatch.' }
    $checks.Add('Index SHA covers exact original JSON including whitespace')
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName((Join-Path $baseline $relative))) | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $baseline $relative), $bytes)
    [IO.File]::AppendAllText($file, ' ')
    & $runner validate-submission $baseline $candidate
    if ($LASTEXITCODE -eq 0) { throw 'Existing version overwrite accepted.' }
    $checks.Add('Already published version immutable even for whitespace changes')
    # Removal is a separate author-only request covering every version of one order.
    [IO.File]::WriteAllBytes($file, $bytes)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName((Join-Path $candidate $otherRelative))) | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $candidate $otherRelative), [IO.File]::ReadAllBytes($otherFile))
    [IO.File]::Delete($file)
    & $runner validate-submission $baseline $candidate
    if ($LASTEXITCODE -ne 0) { throw 'Author removal of all versions rejected.' }
    $checks.Add('Author can request removal without touching another author order')
    $env:MARKETPLACE_AUTHOR = 'OtherAuthor'
    & $runner validate-submission $baseline $candidate
    if ($LASTEXITCODE -eq 0) { throw 'Other author removal accepted.' }
    $checks.Add('Removal of another author order rejected')
    $env:MARKETPLACE_AUTHOR = 'ExampleAuthor'
    $later = 'marketplace/manifests/exampleauthor/test-collection/1.0.1.json'
    $manifest.version = '1.0.1'
    $laterBytes = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Depth 8))
    [IO.File]::WriteAllBytes((Join-Path $baseline $later), $laterBytes)
    [IO.File]::WriteAllBytes((Join-Path $candidate $later), $laterBytes)
    & $runner validate-submission $baseline $candidate
    if ($LASTEXITCODE -eq 0) { throw 'Partial version removal accepted.' }
    $checks.Add('Partial removal rejected so an old version cannot reappear')
    [IO.File]::Delete((Join-Path $candidate $later))
    [IO.File]::WriteAllText($changed, $relative + [char]0 + $later + [char]0)
    & $runner validate-submission $baseline $candidate
    if ($LASTEXITCODE -ne 0) { throw 'Full multi-version removal rejected.' }
    $checks.Add('Removal of all versions accepted as one pending review')
    [IO.File]::WriteAllText($changed, $relative + [char]0 + $later + [char]0 + '.github/workflows/marketplace.yml' + [char]0)
    & $runner validate-submission $baseline $candidate
    if ($LASTEXITCODE -eq 0) { throw 'Workflow edit hidden in removal accepted.' }
    $checks.Add('Removal cannot smuggle a workflow edit')
    [IO.File]::WriteAllText($changed, $relative + [char]0 + $later + [char]0)
    & $runner build $candidate
    if ($LASTEXITCODE -ne 0) { throw 'Removal catalogue build failed.' }
    $removedIndex = [IO.File]::ReadAllText((Join-Path $candidate 'marketplace/index.json')) | ConvertFrom-Json
    if ($removedIndex.entries.Count -ne 1 -or $removedIndex.entries[0].author -cne 'AnotherAuthor') { throw 'Removed order leaked into catalogue.' }
    $checks.Add('Rebuilt index excludes removed order and retains unrelated order')
    [IO.File]::WriteAllText((Join-Path $candidate 'marketplace/manifests/payload.exe'), 'synthetic non-executable text')
    & $runner build $candidate
    if ($LASTEXITCODE -eq 0) { throw 'Unexpected attachment accepted.' }
    $checks.Add('Non-JSON attachment rejected')
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'result.json'), (@{ Passed = $true; Checks = $checks.ToArray() } | ConvertTo-Json -Depth 5))
    Write-Output "Passed: $($checks.Count); results: $OutputDirectory"
}
finally {
    Remove-Item Env:MARKETPLACE_CHANGED_PATHS_FILE -ErrorAction SilentlyContinue
    Remove-Item Env:MARKETPLACE_AUTHOR -ErrorAction SilentlyContinue
}
