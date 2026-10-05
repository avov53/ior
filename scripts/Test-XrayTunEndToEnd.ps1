#requires -RunAsAdministrator
param([Parameter(Mandatory = $true)][string]$ProjectRoot)

$ErrorActionPreference = 'Stop'
$env:IOR_LIVE_XRAY_TUN_E2E = '1'
$env:IOR_USER_STATE_TEST_PATH = Join-Path $env:APPDATA 'ior\state.json'
$env:IOR_XRAY_TUN_TEST_BINARY = Join-Path $ProjectRoot 'core\xray\xray.exe'
Set-Location -LiteralPath $ProjectRoot
dotnet test .\HappBox.Tests\HappBox.Tests.csproj --no-restore -p:NuGetAudit=false --filter OptInXrayTunCarriesHttpsAndUdpAndReleasesRoutes --logger 'trx;LogFileName=xray-tun-e2e.trx' --results-directory .\verification-publish
exit $LASTEXITCODE
