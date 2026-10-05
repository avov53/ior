#requires -RunAsAdministrator
param([Parameter(Mandatory = $true)][string]$ProjectRoot)

$ErrorActionPreference = 'Stop'
$env:IOR_LIVE_ROUTE_LEASE_TEST = '1'
Set-Location -LiteralPath $ProjectRoot
dotnet test .\HappBox.Tests\HappBox.Tests.csproj --no-restore -p:NuGetAudit=false --filter OptInElevatedRouteLeaseAddsAndRemovesOnlyItsOwnHostRoutes --logger 'trx;LogFileName=xray-route-lease.trx' --results-directory .\verification-publish
exit $LASTEXITCODE
