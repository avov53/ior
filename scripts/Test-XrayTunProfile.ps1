#requires -RunAsAdministrator
param(
    [Parameter(Mandatory = $true)][string]$XrayBinary,
    [Parameter(Mandatory = $true)][string]$ConfigPath,
    [Parameter(Mandatory = $true)][string]$ReportPath
)

$ErrorActionPreference = 'Stop'
$result = [ordered]@{ Ready = $false; Https = $false; Udp = $false; RoutesRestored = $false; DnsRestored = $false; Error = '' }
$xrayProcess = $null
$ownedRoutes = @()
$beforeRoutes = @(Get-NetRoute -AddressFamily IPv4 | Select-Object InterfaceIndex, DestinationPrefix, NextHop)
$beforeDns = @(Get-DnsClientServerAddress -AddressFamily IPv4 | Select-Object InterfaceIndex, @{Name='Servers';Expression={$_.ServerAddresses -join ','}})
$tunName = 'ior-xray'

function Wait-Probe {
    param([System.Threading.Tasks.Task]$Task, [System.Diagnostics.Process]$Process, [string]$LogPath, [int]$TimeoutSeconds)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not $Task.IsCompleted -and [DateTime]::UtcNow -lt $deadline) {
        if ($Process.HasExited) { throw 'Xray exited during the network probe.' }
        $log = Get-Item -LiteralPath $LogPath -ErrorAction SilentlyContinue
        if ($log -and $log.Length -gt 262144) { throw 'Excessive Xray traffic logs; test stopped to protect the computer.' }
        Start-Sleep -Milliseconds 100
    }
    if (-not $Task.IsCompleted) { throw 'Network probe timed out.' }
    return $Task.GetAwaiter().GetResult()
}

