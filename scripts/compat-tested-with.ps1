<#
.SYNOPSIS
  Prints the "Tested with" markdown table for a server release from compat/library-matrix.json.

.DESCRIPTION
  Selects the `tested` rows that belong to a release: rows whose `server` equals -Version, plus rows
  recorded against -Commit (a row's `commit` and the given commit match when one is a prefix of the other).
  The output is meant to be pasted into, or appended to, the GitHub Release notes. The maintainer wires it
  into the release workflow; see docs/releasing.md.

  Works on Windows PowerShell 5.1 and PowerShell 7 with no extra modules.

.PARAMETER Version
  Server version without the leading "v", for example 0.5.0.

.PARAMETER Commit
  Release commit SHA (full or abbreviated). Optional; rows recorded against the commit are included.

.PARAMETER Matrix
  Path to the matrix. Defaults to compat/library-matrix.json in this repository.

.PARAMETER Gate
  Release gate: exit with code 2 unless the matrix has matching-commit evidence for the `recommended`
  library: a `ci-only` row (green Compat engine legs) or a `pass` row (native editor run). The table is
  printed either way. A release with only `ci-only` evidence is a CI-only release, and the notes say so.

.EXAMPLE
  ./scripts/compat-tested-with.ps1 -Version 0.5.0 -Commit 6386646

.EXAMPLE
  ./scripts/compat-tested-with.ps1 -Version 0.5.0 -Gate | Out-File tested-with.md
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Commit = '',
    [string]$Matrix = '',
    [switch]$Gate
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Matrix)) {
    $Matrix = Join-Path (Join-Path $PSScriptRoot '..') 'compat/library-matrix.json'
}
if (-not (Test-Path -LiteralPath $Matrix)) {
    throw "Matrix not found: $Matrix"
}

$Version = $Version.Trim().TrimStart('v', 'V')
$data = Get-Content -LiteralPath $Matrix -Raw -Encoding UTF8 | ConvertFrom-Json
$rows = @($data.tested)

function Test-Selected($row) {
    if ($row.server -eq $Version) { return $true }
    if ($Commit -and $row.PSObject.Properties['commit'] -and $row.commit) {
        $a = $Commit.ToLowerInvariant()
        $b = ([string]$row.commit).ToLowerInvariant()
        return $a.StartsWith($b) -or $b.StartsWith($a)
    }
    return $false
}

function Format-Evidence($items) {
    $links = foreach ($item in @($items)) {
        $text = [string]$item
        if ($text -match '^([\w.-]+/[\w.-]+)#(\d+)$') {
            '[{0}#{1}](https://github.com/{0}/issues/{1})' -f $Matches[1], $Matches[2]
        }
        elseif ($text -match '^https?://') {
            '<{0}>' -f $text
        }
        else {
            $text
        }
    }
    return ($links -join ', ')
}

function Format-Cell($value) {
    if ($null -eq $value -or [string]$value -eq '') { return '-' }
    return ([string]$value).Replace('|', '\|')
}

function Get-Field($row, $name) {
    if ($row.PSObject.Properties[$name]) { return $row.$name }
    return $null
}

$selected = @($rows | Where-Object { Test-Selected $_ })

# Newest library first, then by engine, so the recommended library leads.
$selected = @($selected | Sort-Object -Property @{ Expression = { [version]($_.library -replace '[-+].*$', '') }; Descending = $true }, @{ Expression = { Get-Field $_ 'engine' }; Descending = $true })

$out = New-Object System.Collections.Generic.List[string]
$out.Add('### Tested with')
$out.Add('')

if ($selected.Count -eq 0) {
    $out.Add("No test evidence is recorded for server $Version in compat/library-matrix.json.")
}
else {
    $out.Add('| Library | Revision | Engine | OS | Server kind | Database | Result | Evidence |')
    $out.Add('|---|---|---|---|---|---|---|---|')
    foreach ($row in $selected) {
        $database = (@($row.database) -join ', ')
        $out.Add(('| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} |' -f
                (Format-Cell $row.library),
                (Format-Cell (Get-Field $row 'revision')),
                (Format-Cell (Get-Field $row 'engine')),
                (Format-Cell (Get-Field $row 'os')),
                (Format-Cell (Get-Field $row 'kind')),
                (Format-Cell $database),
                (Format-Cell $row.result),
                (Format-Evidence $row.evidence)))
    }
}

$recommended = [string]$data.recommended
$recommendedRows = @($selected | Where-Object { $_.library -eq $recommended })
$hasEvidence = @($recommendedRows | Where-Object { $_.result -eq 'pass' -or $_.result -eq 'ci-only' }).Count -gt 0
$hasPass = @($recommendedRows | Where-Object { $_.result -eq 'pass' }).Count -gt 0

$out.Add('')
if (-not $hasEvidence) {
    $out.Add("**Warning:** no matching-commit evidence for the recommended library ($recommended) exists for this release. Record the Compat engine legs (ci-only) or a native editor run (pass) in compat/library-matrix.json.")
}
elseif (-not $hasPass) {
    $out.Add("**CI-only release:** no native s&box editor run is recorded for the recommended library ($recommended). The green Compat engine legs (real library inside the real s&box engine) are the certification.")
}

$out -join "`n"

if ($Gate -and -not $hasEvidence) {
    [Console]::Error.WriteLine("Release gate failed: no matching-commit evidence ('ci-only' or 'pass') for recommended library $recommended on server $Version.")
    exit 2
}
