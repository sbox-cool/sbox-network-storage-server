<#
.SYNOPSIS
  Runs the real Network Storage library inside s&box (tools/sbox-cli-e2e, dedicated target) for every
  library in compat/library-targets.json against this checkout's build, and compares failures with a
  baseline sbox-ns binary (the latest release).

.DESCRIPTION
  Verdict per library:
    pass        the candidate (this build) passes.
    regression  the candidate fails and the baseline passes: this change breaks games that embed it.
    upstream    both fail: the engine or the library itself broke, not this change.
    known       both fail, and this build fails only checks the target lists in `knownFailures`: a
                documented defect of that immutable library build (it never worked on any server),
                reported but not counted as upstream. Any other failing check on this build keeps the
                normal verdict; extra failures on the older baseline only are not this build's problem.
    fail        the candidate fails and no baseline was given to tell the two apart.
  Writes compat-results.json, tested-libraries.json (the release.json `testedLibraries` field) and
  summary.md under -Out. Exits 1 on any regression or fail; upstream and known failures are reported only.
  The server must already be built (dotnet build src/SboxNetworkStorage.Server -c Release).
#>
param(
  [Parameter(Mandatory)] [string] $Engine,
  [Parameter(Mandatory)] [string] $Out,
  [string] $Baseline = '',
  [string] $Targets = 'compat/library-targets.json',
  # Comma-separated --library refs that replace the targets file (manual runs).
  [string] $Only = ''
)
$ErrorActionPreference = 'Stop'
Set-Location (Resolve-Path (Join-Path $PSScriptRoot '../..'))
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path $Out).Path
if ($Baseline -and -not (Test-Path $Baseline)) { throw "baseline binary $Baseline does not exist" }

$engineCommit = (git -C (Split-Path $Engine -Parent) rev-parse HEAD 2>$null)
$libraryRepo = 'https://github.com/sbox-cool/sbox-network-storage.git'

# r<revision> tags (sbox.game revisions content-matched to a commit by the library's revision sync)
# and main, each with the commit it points at, so the same build is never run twice.
$tagCommit = @{}
$mainCommit = $null
foreach ($line in @(git ls-remote $libraryRepo 'refs/tags/r*' 'refs/heads/main')) {
  $sha, $name = $line -split "`t"
  if ($name -eq 'refs/heads/main') { $mainCommit = $sha; continue }
  $peeled = $name.EndsWith('^{}')
  $tag = ($name -replace '^refs/tags/', '') -replace '\^\{\}$', ''
  if ($tag -match '^r\d+$' -and ($peeled -or -not $tagCommit.ContainsKey($tag))) { $tagCommit[$tag] = $sha }
}
$revisionTags = @($tagCommit.Keys | Sort-Object { [long]$_.Substring(1) } -Descending)

function Invoke-E2E([string] $Ref, [string] $Dir, [string[]] $Extra) {
  $runArgs = @('run', 'tools/sbox-cli-e2e/run.cs', '--', '--engine', $Engine, '--targets', 'dedicated',
    '--library', $Ref, '--no-build', '--startup-timeout', '600', '--out', $Dir) + $Extra
  & dotnet @runArgs | Out-Host
  $code = $LASTEXITCODE
  $file = Join-Path $Dir 'evidence.json'
  $evidence = if (Test-Path $file) { Get-Content $file -Raw | ConvertFrom-Json } else { $null }
  [pscustomobject]@{ Pass = ($code -eq 0); Evidence = $evidence }
}

$configured = if ($Only) {
  $Only.Split(',') | Where-Object { $_.Trim() } | ForEach-Object { [pscustomobject]@{ ref = $_.Trim(); label = $_.Trim() } }
} else {
  (Get-Content $Targets -Raw | ConvertFrom-Json).targets
}

# knownFailures: check ids (the part of an evidence failure before ':') that a pinned build is documented
# to fail on every server. Keyed by commit as well as ref, so the note applies whichever target entry
# (a pin or a latest:N expansion of the same commit) is the one that runs.
$knownFailures = @{}
foreach ($target in @($configured)) {
  if (-not $target.knownFailures) { continue }
  $ids = @($target.knownFailures | ForEach-Object { [string]$_ })
  $knownFailures[$target.ref] = $ids
  if ($target.ref -match '^git:([0-9a-f]{40})$') { $knownFailures[$Matches[1]] = $ids }
}
function Get-FailureIds($Run) {
  if (-not $Run -or -not $Run.Evidence) { return $null }
  , @($Run.Evidence.failures | ForEach-Object { ([string]$_ -split ':', 2)[0].Trim() })
}

# latest / latest:N expand to the newest N published revisions.
$list = foreach ($target in $configured) {
  if ($target.ref -match '^latest(?::(\d+))?$') {
    $count = if ($Matches[1]) { [int]$Matches[1] } else { 1 }
    $tags = @($revisionTags | Select-Object -First $count)
    if ($tags.Count -eq 0) { Write-Host "$($target.ref): no r<revision> release of the library yet; library main covers it"; continue }
    foreach ($tag in $tags) {
      [pscustomobject]@{ ref = $tag; label = "published $tag"; version = $null; revision = [long]$tag.Substring(1); commit = $tagCommit[$tag] }
    }
  } else {
    $commit = $null
    if ($target.ref -match '^git:([0-9a-f]{40})$') { $commit = $Matches[1] }
    elseif ($target.ref -eq 'main') { $commit = $mainCommit }
    elseif ($tagCommit.ContainsKey($target.ref)) { $commit = $tagCommit[$target.ref] }
    [pscustomobject]@{ ref = $target.ref; label = $target.label; version = $target.version; revision = $target.revision; commit = $commit }
  }
}

