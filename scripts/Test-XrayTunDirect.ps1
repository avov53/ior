#requires -RunAsAdministrator
param(
    [Parameter(Mandatory = $true)][string]$XrayBinary,
    [Parameter(Mandatory = $true)][string]$ConfigPath,
    [Parameter(Mandatory = $true)][string]$ReportPath
)

$ErrorActionPreference = 'Stop'
$tunName = 'ior-xray-test'
$result = [ordered]@{ Ready = $false; Tcp = $false; Http = $false; TunRouteCount = 0; BestRoute = ''; PhysicalInterface = ''; RoutesRestored = $false; DnsRestored = $false; Error = '' }
$xrayProcess = $null
$preparedConfig = "$ReportPath.config.json"
$originalRoutes = @(Get-NetRoute -AddressFamily IPv4 | Select-Object InterfaceIndex, DestinationPrefix, NextHop, RouteMetric)
$originalDns = @(Get-DnsClientServerAddress -AddressFamily IPv4 | Select-Object InterfaceIndex, @{Name='Servers';Expression={$_.ServerAddresses -join ','}})

function Wait-TunProbe {
    param([System.Threading.Tasks.Task]$Task, [System.Diagnostics.Process]$Process, [string]$LogPath, [int]$TimeoutSeconds)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not $Task.IsCompleted -and [DateTime]::UtcNow -lt $deadline) {
        if ($Process.HasExited) { throw 'Xray exited during the network probe.' }
        $log = Get-Item -LiteralPath $LogPath -ErrorAction SilentlyContinue
        if ($log -and $log.Length -gt 262144) { throw 'Xray generated excessive traffic logs; stopping the TUN test.' }
        Start-Sleep -Milliseconds 100
    }
    if (-not $Task.IsCompleted) { throw 'The TUN network probe timed out.' }
    return $Task.GetAwaiter().GetResult()
}

try {
    if (Get-Process xray -ErrorAction SilentlyContinue) { throw 'Another Xray process is running; refusing to change routes.' }
    if (Get-NetAdapter -Name $tunName -ErrorAction SilentlyContinue) { throw 'The test TUN adapter already exists.' }
    $physicalRoute = Find-NetRoute -RemoteIPAddress '1.1.1.1' -ErrorAction Stop | Select-Object -First 1
    $physical = Get-NetAdapter -InterfaceIndex $physicalRoute.InterfaceIndex -ErrorAction Stop
    if ($physical.Status -ne 'Up' -or $physical.Name -like 'ior*') { throw 'No healthy physical outbound interface was found.' }
    $result.PhysicalInterface = $physical.Name
    $config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json -AsHashtable
    $config.inbounds[0].settings.autoOutboundsInterface = $physical.Name
    $physicalIp = Get-NetIPAddress -InterfaceIndex $physical.ifIndex -AddressFamily IPv4 -ErrorAction Stop |
        Where-Object { $_.AddressState -eq 'Preferred' -and $_.IPAddress -notlike '169.254.*' } |
        Select-Object -First 1 -ExpandProperty IPAddress
    if (-not $physicalIp) { throw 'The physical interface has no usable IPv4 address.' }
    $config.outbounds[0].sendThrough = $physicalIp
    $config.outbounds[0].streamSettings = @{ sockopt = @{ interface = $physical.Name } }
    $config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $preparedConfig -Encoding UTF8
    & $XrayBinary run -test -c $preparedConfig | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Xray rejected the pinned direct TUN config.' }
    $xrayProcess = Start-Process -FilePath $XrayBinary -ArgumentList @('run', '-c', "`"$preparedConfig`"") -PassThru -WindowStyle Hidden -WorkingDirectory (Split-Path -Parent $XrayBinary) -RedirectStandardOutput "$ReportPath.stdout.log" -RedirectStandardError "$ReportPath.stderr.log"
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    while ([DateTime]::UtcNow -lt $deadline -and -not $xrayProcess.HasExited) {
        if (Get-NetAdapter -Name $tunName -ErrorAction SilentlyContinue) { $result.Ready = $true; break }
        Start-Sleep -Milliseconds 200
    }
    if (-not $result.Ready) { throw 'Xray TUN adapter did not become ready within 8 seconds.' }
    $result.TunRouteCount = @(Get-NetRoute -InterfaceAlias $tunName -ErrorAction SilentlyContinue).Count
    $bestRoute = Find-NetRoute -RemoteIPAddress '1.1.1.1' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($bestRoute) { $result.BestRoute = "$($bestRoute.InterfaceAlias)/$($bestRoute.InterfaceIndex)" }
    $tcp = [System.Net.Sockets.TcpClient]::new()
    try {
        Wait-TunProbe -Task ($tcp.ConnectAsync('1.1.1.1', 443)) -Process $xrayProcess -LogPath "$ReportPath.stdout.log" -TimeoutSeconds 5 | Out-Null
        $result.Tcp = $tcp.Connected
    }
    catch { $result.Tcp = $false }
    finally { $tcp.Dispose() }
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(4)
    try {
        $response = Wait-TunProbe -Task ($client.GetAsync('https://www.cloudflare.com/cdn-cgi/trace')) -Process $xrayProcess -LogPath "$ReportPath.stdout.log" -TimeoutSeconds 5
        $result.Http = $response.IsSuccessStatusCode
    }
    finally { $client.Dispose(); $handler.Dispose() }
}
catch { $result.Error = $_.Exception.Message }
finally {
    if ($xrayProcess -and -not $xrayProcess.HasExited) {
        Stop-Process -Id $xrayProcess.Id -Force -ErrorAction SilentlyContinue
        $xrayProcess.WaitForExit(5000) | Out-Null
    }
    Start-Sleep -Seconds 2
    $leftover = Get-NetAdapter -Name $tunName -ErrorAction SilentlyContinue
    if ($leftover) {
        # Remove only routes on the adapter created by this isolated test.
        Get-NetRoute -InterfaceIndex $leftover.ifIndex -ErrorAction SilentlyContinue |
            Where-Object { $originalRoutes.InterfaceIndex -notcontains $_.InterfaceIndex } |
            Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue
    }
    $currentRoutes = @(Get-NetRoute -AddressFamily IPv4 | Select-Object InterfaceIndex, DestinationPrefix, NextHop, RouteMetric)
    $result.RoutesRestored = -not (@($currentRoutes | Where-Object {
        $candidate = $_
        -not (@($originalRoutes | Where-Object {
            $_.InterfaceIndex -eq $candidate.InterfaceIndex -and $_.DestinationPrefix -eq $candidate.DestinationPrefix -and $_.NextHop -eq $candidate.NextHop
        }).Count)
    }).Count)
    $currentDns = @(Get-DnsClientServerAddress -AddressFamily IPv4 | Select-Object InterfaceIndex, @{Name='Servers';Expression={$_.ServerAddresses -join ','}})
    $result.DnsRestored = -not (@($originalDns | Where-Object {
        $candidate = $_
        -not (@($currentDns | Where-Object { $_.InterfaceIndex -eq $candidate.InterfaceIndex -and $_.Servers -eq $candidate.Servers }).Count)
    }).Count)
    $result | ConvertTo-Json | Set-Content -LiteralPath $ReportPath -Encoding UTF8
}
