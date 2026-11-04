param([string]$ResultDirectory='verification-publish\20261008-awg-packet-stage1',[switch]$Matrix,[ValidateSet('go','rust')][string]$Engine='go',[int]$Rtt=50)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output=[IO.Path]::GetFullPath((Join-Path $repo $ResultDirectory))
if(-not $output.StartsWith((Join-Path $repo 'verification-publish\'),[StringComparison]::OrdinalIgnoreCase)){throw 'Result directory must stay in verification-publish.'}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Run this local Wintun test as administrator.'}
$binary=Get-ChildItem -LiteralPath (Join-Path $repo 'ior-core\target\debug\deps') -Filter 'ior_core-*.exe' | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if(-not $binary){throw 'Build Rust lib tests first: cargo test --lib --no-run.'}
$result=Join-Path $output $(if($Matrix){'windows-tcp-matrix.json'}else{'windows-tcp-correctness.json'})
if(Test-Path -LiteralPath $result){throw 'Preserve existing Windows TCP test result.'}
$env:IOR_TEST_AWG_ADAPTER=Join-Path $repo 'core\ior-core\ior-awg-adapter.exe'
$env:IOR_TEST_WINTUN=Join-Path $repo 'core\xray\wintun.dll'
$env:IOR_AWG_LAB_OUTPUT=$result
$env:IOR_AWG_TEST_ENGINE=$Engine
$env:IOR_AWG_TEST_RTT=[string]$Rtt
if($Matrix){$env:IOR_AWG_TCP_MATRIX='1'}else{Remove-Item Env:\IOR_AWG_TCP_MATRIX -ErrorAction SilentlyContinue}
# Windows PowerShell 5 treats native stderr as ErrorRecord. Rust diagnostics
# legitimately use stderr; do not terminate the child at its first event.
$ErrorActionPreference='Continue'
& $binary.FullName 'tunnel_outbound::awg_packet_lab::windows_tcp_through_production_tun_and_official_awg' --exact --ignored --nocapture *> (Join-Path $output $(if($Matrix){'windows-tcp-matrix.log'}else{'windows-tcp-lab.log'}))
$testExit=$LASTEXITCODE
[IO.File]::WriteAllText((Join-Path $output $(if($Matrix){'windows-tcp-matrix-exit.txt'}else{'windows-tcp-lab-exit.txt'})),[string]$testExit)
exit $testExit
