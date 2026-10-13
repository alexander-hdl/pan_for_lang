# Merges the InkLab publish folder into the main V0 publish folder (used by publish-v0.cmd).
# Files that exist in both folders must be byte-identical (same runtime, same product assemblies): otherwise the merge stops with
# an error and nothing is copied, so a version mismatch cannot silently end up in the package.
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Target
)

$ErrorActionPreference = 'Stop'
$sourceRoot = (Resolve-Path -LiteralPath $Source).Path
$targetRoot = (Resolve-Path -LiteralPath $Target).Path

$conflicts = @()
$toCopy = @()
# EnumerateFiles keeps the root exactly as given (Get-ChildItem may expand 8.3 short names and break the relative path).
foreach ($fullName in [System.IO.Directory]::EnumerateFiles($sourceRoot, '*', [System.IO.SearchOption]::AllDirectories)) {
    $relative = $fullName.Substring($sourceRoot.Length).TrimStart('\')
    $existing = Join-Path $targetRoot $relative
    if (Test-Path -LiteralPath $existing -PathType Leaf) {
        $a = (Get-FileHash -LiteralPath $fullName -Algorithm SHA256).Hash
        $b = (Get-FileHash -LiteralPath $existing -Algorithm SHA256).Hash
        if ($a -ne $b) { $conflicts += $relative }
    }
    else {
        $toCopy += $relative
    }
}

if ($conflicts.Count -gt 0) {
    Write-Error ("InkLab publish differs from the main package in {0} file(s); nothing was copied:`n  {1}" -f $conflicts.Count, ($conflicts -join "`n  "))
    exit 1
}

foreach ($relative in $toCopy) {
    $destination = Join-Path $targetRoot $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceRoot $relative) -Destination $destination
}

Write-Host ("InkLab merged: {0} new file(s), {1} identical file(s) skipped." -f $toCopy.Count, (@([System.IO.Directory]::EnumerateFiles($sourceRoot, '*', [System.IO.SearchOption]::AllDirectories)).Count - $toCopy.Count))
exit 0
