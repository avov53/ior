param([switch]$Apply)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
foreach($marker in @('distandroid.bat','ior-core/Cargo.toml')){
    if(!(Test-Path -LiteralPath (Join-Path $projectRoot $marker) -PathType Leaf)){throw 'Project marker missing'}
}
# Fixed allowlist: generated build intermediates only. Keep final native binaries,
# all distribution directories, source files, signing keys, backups and SDKs.
$relativeTargets=@(
    'ior-core/target/debug/build','ior-core/target/debug/deps','ior-core/target/debug/.fingerprint','ior-core/target/debug/incremental','ior-core/target/debug/examples',
    'ior-core/target/release/build','ior-core/target/release/deps','ior-core/target/release/.fingerprint','ior-core/target/release/incremental','ior-core/target/release/examples',
    'ior-core/target/aarch64-linux-android/release/build','ior-core/target/aarch64-linux-android/release/deps','ior-core/target/aarch64-linux-android/release/.fingerprint','ior-core/target/aarch64-linux-android/release/incremental','ior-core/target/aarch64-linux-android/release/examples',
    'android/app/build','android/.gradle','android/.kotlin',
    '.local/test-fixtures/hysteria-20261020'
)
function Get-CheckedCachePath([string]$Relative){
    $expected=[IO.Path]::GetFullPath((Join-Path $projectRoot $Relative))
    if(!$expected.StartsWith($projectRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Cache escapes project root'}
    $cursor=$projectRoot
    foreach($segment in @('')+$Relative.Split('/')){
        if($segment){$cursor=Join-Path $cursor $segment}
        $item=Get-Item -LiteralPath $cursor -Force
        if(!$item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw "Not a normal directory: $cursor"}
    }
    $resolved=[IO.Path]::GetFullPath((Resolve-Path -LiteralPath $expected).ProviderPath)
    if(![string]::Equals($resolved,$expected,[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected resolved cache path'}
    $links=@(Get-ChildItem -LiteralPath $resolved -Force -Recurse -Attributes ReparsePoint -ErrorAction Stop)
    if($links.Count){throw "Reparse points inside cache; refusing recursive removal: $resolved"}
    return $resolved
}
$plan=@(foreach($relative in $relativeTargets){
    if(Test-Path -LiteralPath (Join-Path $projectRoot $relative)){
        $path=Get-CheckedCachePath $relative
        $files=Get-ChildItem -LiteralPath $path -File -Recurse -Force -ErrorAction Stop | Measure-Object -Property Length -Sum
        [pscustomobject]@{relativePath=$relative;absolutePath=$path;bytes=[long]$files.Sum;files=$files.Count;removed=$false}
    }
})
$reportDirectory=Join-Path $projectRoot '.local/cleanup'
New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
$reportPath=Join-Path $reportDirectory 'build-cache-20261020.json'
$report=[ordered]@{root=$projectRoot;mode=if($Apply){'apply'}else{'preview'};targets=$plan;logicalBytes=($plan | Measure-Object bytes -Sum).Sum}
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding utf8
if($Apply){
    # Recheck each resolved absolute path immediately before deletion. Use native
    # PowerShell from validation through removal; never pass a path to another shell.
    foreach($entry in $plan){
        $path=Get-CheckedCachePath $entry.relativePath
        if(![string]::Equals($path,$entry.absolutePath,[StringComparison]::OrdinalIgnoreCase)){throw 'Cache path changed'}
        Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop
        $entry.removed=$true
        $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding utf8
    }
}
$plan | Select-Object relativePath,@{Name='GiB';Expression={[math]::Round($_.bytes/1GB,3)}},files,removed | Format-Table -AutoSize
Write-Host ('Total generated cache: '+[math]::Round($report.logicalBytes/1GB,3)+' GiB')
Write-Host ('Report: '+$reportPath)
