param([string]$OutputDirectory=(Join-Path $PSScriptRoot 'diagnostics-local'))
$ErrorActionPreference='Stop'
# Read-only system inspection. This report is local and may contain private DNS namespaces.
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$report=[ordered]@{utc=[DateTime]::UtcNow.ToString('o');windows=[Environment]::OSVersion.Version.ToString()}
foreach($kind in @('effective','rules','adapters')){
 try{
  $report[$kind]=switch($kind){
   'effective' {@(Get-DnsClientNrptPolicy -Effective -ErrorAction Stop)}
   'rules' {@(Get-DnsClientNrptRule -ErrorAction Stop)}
   'adapters' {@(Get-NetAdapter -IncludeHidden -ErrorAction Stop | Select-Object Name,InterfaceGuid,ifIndex,Status,InterfaceDescription)}
  }
 }catch{$report[$kind]=@{errorType=$_.Exception.GetType().Name}}
}
$binary=Join-Path $PSScriptRoot 'core/xray/xray.exe'
if(Test-Path -LiteralPath $binary){$report.xraySha256=(Get-FileHash -LiteralPath $binary).Hash;$report.xrayVersion=@(& $binary version)}
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'windows-network.json') -Encoding UTF8
Write-Output 'Saved local report. Review private DNS namespaces before sharing. No network settings were changed.'
