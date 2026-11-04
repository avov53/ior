param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/Build-FileHash.ps1"
$root=Split-Path $PSScriptRoot -Parent
Set-Location $root
function Run([string]$Tool,[string[]]$Arguments){
    $ErrorActionPreference='Continue'
    & $Tool @Arguments
    if($LASTEXITCODE -ne 0){throw "$Tool failed (exit $LASTEXITCODE)"}
}
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(!$output.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Output must be inside project'}
if(Test-Path -LiteralPath $output){throw 'Use a new output directory'}
foreach($tool in @('dotnet.exe','node.exe','npm.cmd','cargo.exe')){if(!(Get-Command $tool -ErrorAction SilentlyContinue)){throw "Required tool missing: $tool"}}
foreach($v in @('10','11')){if(!(Test-Path -LiteralPath "$root/core/win$v/sing-box.exe")){throw "Missing Windows $v core"}}
if(!(Test-Path -LiteralPath "$root/design/node_modules/rolldown")){
    Run 'npm.cmd' @('ci','--prefix',"$root/design",'--include=dev','--no-audit','--no-fund')
}
Run 'node.exe' @('design/scripts/build-native-refresh.mjs')
Run 'node.exe' @('design/scripts/verify-native-refresh.mjs')
& "$PSScriptRoot/Build-AwgPacketEngine.ps1" -VerifyOnly
Push-Location "$root/ior-core"
try {Run 'cargo.exe' @('build','-p','ior-core','--release','--offline','--locked','--bin','ior-core')}
finally {Pop-Location}
foreach($v in @('10','11')){
    $package=Join-Path $output "windows-$v"
    Run 'dotnet.exe' @('publish',"$root/HappBox.csproj",'-c','Release','-r','win-x64','--self-contained','true','-p:DebugType=None','-p:DebugSymbols=false','-o',$package)
    Copy-Item -LiteralPath "$root/core/win$v/sing-box.exe" -Destination "$package/core/sing-box.exe" -Force
    New-Item -ItemType File -Path "$package/ior.win$v" -Force | Out-Null
    & "$PSScriptRoot/Assert-NativePackage.ps1" -Source "$root/ior-core/target/release/ior-core.exe" -Package $package
    foreach($file in @('ior.exe','ior.dll','webui/index.html','core/ior-core/ior-core.exe')){if(!(Test-Path -LiteralPath "$package/$file")){throw "Missing packaged file: $file"}}
    Get-ChildItem -LiteralPath $package -File -Recurse | Get-FileHash | Select-Object Hash,Path | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath "$package/checksums.json" -Encoding UTF8
}
