# Pack widget-session/ into an .icuewidget (= zip) in dist/
# This is the only way to get it onto the real screen - copying files over html_widgets\
# gets wiped by iCUE every time the program restarts (see WORK-LOG-2026-08-23-widget-v2.md item 9)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$src  = Join-Path $root 'widget-session'

# matches what is actually in the v1.0.3 package - mock/ is not included
$files = 'index.html', 'manifest.json', 'styles/edge.css', 'resources/icon.svg'

$version = (Get-Content (Join-Path $src 'manifest.json') -Raw | ConvertFrom-Json).version
$out = Join-Path $root "dist/claudesession-v$version.icuewidget"

Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path $out) { Remove-Item $out }
$zip = [System.IO.Compression.ZipFile]::Open($out, 'Create')
try {
    foreach ($f in $files) {
        $path = Join-Path $src $f
        if (-not (Test-Path $path)) { throw "File not found: $path" }
                # entry names must use forward slashes per the zip format
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $path, $f) | Out-Null
    }
} finally { $zip.Dispose() }

"Packed: $out  ($((Get-Item $out).Length) B, $($files.Count) files, v$version)"
