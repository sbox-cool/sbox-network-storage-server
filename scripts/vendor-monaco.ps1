<#
.SYNOPSIS
  Vendors monaco-editor into src/SboxNetworkStorage.Server/wwwroot/vendor/monaco for the owner console.

.DESCRIPTION
  Downloads the pinned monaco-editor npm tarball, checks its sha512 against the registry's dist.integrity,
  and copies the runtime subset of the min/ build into the repository. The server embeds the result
  (see SboxNetworkStorage.Server.csproj) and serves it from /owner-assets/vendor/monaco/.

  Kept: every module reachable from vs/editor/editor.main (loader, editor, language services for JavaScript,
  TypeScript, JSON and YAML, and their workers), the editor CSS, LICENSE.txt and ThirdPartyNotices.txt.
  Dropped: NLS locale files (the owner console runs in English), the AMD development sources under vs/language
  and vs/editor/editor.worker.js, modules outside the editor.main import graph, and the CSS and HTML language
  services (workers and grammars). The owner console never opens CSS or HTML models.

  Works on Windows PowerShell 5.1 and PowerShell 7 with no extra modules. Uses tar, which ships with
  Windows 10 1803 and later. Re-run after changing $MonacoVersion, then review the resulting file list.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts/vendor-monaco.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

# Pinned upstream release (MIT).
$MonacoVersion = '0.57.0'

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Target = Join-Path $RepoRoot 'src/SboxNetworkStorage.Server/wwwroot/vendor/monaco'
$Sep = [IO.Path]::DirectorySeparatorChar

# Paths relative to package/min, with forward slashes. Each one was checked against the import graph of
# vs/editor/editor.main for this version. Re-check them when $MonacoVersion changes; the script fails if one is missing.
$Excluded = @(
  # Not imported by editor.main. The vs/editor and vs/index barrels are only needed by consumers of the module API.
  'vs/editor.js',
  'vs/index.js',
  'vs/index-049xrXrp.js',
  'vs/initialize-CAIC7-3s.js',
  # CSS and HTML language services. Only used when a CSS or HTML model is opened.
  'vs/css-CaeNmE3S.js',
  'vs/html-BTio3wpy.js',
  'vs/assets/css.worker-BpD9FCKP.js',
  'vs/assets/html.worker-CLuInTTy.js',
  # AMD development source of the editor worker. The bundled worker in vs/assets is loaded instead.
  'vs/editor/editor.worker.js'
)

function Test-Kept([string]$Rel) {
  if ($Rel.StartsWith('vs/nls/') -or $Rel.StartsWith('vs/language/')) { return $false }
  return -not ($Excluded -contains $Rel)
}

$registry = Invoke-RestMethod -Uri "https://registry.npmjs.org/monaco-editor/$MonacoVersion"
$tarballUrl = [string]$registry.dist.tarball
$integrity = [string]$registry.dist.integrity
if (-not $integrity.StartsWith('sha512-')) {
  throw "Unexpected integrity format for monaco-editor@${MonacoVersion}: $integrity"
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('monaco-vendor-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
  $tgz = Join-Path $work 'monaco-editor.tgz'
  Invoke-WebRequest -UseBasicParsing -Uri $tarballUrl -OutFile $tgz

  $sha512 = [Security.Cryptography.SHA512]::Create()
  try {
    $digest = [Convert]::ToBase64String($sha512.ComputeHash([IO.File]::ReadAllBytes($tgz)))
  } finally {
    $sha512.Dispose()
  }
  if ("sha512-$digest" -cne $integrity) {
    throw "Integrity mismatch for ${tarballUrl}: expected $integrity, got sha512-$digest."
  }

  $extract = Join-Path $work 'extract'
  New-Item -ItemType Directory -Path $extract | Out-Null
  & tar -xzf $tgz -C $extract
  if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE." }

  $pkgRoot = Join-Path $extract 'package'
  $minFull = [IO.Path]::GetFullPath((Join-Path $pkgRoot 'min'))
  if (-not (Test-Path -LiteralPath (Join-Path $minFull 'vs/loader.js'))) {
    throw "vs/loader.js not found in package/min of monaco-editor@$MonacoVersion."
  }
  foreach ($rel in $Excluded) {
    if (-not (Test-Path -LiteralPath (Join-Path $minFull $rel.Replace('/', $Sep)))) {
      throw "Excluded path '$rel' not found in monaco-editor@$MonacoVersion. Update the list in this script."
    }
  }

  if (Test-Path -LiteralPath $Target) { Remove-Item -LiteralPath $Target -Recurse -Force }
  New-Item -ItemType Directory -Path $Target | Out-Null

  foreach ($file in Get-ChildItem -LiteralPath $minFull -Recurse -File) {
    $rel = $file.FullName.Substring($minFull.Length + 1).Replace($Sep, '/')
    if (-not (Test-Kept $rel)) { continue }
    $dest = Join-Path $Target $rel.Replace('/', $Sep)
    New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $dest
  }

  Copy-Item -LiteralPath (Join-Path $pkgRoot 'LICENSE') -Destination (Join-Path $Target 'LICENSE.txt')
  Copy-Item -LiteralPath (Join-Path $pkgRoot 'ThirdPartyNotices.txt') -Destination (Join-Path $Target 'ThirdPartyNotices.txt')

  $vendored = Get-ChildItem -LiteralPath $Target -Recurse -File
  $bytes = ($vendored | Measure-Object -Property Length -Sum).Sum
  Write-Output "monaco-editor@$MonacoVersion vendored into $Target"
  Write-Output "Files: $($vendored.Count)"
  Write-Output "Bytes: $bytes"
} finally {
  Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