try {
    if (Get-Process xray -ErrorAction SilentlyContinue) { throw 'Another Xray process is running.' }
    $config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json -AsHashtable
    $tun = @($config.inbounds | Where-Object { $_.protocol -eq 'tun' }) | Select-Object -First 1
    if (-not $tun) { throw 'Configuration has no TUN inbound.' }
    $tunName = $tun.settings.name
    if (Get-NetAdapter -Name $tunName -ErrorAction SilentlyContinue) { throw 'The intended TUN adapter already exists.' }
    $serverIp = if ($config.outbounds[0].protocol -eq 'hysteria') {
        $config.outbounds[0].settings.address
    } else {
        $config.outbounds[0].settings.vnext[0].address
    }
    $dnsIp = $config.dns.servers[0]
    foreach ($address in @($serverIp, $dnsIp)) {
        $parsed = [System.Net.IPAddress]::Parse($address)
        if ($parsed.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) { throw 'Only IPv4 bootstrap routes are supported in this test.' }
    }
    $physicalRoute = Find-NetRoute -RemoteIPAddress $serverIp -ErrorAction Stop | Select-Object -First 1
    $physical = Get-NetAdapter -InterfaceIndex $physicalRoute.InterfaceIndex -ErrorAction Stop
    if ($physical.Status -ne 'Up' -or $physical.Name -like 'ior*') { throw 'Physical outbound interface unavailable.' }
    $gateway = Get-NetRoute -InterfaceIndex $physical.ifIndex -DestinationPrefix '0.0.0.0/0' -ErrorAction Stop |
        Sort-Object RouteMetric | Select-Object -First 1 -ExpandProperty NextHop
    if (-not $gateway -or $gateway -eq '0.0.0.0') { throw 'Physical gateway unavailable.' }
    foreach ($ip in @($serverIp, $dnsIp) | Select-Object -Unique) {
        $prefix = "$ip/32"
        $existing = @(Get-NetRoute -DestinationPrefix $prefix -ErrorAction SilentlyContinue)
        if ($existing.Count -gt 0) {
            if (-not (@($existing | Where-Object { $_.InterfaceIndex -eq $physical.ifIndex -and $_.NextHop -eq $gateway }).Count))
            { throw "A conflicting host route already exists for $prefix." }
            continue
        }
        New-NetRoute -DestinationPrefix $prefix -InterfaceIndex $physical.ifIndex -NextHop $gateway -RouteMetric 1 -PolicyStore ActiveStore -ErrorAction Stop | Out-Null
        $ownedRoutes += [pscustomobject]@{ Prefix = $prefix; Index = $physical.ifIndex; NextHop = $gateway }
    }
    $xrayProcess = Start-Process -FilePath $XrayBinary -ArgumentList @('run', '-c', "`"$ConfigPath`"") -PassThru -WindowStyle Hidden -WorkingDirectory (Split-Path -Parent $XrayBinary) -RedirectStandardOutput "$ReportPath.stdout.log" -RedirectStandardError "$ReportPath.stderr.log"
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    while ([DateTime]::UtcNow -lt $deadline -and -not $xrayProcess.HasExited) {
        if (Get-NetAdapter -Name $tunName -ErrorAction SilentlyContinue) { $result.Ready = $true; break }
        Start-Sleep -Milliseconds 200
    }
    if (-not $result.Ready) { throw 'Xray TUN adapter did not become ready.' }
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(8)
    try {
        $response = Wait-Probe -Task ($client.GetAsync('https://www.cloudflare.com/cdn-cgi/trace')) -Process $xrayProcess -LogPath "$ReportPath.stdout.log" -TimeoutSeconds 9
        $result.Https = $response.IsSuccessStatusCode
    }
    finally { $client.Dispose(); $handler.Dispose() }
    $udp = [System.Net.Sockets.UdpClient]::new()
    try {
        $query = [byte[]](0x12,0x34,0x01,0x00,0x00,0x01,0,0,0,0,0,0,7,101,120,97,109,112,108,101,3,99,111,109,0,0,1,0,1)
        $udp.Connect('8.8.8.8', 53)
        $udp.Send($query, $query.Length) | Out-Null
        $reply = Wait-Probe -Task ($udp.ReceiveAsync()) -Process $xrayProcess -LogPath "$ReportPath.stdout.log" -TimeoutSeconds 5
        $result.Udp = $reply.Buffer.Length -ge 12 -and $reply.Buffer[0] -eq 0x12 -and $reply.Buffer[1] -eq 0x34
    }
    finally { $udp.Dispose() }
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
        Get-NetRoute -InterfaceIndex $leftover.ifIndex -ErrorAction SilentlyContinue |
            Where-Object { $beforeRoutes.InterfaceIndex -notcontains $_.InterfaceIndex } |
            Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue
    }
    foreach ($owned in $ownedRoutes) {
        Remove-NetRoute -DestinationPrefix $owned.Prefix -InterfaceIndex $owned.Index -NextHop $owned.NextHop -Confirm:$false -ErrorAction SilentlyContinue
    }
    $currentRoutes = @(Get-NetRoute -AddressFamily IPv4 | Select-Object InterfaceIndex, DestinationPrefix, NextHop)
    $result.RoutesRestored = -not (@($currentRoutes | Where-Object {
        $candidate = $_
        -not (@($beforeRoutes | Where-Object { $_.InterfaceIndex -eq $candidate.InterfaceIndex -and $_.DestinationPrefix -eq $candidate.DestinationPrefix -and $_.NextHop -eq $candidate.NextHop }).Count)
    }).Count)
    $currentDns = @(Get-DnsClientServerAddress -AddressFamily IPv4 | Select-Object InterfaceIndex, @{Name='Servers';Expression={$_.ServerAddresses -join ','}})
    $result.DnsRestored = -not (@($beforeDns | Where-Object {
        $candidate = $_
        -not (@($currentDns | Where-Object { $_.InterfaceIndex -eq $candidate.InterfaceIndex -and $_.Servers -eq $candidate.Servers }).Count)
    }).Count)
    $result | ConvertTo-Json | Set-Content -LiteralPath $ReportPath -Encoding UTF8
}
