param([Parameter(Mandatory=$true)][string]$Manifest,[switch]$NoLaunch)
$ErrorActionPreference='Stop'
function Get-InstallerFileHash([string]$LiteralPath){
 $stream=[IO.File]::OpenRead($LiteralPath);$algorithm=[Security.Cryptography.SHA256]::Create()
 try{return @{Hash=[BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','')}}finally{$algorithm.Dispose();$stream.Dispose()}
}
$job=Get-Content -LiteralPath $Manifest -Raw|ConvertFrom-Json
$source=[IO.Path]::GetFullPath($job.source)
$target=[IO.Path]::GetFullPath($job.target).TrimEnd('\')
$root=[IO.Path]::GetFullPath($job.root).TrimEnd('\')
$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
if(!$root.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase) -or (Split-Path $root -Leaf) -notmatch '^ior-update-[a-f0-9]{32}$'){throw 'Invalid update staging directory'}
if(!$source.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase) -or $target.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid installation paths'}
function Assert-PlainPath([string]$path){
 $current=$path
 while($current){if(Test-Path -LiteralPath $current){if((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Update path contains a link'}};$parent=Split-Path $current -Parent;if($parent -eq $current){break};$current=$parent}
}
Assert-PlainPath $source;Assert-PlainPath $target;Assert-PlainPath $root
[IO.File]::WriteAllText((Join-Path $root 'installer-ready.txt'),$PID.ToString())
$backup=Join-Path $root 'rollback'
$changes=New-Object 'System.Collections.Generic.List[object]'
$installStarted=$false
$env:IOR_SKIP_STARTUP_NETWORK='1'
try{
 $deadline=[DateTime]::UtcNow.AddMinutes(2)
 while(Get-Process -Id $job.processId -ErrorAction SilentlyContinue){if([DateTime]::UtcNow -gt $deadline){throw 'Application did not exit'};Start-Sleep -Milliseconds 250}
 $files=@(Get-ChildItem -LiteralPath $source -File -Recurse -Force)
 if(!(Test-Path -LiteralPath (Join-Path $source 'ior.exe'))){throw 'Package is missing ior.exe'}
 foreach($file in $files){
  Assert-PlainPath $file.FullName
  $relative=$file.FullName.Substring($source.TrimEnd('\').Length+1)
  if($relative -notmatch '^(core|webui|assets|runtimes|[a-z]{2}(?:-[A-Za-z]{2,8})?)\\' -and $relative -notmatch '^(createdump\.exe|Microsoft\.Web\.WebView2\.(Core|WinForms)\.xml|ior\.exe|ior\.dll|ior\.deps\.json|ior\.runtimeconfig\.json|ior\.win1[01]|[^\\]+\.dll)$'){throw "Unexpected package file: $relative"}
  $destination=[IO.Path]::GetFullPath((Join-Path $target $relative))
  if(!$destination.StartsWith($target+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid destination'}
  Assert-PlainPath $destination
  $saved=Join-Path $backup $relative;$exists=Test-Path -LiteralPath $destination
  if($exists){New-Item -ItemType Directory -Force (Split-Path $saved -Parent)|Out-Null;Copy-Item -LiteralPath $destination -Destination $saved -ErrorAction Stop}
  $changes.Add([pscustomobject]@{source=$file.FullName;destination=$destination;backup=$saved;existed=$exists})
 }
 $installStarted=$true
 foreach($change in $changes){New-Item -ItemType Directory -Force (Split-Path $change.destination -Parent)|Out-Null;Copy-Item -LiteralPath $change.source -Destination $change.destination -Force -ErrorAction Stop;if((Get-InstallerFileHash -LiteralPath $change.source).Hash -ne (Get-InstallerFileHash -LiteralPath $change.destination).Hash){throw 'Installed file checksum mismatch'}}
 if(!$NoLaunch){Start-Process -FilePath (Join-Path $target 'ior.exe') -ArgumentList '--after-update' -WorkingDirectory $target -WindowStyle Hidden}
 # Only the validated, uniquely named staging directory may be removed recursively.
 try{Remove-Item -LiteralPath $root -Recurse -Force}catch{Write-Warning 'Installed successfully; staging cleanup deferred'}
}catch{
 $failure=$_.Exception.Message;$restored=$true
 if($installStarted){foreach($change in $changes){try{if($change.existed){if(!(Test-Path -LiteralPath $change.destination) -or (Get-InstallerFileHash -LiteralPath $change.backup).Hash -ne (Get-InstallerFileHash -LiteralPath $change.destination).Hash){Copy-Item -LiteralPath $change.backup -Destination $change.destination -Force -ErrorAction Stop}}else{if(Test-Path -LiteralPath $change.destination){Remove-Item -LiteralPath $change.destination -Force -ErrorAction Stop}}}catch{$restored=$false}}}
 $message='Не удалось установить обновление: '+$failure+'. '+$(if($restored){'Прежняя версия сохранена.'}else{'Восстановление завершено не полностью. Резервные файлы: '+$backup})
 $message | Set-Content -LiteralPath (Join-Path $root 'error.txt') -Encoding UTF8
 if(!$NoLaunch -and $restored -and !(Get-Process -Id $job.processId -ErrorAction SilentlyContinue)){Start-Process -FilePath (Join-Path $target 'ior.exe') -ArgumentList '--after-update' -WorkingDirectory $target -WindowStyle Hidden}
 if(!$NoLaunch){Add-Type -AssemblyName System.Windows.Forms;[System.Windows.Forms.MessageBox]::Show($message,'Обновление ior')|Out-Null}
 exit 1
}
