# Builds dist\killing-duck-<version>.zip for the Chrome Web Store.
# Run pack.cmd rather than this.
#
# The store refuses a manifest with a "key" field, but the local copy keeps
# one so the unpacked extension's ID stays fixed for the helper. This strips
# "key" from the zipped manifest only, and leaves the helper out.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$manifest = Get-Content (Join-Path $root 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$manifest.PSObject.Properties.Remove('key')
$manifestJson = $manifest | ConvertTo-Json -Depth 20

# Everything the extension loads: root scripts, pages and styles, plus its
# image folders. The helper, README and these scripts stay out.
$files = @()
$files += Get-ChildItem $root -File | Where-Object { '.js', '.html', '.css' -contains $_.Extension }
$files += Get-ChildItem (Join-Path $root 'icons'), (Join-Path $root 'assets') -File -Recurse

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$zipPath = Join-Path $dist "killing-duck-$($manifest.version).zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

# Built by hand rather than with Compress-Archive, which writes backslashes
# into entry names on Windows PowerShell 5.1.
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, 'Create')
try {
    $entry = $zip.CreateEntry('manifest.json', 'Optimal')
    $writer = New-Object System.IO.StreamWriter($entry.Open(), (New-Object System.Text.UTF8Encoding $false))
    try { $writer.Write($manifestJson) } finally { $writer.Dispose() }

    foreach ($file in $files) {
        $name = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $name, 'Optimal') | Out-Null
        Write-Host "  $name"
    }
} finally {
    $zip.Dispose()
}

Write-Host ''
Write-Host "Done: $zipPath"
Write-Host 'Upload that zip on the Chrome Web Store developer dashboard.'
