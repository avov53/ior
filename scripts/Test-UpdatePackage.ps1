param([Parameter(Mandatory=$true)][string]$Package)
$ErrorActionPreference='Stop'
$workspace=Split-Path $PSScriptRoot -Parent
$root=Join-Path ([IO.Path]::GetTempPath()) ('ior-update-'+[guid]::NewGuid().ToString('N'))
$source=Join-Path $root 'files'
$testBase=[IO.Path]::GetFullPath((Join-Path $workspace '.local/update-tests'))
$target=Join-Path $testBase ('Full package '+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $root,$target|Out-Null
Copy-Item -LiteralPath $Package -Destination $source -Recurse
[IO.File]::WriteAllText((Join-Path $target 'settings.json'),'preserve user settings')
$expected=@(Get-ChildItem -LiteralPath $source -File -Recurse|ForEach-Object{@{relative=$_.FullName.Substring($source.Length+1);hash=(Get-FileHash -LiteralPath $_.FullName).Hash}})
$manifest=Join-Path $root 'install.json'
@{source=$source;root=$root;target=$target;processId=2147483647}|ConvertTo-Json|Set-Content -LiteralPath $manifest -Encoding UTF8
& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Install-Update.ps1') -Manifest $manifest -NoLaunch
if($LASTEXITCODE -ne 0){throw 'Full package installation failed'}
foreach($file in $expected){if((Get-FileHash -LiteralPath (Join-Path $target $file.relative)).Hash -ne $file.hash){throw 'Copied package mismatch'}}
if([IO.File]::ReadAllText((Join-Path $target 'settings.json')) -ne 'preserve user settings'){throw 'Settings modified'}
if(![IO.Path]::GetFullPath($target).StartsWith($testBase+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe cleanup path'}
Remove-Item -LiteralPath $target -Recurse -Force
Write-Output "Full package filesystem installation passed: $($expected.Count) files; checksums match; settings preserved; no application launched."
