param([Parameter(Mandatory=$true)][string]$ProjectRoot)
$ErrorActionPreference='Stop'
$benchRoot=[IO.Path]::GetFullPath($ProjectRoot)
$benchExecutable=Join-Path $benchRoot 'bin\Debug\net8.0-windows\ior.exe'
if(-not(Test-Path -LiteralPath $benchExecutable)){throw 'Build HappBox first.'}
$benchId=[Guid]::NewGuid().ToString('N')
$env:IOR_NATIVE_TEST_BINARY=Join-Path $benchRoot 'ior-core\target\release\ior-core.exe'
$env:IOR_NATIVE_TUN_BASELINE_BINARY=Join-Path $benchRoot 'verification-publish\20261004-awg-start-measured\windows-11\core\ior-core\ior-core.exe'
if(-not(Test-Path -LiteralPath $env:IOR_NATIVE_TUN_BASELINE_BINARY)){throw 'Prior TUN comparison binary is required for the baseline.'}
$env:IOR_USER_STATE_TEST_PATH=Join-Path $env:APPDATA 'ior\state.json'
$env:IOR_NATIVE_TUN_BENCH_REPORT=Join-Path $benchRoot ('verification-publish\tun-comparison-'+$benchId+'.json')
$benchProgress=Join-Path $env:APPDATA ('ior\diagnostics\native-tun-validation\'+$benchId+'\progress.json')
$benchElevation=[Diagnostics.Stopwatch]::StartNew()
try {
    $benchRun=Start-Process -FilePath $benchExecutable -ArgumentList @('--native-tun-benchmark',$benchId,('"'+$env:IOR_NATIVE_TUN_BASELINE_BINARY+'"')) -Verb RunAs -WindowStyle Hidden -PassThru
} catch {
    $benchElevation.Stop()
    # No validator started: no network lease or VPN process was created.
    $benchCode=if($_.Exception.NativeErrorCode -eq 1223 -or $_.Exception.InnerException.NativeErrorCode -eq 1223){'UAC_CANCELLED'}else{'ELEVATION_LAUNCH_FAILED'}
    @{schema=1;engine='ior-core';mode='tun';status=$benchCode;elevationWaitMs=$benchElevation.ElapsedMilliseconds;cyclesStarted=0;networkChanged=$false;accelerationConfirmed=$false}|ConvertTo-Json|Set-Content -LiteralPath $env:IOR_NATIVE_TUN_BENCH_REPORT -Encoding utf8
    Write-Host ('TUN comparison did not start: '+$benchCode)
    Write-Host ('Report: '+$env:IOR_NATIVE_TUN_BENCH_REPORT)
    exit 2
}
$benchElevation.Stop()
Write-Host ('Elevation and process launch: '+$benchElevation.ElapsedMilliseconds+'ms')
$benchLast=''
while(-not $benchRun.HasExited){
    if(Test-Path -LiteralPath $benchProgress){
        try{$benchStatus=Get-Content -LiteralPath $benchProgress -Raw|ConvertFrom-Json;if($benchStatus.Stage -ne $benchLast){$benchLast=$benchStatus.Stage;Write-Host ('TUN comparison: '+$benchLast);if($benchStatus.Reason){Write-Host $benchStatus.Reason}}}catch [System.IO.IOException]{}
    }
    Start-Sleep -Milliseconds 500
}
$benchRun.WaitForExit()
Write-Host ('Report: '+(Join-Path ([IO.Path]::GetDirectoryName($benchProgress)) 'comparison.json'))
if(Test-Path -LiteralPath $benchProgress){Get-Content -LiteralPath $benchProgress}
exit $benchRun.ExitCode
