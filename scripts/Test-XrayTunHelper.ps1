#requires -RunAsAdministrator
param([Parameter(Mandatory = $true)][string]$ProjectRoot, [switch]$AllowTemporaryForwardingDisable)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $ProjectRoot
$env:IOR_LIVE_XRAY_DIRECT_HELPER = '1'
$env:IOR_USER_STATE_TEST_PATH = Join-Path $env:APPDATA 'ior\state.json'
$env:IOR_XRAY_TUN_TEST_BINARY = Join-Path $ProjectRoot 'core\xray\xray.exe'
if ($AllowTemporaryForwardingDisable) { $env:IOR_ALLOW_FORWARDING_CHANGE = '1' }
dotnet test .\HappBox.Tests\HappBox.Tests.csproj --no-build --no-restore --filter OptInXrayTunUsesPhysicalHelperForHappDirectTcpAndUdp --logger 'trx;LogFileName=xray-helper-live.trx' --results-directory .\verification-publish
exit $LASTEXITCODE
