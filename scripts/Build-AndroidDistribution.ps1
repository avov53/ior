param([switch]$RebuildNative)
$ErrorActionPreference = 'Stop'
function Get-ApkHash([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($sha.ComputeHash($stream)) }
    finally { $sha.Dispose(); $stream.Dispose() }
}
$root = Split-Path $PSScriptRoot -Parent
$stage = Join-Path $root ('.local/android-dist-stage-' + [Guid]::NewGuid().ToString('N'))
& "$PSScriptRoot/Build-Android.ps1" -OutputDirectory $stage -ReuseVerifiedNative:(!$RebuildNative)
$built = Join-Path $stage 'ior-android-arm64.apk'
if (!(Test-Path -LiteralPath $built -PathType Leaf)) { throw 'Android build did not produce an APK; previous distribution preserved.' }
$destination = Join-Path $root 'distandroid'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
$current = Join-Path $destination 'ior-android-arm64.apk'
if (Test-Path -LiteralPath $current) {
    $backup = Join-Path $root ('.local/backups/android-dist-' + (Get-Date -Format yyyyMMdd-HHmmss) + '-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $backup | Out-Null
    Copy-Item -LiteralPath $current -Destination $backup
    if ((Get-ApkHash $current) -ne (Get-ApkHash (Join-Path $backup 'ior-android-arm64.apk'))) { throw 'Previous APK backup verification failed.' }
}
Copy-Item -LiteralPath $built -Destination $current -Force
if ((Get-ApkHash $current) -ne (Get-ApkHash $built)) { throw 'Distribution APK verification failed; staged build and backup retained.' }
Write-Host "Ready: $current"
