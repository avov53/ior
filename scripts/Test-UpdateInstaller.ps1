$ErrorActionPreference='Stop'
$installer=Join-Path $PSScriptRoot 'Install-Update.ps1'
$workspace=Split-Path $PSScriptRoot -Parent
$testBase=Join-Path $workspace '.local/update-tests'
New-Item -ItemType Directory -Force $testBase|Out-Null
foreach($locked in @($false,$true)){
 $root=Join-Path ([IO.Path]::GetTempPath()) ('ior-update-'+[guid]::NewGuid().ToString('N'))
 $source=Join-Path $root 'files';$target=Join-Path $testBase ('Папка с пробелами '+[guid]::NewGuid().ToString('N'))
 New-Item -ItemType Directory -Force $source,$target|Out-Null
 [IO.File]::WriteAllText((Join-Path $source 'ior.exe'),'new exe')
 [IO.File]::WriteAllText((Join-Path $source 'ior.dll'),'new dll')
 [IO.File]::WriteAllText((Join-Path $target 'ior.exe'),'old exe')
 [IO.File]::WriteAllText((Join-Path $target 'ior.dll'),'old dll')
 [IO.File]::WriteAllText((Join-Path $target 'settings.json'),'user settings')
 [IO.File]::WriteAllText((Join-Path $target 'user-note.txt'),'user file')
 $manifest=Join-Path $root 'install.json'
 @{source=$source;target=$target;root=$root;processId=2147483647}|ConvertTo-Json|Set-Content -LiteralPath $manifest -Encoding UTF8
 $handle=$null
 try{
  if($locked){$handle=[IO.File]::Open((Join-Path $target 'ior.exe'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)}
  & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $installer -Manifest $manifest -NoLaunch
  $result=$LASTEXITCODE
 }finally{if($handle){$handle.Dispose()}}
 if($locked){if($result -ne 1 -or [IO.File]::ReadAllText((Join-Path $target 'ior.dll')) -ne 'old dll' -or [IO.File]::ReadAllText((Join-Path $target 'ior.exe')) -ne 'old exe'){throw 'Rollback failed'}}
 else{if($result -ne 0 -or [IO.File]::ReadAllText((Join-Path $target 'ior.dll')) -ne 'new dll'){throw 'Install failed'}}
 if([IO.File]::ReadAllText((Join-Path $target 'settings.json')) -ne 'user settings' -or [IO.File]::ReadAllText((Join-Path $target 'user-note.txt')) -ne 'user file'){throw 'User data changed'}
 Write-Output "Installer filesystem check passed: locked=$locked; Unicode path; user data preserved."
}
