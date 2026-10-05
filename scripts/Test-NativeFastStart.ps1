param([Parameter(Mandatory=$true)][string]$ProjectRoot,[ValidateRange(1,5)][int]$Repetitions=1)
$ErrorActionPreference='Stop'
$testRoot=[IO.Path]::GetFullPath($ProjectRoot)
$testExecutable=Join-Path $testRoot 'bin\Debug\net8.0-windows\ior.exe'
$testId=[Guid]::NewGuid().ToString('N')
$testProgress=Join-Path $env:APPDATA ('ior\diagnostics\native-tun-validation\'+$testId+'\progress.json')
$env:IOR_NATIVE_TEST_BINARY=Join-Path $testRoot 'ior-core\target\release\ior-core.exe'
$testIsAdmin=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
try {
    if($testIsAdmin){$testProcess=Start-Process -FilePath $testExecutable -WindowStyle Hidden -ArgumentList @('--native-tun-fast-check',$testId,[string]$Repetitions) -PassThru}
    else {$testProcess=Start-Process -FilePath $testExecutable -Verb RunAs -WindowStyle Hidden -ArgumentList @('--native-tun-fast-check',$testId,[string]$Repetitions) -PassThru}
} catch {Write-Host 'FAST_TUN_TEST_ELEVATION_FAILED: network tests did not start';exit 2}
$testStage=''
while(-not $testProcess.HasExited){
    if(Test-Path -LiteralPath $testProgress){
        try {$testResult=Get-Content -LiteralPath $testProgress -Raw|ConvertFrom-Json;if($testStage -ne $testResult.Stage){$testStage=$testResult.Stage;Write-Host $testStage}}catch [IO.IOException]{}
    }
    Start-Sleep -Milliseconds 500
}
$testProcess.WaitForExit()
if(Test-Path -LiteralPath $testProgress){Get-Content -LiteralPath $testProgress}
Write-Host ('Results: '+[IO.Path]::GetDirectoryName($testProgress))
exit $testProcess.ExitCode
