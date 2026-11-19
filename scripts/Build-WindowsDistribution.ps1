param([Parameter(Mandatory=$true)][string]$OutputDirectory,
    [switch]$ReuseVerifiedNative,
    [switch]$NewInterface,
    [string]$BaselineDirectory='.local/backups/ui-integration-20261117')
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
$requiredTools=@('dotnet.exe','node.exe','npm.cmd');if(!$ReuseVerifiedNative){$requiredTools+='cargo.exe'}
foreach($tool in $requiredTools){if(!(Get-Command $tool -ErrorAction SilentlyContinue)){throw "Required tool missing: $tool"}}
if($ReuseVerifiedNative){
    . "$PSScriptRoot/Verified-UiNative.ps1"
    $baseline=Resolve-UiNativeBaseline $root $BaselineDirectory
}
$uiDependencies=if($NewInterface){'design/app'}else{'design'}
$dependencyReady=if($NewInterface){(Test-Path "$root/design/app/node_modules/vite") -or (Test-Path "$root/design/test/node_modules/vite")}else{Test-Path "$root/design/node_modules/rolldown"}
if(!$dependencyReady){
    Run 'npm.cmd' @('ci','--prefix',"$root/$uiDependencies",'--include=dev','--no-audit','--no-fund')
}
if($NewInterface){
    if(!$ReuseVerifiedNative){throw 'New interface builds require -ReuseVerifiedNative'}
    Run 'node.exe' @('design/app/build.mjs','windows')
}else{
    Run 'node.exe' @('design/scripts/build-native-refresh.mjs')
    Run 'node.exe' @('design/scripts/verify-native-refresh.mjs')
}
if(!$ReuseVerifiedNative){
    & "$PSScriptRoot/Build-AwgPacketEngine.ps1" -VerifyOnly
    Push-Location "$root/ior-core"
    try {Run 'cargo.exe' @('build','-p','ior-core','--release','--offline','--locked','--bin','ior-core')}
    finally {Pop-Location}
}
foreach($v in @('10','11')){
    $package=Join-Path $output "windows-$v"
    $publishArgs=@('publish',"$root/HappBox.csproj",'-c','Release','-r','win-x64','--self-contained','true','-p:DebugType=None','-p:DebugSymbols=false','-o',$package)
    if($NewInterface){$publishArgs+='-p:UseNewUi=true'}
    Run 'dotnet.exe' $publishArgs
    if(Get-ChildItem -LiteralPath $package -Filter 'sing-box*.exe' -File -Recurse){throw 'Retired sing-box executable unexpectedly included in package'}
    $nativeSource="$root/ior-core/target/release/ior-core.exe"
    New-Item -ItemType File -Path "$package/ior.win$v" -Force | Out-Null
    if(!$ReuseVerifiedNative){ & "$PSScriptRoot/Assert-NativePackage.ps1" -Source $nativeSource -Package $package }
    if(!$ReuseVerifiedNative -and $v -eq '10'){
        (Get-FileHash -LiteralPath $nativeSource -Algorithm SHA256).Hash | Set-Content -LiteralPath "$package/core/ior-core/awg-named-ipv4.sha256" -Encoding ascii
    }
    if($ReuseVerifiedNative){
        Copy-VerifiedUiNative $baseline "windows-$v" $package
        $nativeSource=Join-Path $baseline "dist/windows-$v/core/ior-core/ior-core.exe"
        & "$PSScriptRoot/Assert-NativePackage.ps1" -Source $nativeSource -Package $package
    }
    foreach($file in @('ior.exe','ior.dll','webui/index.html','core/ior-core/ior-core.exe')){if(!(Test-Path -LiteralPath "$package/$file")){throw "Missing packaged file: $file"}}
    Get-ChildItem -LiteralPath $package -File -Recurse | ForEach-Object { @{path=$_.FullName.Substring($package.Length+1);hash=(Get-FileHash -LiteralPath $_.FullName).Hash} } | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath "$output/windows-$v-checksums.json" -Encoding UTF8
}
