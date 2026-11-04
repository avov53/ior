param([switch]$WindowsOnly,[switch]$RequireMacOS)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/Build-FileHash.ps1"
$root=Split-Path $PSScriptRoot -Parent
Set-Location $root
$stage=Join-Path $root ('.local/dist-stage-'+[Guid]::NewGuid().ToString('N'))
$macReady=$false
function Assert-ProjectDistNotRunning {
 $distPrefix=[IO.Path]::GetFullPath((Join-Path $root 'dist')).TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
 foreach($process in @(Get-Process -Name 'ior' -ErrorAction SilentlyContinue)){
  try {$executable=$process.Path}catch{continue}
  if($executable -and [IO.Path]::GetFullPath($executable).StartsWith($distPrefix,[StringComparison]::OrdinalIgnoreCase)){
   throw "Close ior running from this project's dist folder before building: $executable"
  }
 }
}
try {
 Assert-ProjectDistNotRunning
 if(!$WindowsOnly){try{& "$PSScriptRoot/Build-MacOSRemote.ps1" -Preflight;$macReady=$true}catch{if($RequireMacOS){throw};Write-Host "macOS skipped: $($_.Exception.Message)" -ForegroundColor Yellow}}
 & "$PSScriptRoot/Build-WindowsDistribution.ps1" -OutputDirectory $stage
 if($macReady){try{& "$PSScriptRoot/Build-MacOSRemote.ps1" -OutputDirectory $stage}catch{if($RequireMacOS){throw};Write-Host "macOS skipped: $($_.Exception.Message)" -ForegroundColor Yellow}}
 Assert-ProjectDistNotRunning
 $dist=Join-Path $root 'dist'
 New-Item -ItemType Directory -Force $dist | Out-Null
 foreach($name in @('windows-10','windows-11','macos-arm64','macos-x64')){
  $source=Join-Path $stage $name
  if(!(Test-Path -LiteralPath $source)){continue}
  $destination=Join-Path $dist $name
  $backup=Join-Path $root ('.local/dist-previous-'+$name+'-'+[Guid]::NewGuid().ToString('N'))
  if(Test-Path -LiteralPath $destination){Move-Item -LiteralPath $destination -Destination $backup}
  try{Move-Item -LiteralPath $source -Destination $destination}catch{if(Test-Path -LiteralPath $backup){Move-Item -LiteralPath $backup -Destination $destination};throw}
 }
 Write-Host "Windows packages ready: $dist/windows-10 and $dist/windows-11" -ForegroundColor Green
 exit 0
}catch{Write-Host "Build failed: $($_.Exception.Message)" -ForegroundColor Red;Write-Host "Previous packages preserved. Staging: $stage";exit 1}
