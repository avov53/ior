param([Parameter(Mandatory=$true)][string]$ProjectRoot,[switch]$Reality,[switch]$Awg,[switch]$Hysteria2,[switch]$Discord,[switch]$Routing,[switch]$Crash,[ValidateRange(0,30)][int]$LoadMinutes=0)
$ErrorActionPreference='Stop'
if($LoadMinutes -gt 0 -and -not [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
    throw 'Run PowerShell as administrator for the optional extended load test.'
}
$env:IOR_PROJECT_ROOT=$ProjectRoot
$env:IOR_NATIVE_REALITY_TEST='1'
if($Awg){$env:IOR_NATIVE_REALITY_TEST='0';$env:IOR_NATIVE_AWG_TEST='1'}else{$env:IOR_NATIVE_AWG_TEST='0'}
$env:IOR_NATIVE_HYSTERIA_TEST=if($Hysteria2){'1'}else{'0'}
if($Hysteria2){$env:IOR_NATIVE_REALITY_TEST='0'}
$env:IOR_NATIVE_DISCORD_TEST='1'
$env:IOR_NATIVE_TUN_ROUTING_TEST='1'
$env:IOR_NATIVE_TUN_LOAD_MINUTES=$LoadMinutes.ToString()
$env:IOR_USER_STATE_TEST_PATH=Join-Path $env:APPDATA 'ior\state.json'
$env:IOR_NATIVE_TEST_BINARY=Join-Path $ProjectRoot 'ior-core\target\release\ior-core.exe'
Set-Location -LiteralPath $ProjectRoot
$nativeResults=Join-Path $ProjectRoot 'verification-publish'
[System.IO.Directory]::CreateDirectory($nativeResults)|Out-Null
$nativeValidationId=[Guid]::NewGuid().ToString('N')
$env:IOR_NATIVE_TUN_PROGRESS_PATH=Join-Path $env:APPDATA ('ior\diagnostics\native-tun-validation\'+$nativeValidationId+'\progress.json')
$nativeValidator=Join-Path $ProjectRoot 'bin\Debug\net8.0-windows\ior.exe'
if(-not (Test-Path -LiteralPath $nativeValidator)){throw 'Build HappBox before running the native TUN validator.'}
$nativeElevationTime=[Diagnostics.Stopwatch]::StartNew()
$nativeRun=Start-Process -FilePath $nativeValidator -Verb RunAs -WindowStyle Hidden -PassThru `
    -ArgumentList @('--native-tun-validate',$nativeValidationId)
$nativeElevationTime.Stop()
Write-Host ('Native TUN: elevation-and-launch-wait-ms='+$nativeElevationTime.ElapsedMilliseconds)
$lastNativeStage=''
try {
    while(-not $nativeRun.HasExited) {
        try {
            $nativeProgress=Get-Content -LiteralPath $env:IOR_NATIVE_TUN_PROGRESS_PATH -Raw | ConvertFrom-Json
            if($nativeProgress.stage -ne $lastNativeStage) {
                $lastNativeStage=$nativeProgress.stage
                Write-Host ('Native TUN: '+$lastNativeStage)
                if($nativeProgress.Reason){Write-Host $nativeProgress.Reason}
            }
        } catch { }
        Start-Sleep -Milliseconds 500
    }
    $nativeRun.WaitForExit()
    if(Test-Path -LiteralPath $env:IOR_NATIVE_TUN_PROGRESS_PATH){Get-Content -LiteralPath $env:IOR_NATIVE_TUN_PROGRESS_PATH}
    if($nativeRun.ExitCode -ne 0){exit $nativeRun.ExitCode}
    if($LoadMinutes -gt 0){
        $env:IOR_LIVE_NATIVE_TUN_EXTENDED='1'
        dotnet test .\HappBox.Tests\HappBox.Tests.csproj --no-build --filter OptInExtendedNativeTunTrafficExperiment --logger 'trx;LogFileName=native-tun-load.trx' --results-directory $nativeResults
        exit $LASTEXITCODE
    }
    exit 0
} finally {
    if(-not $nativeRun.HasExited){& taskkill.exe /PID $nativeRun.Id /T /F | Out-Null}
}