$results = [System.Collections.Generic.List[object]]::new()
$seen = @{}
foreach ($target in $list) {
  $ref = $target.ref
  $key = if ($target.commit) { $target.commit } else { $ref }
  if ($seen.ContainsKey($key)) { Write-Host "$($target.label) ($ref): same commit as $($seen[$key]), already run"; continue }
  $seen[$key] = $ref
  $slug = $ref -replace '[^A-Za-z0-9._-]', '_'
  Write-Host "::group::$($target.label) ($ref) against this build"
  # PowerShell names are case-insensitive: these must not reuse the $Baseline parameter's name.
  $thisRun = Invoke-E2E $ref (Join-Path $Out "$slug/candidate") @()
  Write-Host '::endgroup::'
  $releaseRun = $null
  if (-not $thisRun.Pass -and $Baseline) {
    Write-Host "::group::$($target.label) ($ref) against the baseline"
    $releaseRun = Invoke-E2E $ref (Join-Path $Out "$slug/baseline") @('--server-binary', $Baseline)
    Write-Host '::endgroup::'
  }
  $verdict = if ($thisRun.Pass) { 'pass' } elseif ($null -eq $releaseRun) { 'fail' } elseif ($releaseRun.Pass) { 'regression' } else { 'upstream' }
  $known = @(if ($target.commit -and $knownFailures.ContainsKey($target.commit)) { $knownFailures[$target.commit] } elseif ($knownFailures.ContainsKey($ref)) { $knownFailures[$ref] })
  if ($verdict -eq 'upstream' -and $known.Count -gt 0) {
    $candidateIds = Get-FailureIds $thisRun
    if ($null -ne $candidateIds -and $candidateIds.Count -gt 0 -and
        @($candidateIds | Where-Object { $_ -notin $known }).Count -eq 0) {
      $verdict = 'known'
    }
  }
  $library = $thisRun.Evidence.identity.library
  $version = if ($library.version) { $library.version } elseif ($target.version) { $target.version } else { 'legacy' }
  $revision = if ($library.revision) { [long]$library.revision } elseif ($target.revision) { [long]$target.revision } else { $null }
  $source = if ($ref -eq 'main' -and $library.commit) { "main@$($library.commit)" } else { $ref }
  $results.Add([pscustomobject][ordered]@{
      label     = $target.label
      ref       = $ref
      source    = $source
      library   = $version
      revision  = $revision
      commit    = $library.commit
      engine    = $engineCommit
      verdict   = $verdict
      candidate = $thisRun.Pass
      baseline  = if ($releaseRun) { $releaseRun.Pass } else { $null }
      failures  = if ($thisRun.Evidence) { @($thisRun.Evidence.failures) } else { @('no evidence.json: the runner stopped before writing it') }
    })
  switch ($verdict) {
    'regression' { Write-Host "::error::$($target.label) ($ref) passes on the baseline but fails on this build: this change breaks games that embed it." }
    'upstream' { Write-Host "::warning::$($target.label) ($ref) fails on this build and on the baseline: an engine or library problem, not this change." }
    'known' { Write-Host "::notice::$($target.label) ($ref) fails only its documented knownFailures, on this build and on the baseline." }
    'fail' { Write-Host "::error::$($target.label) ($ref) fails on this build (no baseline to compare)." }
  }
}

# testedLibraries for release.json: upstream and known failures say nothing about this server, so they are left out.
$tested = @($results | Where-Object { $_.verdict -notin 'upstream', 'known' } | ForEach-Object {
    [ordered]@{
      library  = $_.library
      revision = $_.revision
      source   = $_.source
      engine   = $_.engine
      target   = 'dedicated'
      result   = if ($_.verdict -eq 'pass') { 'ci-only' } else { 'fail' }
    }
  })
ConvertTo-Json -InputObject @($results) -Depth 6 | Set-Content (Join-Path $Out 'compat-results.json') -Encoding utf8
ConvertTo-Json -InputObject $tested -Depth 4 -Compress | Set-Content (Join-Path $Out 'tested-libraries.json') -Encoding utf8

$md = [System.Text.StringBuilder]::new()
[void]$md.AppendLine('## Library x server in s&box (dedicated server, ci-only)')
[void]$md.AppendLine('')
[void]$md.AppendLine("Engine: Facepunch/sbox-public ``$engineCommit``. Baseline: $(if ($Baseline) { 'latest server release' } else { 'none' }).")
[void]$md.AppendLine('')
[void]$md.AppendLine('| Library | Ref | Version | This build | Latest release | Verdict |')
[void]$md.AppendLine('| --- | --- | --- | --- | --- | --- |')
foreach ($r in $results) {
  $base = if ($null -eq $r.baseline) { '-' } elseif ($r.baseline) { 'pass' } else { 'fail' }
  [void]$md.AppendLine("| $($r.label) | ``$($r.ref)`` | $($r.library)$(if ($r.revision) { " (r$($r.revision))" }) | $(if ($r.candidate) { 'pass' } else { 'fail' }) | $base | **$($r.verdict)** |")
}
$md.ToString() | Set-Content (Join-Path $Out 'summary.md') -Encoding utf8
if ($env:GITHUB_STEP_SUMMARY) { $md.ToString() | Add-Content $env:GITHUB_STEP_SUMMARY }
Write-Host $md.ToString()

if ($results.Count -eq 0) { throw 'no library targets ran' }
if (@($results | Where-Object { $_.verdict -in 'regression', 'fail' }).Count -gt 0) { exit 1 }
exit 0
