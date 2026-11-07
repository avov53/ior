using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace HappBox;

/// <summary>Journals owned IPv4 host routes and a caller-authorized, temporary physical forwarding change.</summary>
public sealed class XrayTunRouteLease
{
    public sealed record HostRoute(string Prefix, int InterfaceIndex, string NextHop);
    private sealed record InspectedRoute(string Prefix, string State);
    private sealed record EgressInspection(string Forwarding, InspectedRoute[] Routes,int NativeDnsPolicyCount=0);
    private sealed record LeaseJournal(string InterfaceName, string Address, string Gateway, HostRoute[] Routes, string? RunId = null,
        int ForwardingInterfaceIndex = 0, bool RestoreForwarding = false, string? NativeTunName = null, int NativeTunIndex = 0,
        string[]? NativeTunOriginalDns = null, string? NativeTunAddress = null,
        string? NativeNrptDisplayName = null, string? NativeDnsAddress = null);
    private readonly string _journalPath;
    private HostRoute[] _ownedRoutes;
    private readonly string _interfaceName;
    private readonly string _physicalAddress;
    private readonly string _gateway;
    private readonly int _forwardingInterfaceIndex;
    private readonly bool _restoreForwarding;
    private readonly SemaphoreSlim _releaseGate = new(1, 1);

    private XrayTunRouteLease(string journalPath, HostRoute[] ownedRoutes, string interfaceName,
        string physicalAddress, string gateway, int forwardingInterfaceIndex = 0, bool restoreForwarding = false)
    {
        _journalPath = journalPath;
        _ownedRoutes = ownedRoutes;
        _interfaceName = interfaceName;
        _physicalAddress = physicalAddress;
        _gateway = gateway;
        _forwardingInterfaceIndex = forwardingInterfaceIndex;
        _restoreForwarding = restoreForwarding;
    }

    public static string JournalPath(string appDirectory) => Path.Combine(appDirectory, "xray-tun-routes.json");

    private static async Task<string> CaptureDnsPolicyReportAsync(string directory, CancellationToken token)
    {
        // Read-only, local report. Never send internal namespaces to the normal log.
        var report = await RunPowerShellAsync("$effective=@(Get-DnsClientNrptPolicy -Effective -ErrorAction Stop); $rules=@(Get-DnsClientNrptRule -ErrorAction Stop); @{Windows=[Environment]::OSVersion.Version.ToString(); Effective=$effective; Rules=$rules} | ConvertTo-Json -Depth 8", token);
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory,"dns-policy-report.json"),report,token);
        return report;
    }

    public static async Task<XrayTunRouteLease> AcquireAsync(string appDirectory, IPAddress server,
        IPAddress bootstrapDns, string interfaceName, CancellationToken cancellationToken = default, string? runId = null,
        bool allowTemporaryForwardingDisable = false,bool requireNativeDnsPolicyAvailable=false)
    {
        var dnsReport = requireNativeDnsPolicyAvailable ? await CaptureDnsPolicyReportAsync(appDirectory,cancellationToken) : null;
        var network = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item =>
            item.Name == interfaceName && item.OperationalStatus == OperationalStatus.Up &&
            item.NetworkInterfaceType is not (NetworkInterfaceType.Tunnel or NetworkInterfaceType.Loopback));
        var index = network?.GetIPProperties().GetIPv4Properties()?.Index ?? 0;
        var gateway = network?.GetIPProperties().GatewayAddresses
            .Select(item => item.Address).FirstOrDefault(item => item.AddressFamily == server.AddressFamily &&
                !item.Equals(IPAddress.Any));
        if (network is null || gateway is null)
            throw new InvalidOperationException("Не найден физический шлюз TUN для семьи адреса сервера.");
        var physicalAddress = network!.GetIPProperties().UnicastAddresses.Select(item => item.Address)
            .FirstOrDefault(item => item.AddressFamily == server.AddressFamily &&
                !IPAddress.IsLoopback(item) && !item.ToString().StartsWith("169.254.", StringComparison.Ordinal));
        if (physicalAddress is null)
            throw new InvalidOperationException("У физического интерфейса нет рабочего IPv4-адреса.");
        var candidates = new[] { server, bootstrapDns }.Distinct().Select(address =>
        {
            var properties=network.GetIPProperties();
            var familyGateway=properties.GatewayAddresses.Select(item=>item.Address).FirstOrDefault(ip=>ip.AddressFamily==address.AddressFamily && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any))
                ?? throw new InvalidOperationException("TUN_ENDPOINT_GATEWAY_UNAVAILABLE");
            var familyIndex=address.AddressFamily==AddressFamily.InterNetwork?properties.GetIPv4Properties()?.Index:properties.GetIPv6Properties()?.Index;
            if(familyIndex is null)throw new InvalidOperationException("TUN_ENDPOINT_INTERFACE_UNAVAILABLE");
            return new HostRoute($"{address}/{(address.AddressFamily==AddressFamily.InterNetwork?32:128)}",familyIndex.Value,familyGateway.ToString());
        }).ToArray();
        var inspectRoutes = string.Join(Environment.NewLine, candidates.Select(route => $@"
$matching = @($allRoutes | Where-Object {{ $_.DestinationPrefix -eq '{route.Prefix}' }})
$routeState = if ($matching.Count -eq 0) {{ 'NONE' }} elseif (@($matching | Where-Object {{ $_.InterfaceIndex -eq {route.InterfaceIndex} -and $_.NextHop -eq '{route.NextHop}' }}).Count -eq $matching.Count) {{ 'MATCH' }} else {{ 'CONFLICT' }}
$inspected.Add(@{{Prefix='{route.Prefix}'; State=$routeState}})"));
        var inspection = JsonSerializer.Deserialize<EgressInspection>(await RunPowerShellAsync($@"
$forwarding = if ({index} -gt 0) {{ (Get-NetIPInterface -AddressFamily IPv4 -InterfaceIndex {index} -ErrorAction Stop).Forwarding.ToString() }} else {{ 'Disabled' }}
$allRoutes = @(Get-NetRoute -PolicyStore ActiveStore -ErrorAction Stop)
$nativeDnsPolicies = {(requireNativeDnsPolicyAvailable?"@(Get-DnsClientNrptPolicy -Effective -ErrorAction Stop | Where-Object { $_.Namespace -and @($_.Namespace | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -gt 0 }).Count + @(Get-DnsClientNrptRule -ErrorAction Stop | Where-Object { $_.Namespace -and @($_.Namespace | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -gt 0 }).Count":"0")}
$inspected = [System.Collections.Generic.List[object]]::new()
{inspectRoutes}
@{{Forwarding=$forwarding; Routes=@($inspected.ToArray()); NativeDnsPolicyCount=$nativeDnsPolicies}} | ConvertTo-Json -Depth 4 -Compress", cancellationToken))
            ?? throw new IOException("TUN_EGRESS_INSPECTION_FAILED");
        if(requireNativeDnsPolicyAvailable && inspection.NativeDnsPolicyCount!=0)
            throw new InvalidOperationException(DnsPolicyConflict.Message(dnsReport));
        var forwardingEnabled = inspection.Forwarding switch { "Enabled" => true, "Disabled" => false,
            _ => throw new IOException("TUN_FORWARDING_INSPECTION_FAILED") };
        if (forwardingEnabled && !allowTemporaryForwardingDisable)
            throw new InvalidOperationException("На физическом интерфейсе включена переадресация IPv4. Она может возвращать DIRECT в TUN. Требуется согласие на временное отключение с восстановлением при остановке.");

        var journal = JournalPath(appDirectory);
        if (File.Exists(journal))
            throw new InvalidOperationException("NETWORK_RECOVERY_PENDING: восстановление сети после предыдущего TUN-подключения ещё не завершено.");
        var owned = new List<HostRoute>();
        foreach (var route in candidates)
        {
            var state = inspection.Routes.SingleOrDefault(item => item.Prefix == route.Prefix)?.State;
            if (state == "CONFLICT") throw new InvalidOperationException($"Для {route.Prefix} уже существует конфликтующий маршрут.");
            if (state != "MATCH" && state != "NONE") throw new InvalidOperationException("Не удалось проверить существующий маршрут Xray TUN.");
            if (state == "NONE") owned.Add(route);
        }

        var lease = new XrayTunRouteLease(journal, [.. owned], interfaceName,
            physicalAddress.ToString(), gateway.ToString(), index, forwardingEnabled);
        // Journal first: a crash after adding either route still leaves an exact recovery target.
        Directory.CreateDirectory(appDirectory);
        var pendingJournal = journal + ".pending";
        try
        {
            await File.WriteAllTextAsync(pendingJournal, JsonSerializer.Serialize(new LeaseJournal(interfaceName,
                physicalAddress.ToString(), gateway.ToString(), lease._ownedRoutes, runId, index, forwardingEnabled)), cancellationToken);
            File.Move(pendingJournal, journal);
        }
        finally
        {
            if (File.Exists(pendingJournal)) File.Delete(pendingJournal);
        }
        try
        {
            var apply = new List<string>();
            if (forwardingEnabled)
                apply.Add($"Set-NetIPInterface -AddressFamily IPv4 -InterfaceIndex {index} -Forwarding Disabled -PolicyStore ActiveStore -ErrorAction Stop");
            apply.AddRange(owned.Select(route => $"New-NetRoute -AddressFamily {(route.Prefix.Contains(':')?"IPv6":"IPv4")} -DestinationPrefix '{route.Prefix}' -InterfaceIndex {route.InterfaceIndex} -NextHop '{route.NextHop}' -RouteMetric 1 -PolicyStore ActiveStore -ErrorAction Stop | Out-Null"));
            if(index>0)apply.Add($"if ((Get-NetIPInterface -AddressFamily IPv4 -InterfaceIndex {index} -ErrorAction Stop).Forwarding.ToString() -ne 'Disabled') {{ throw 'TUN_PHYSICAL_FORWARDING_NOT_DISABLED' }}");
            await RunPowerShellAsync(string.Join(Environment.NewLine, apply), cancellationToken);
            // A successful New-NetRoute is not proof that Windows will use it.
            // Refuse to start TUN unless both bootstrap and server traffic actually
            // select the physical interface; the catch below restores owned routes.
            foreach (var address in new[] { server, bootstrapDns }.Distinct())
            {
                var selected = PhysicalInterfaceResolver.ForAddress(address);
                if (!string.Equals(selected, interfaceName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Маршрут к {address} выбрал интерфейс {selected} вместо {interfaceName}; Xray TUN не запущен.");
            }
            return lease;
        }
        catch
        {
            await lease.ReleaseAsync(CancellationToken.None);
            throw;
        }
    }

    public static XrayTunRouteLease? LoadForRecovery(string appDirectory, string? requiredRunId = null)
    {
        var journal = JournalPath(appDirectory);
        if (!File.Exists(journal)) return null;
        var data = JsonSerializer.Deserialize<LeaseJournal>(File.ReadAllText(journal)) ??
            throw new InvalidDataException("Журнал маршрутов Xray TUN пуст.");
        if (requiredRunId is not null && data.RunId != requiredRunId) return null;
        var routes = data.Routes ?? [];
        if (routes.Any(route => !IPNetwork.TryParse(route.Prefix, out _) ||
            !IPAddress.TryParse(route.NextHop, out _) || route.InterfaceIndex <= 0) ||
            string.IsNullOrWhiteSpace(data.InterfaceName) || !IPAddress.TryParse(data.Address, out _) ||
            !IPAddress.TryParse(data.Gateway, out _) || (data.RestoreForwarding && data.ForwardingInterfaceIndex <= 0))
            throw new InvalidDataException("Журнал маршрутов Xray TUN повреждён.");
        if (data.NativeTunName is not null &&
            (!System.Text.RegularExpressions.Regex.IsMatch(data.NativeTunName, "^ior-native-[a-f0-9]{8}$") ||
             data.NativeTunIndex <= 0 || !IPAddress.TryParse(data.NativeTunAddress, out var nativeAddress) ||
             nativeAddress.AddressFamily != AddressFamily.InterNetwork ||
             (data.NativeTunOriginalDns ?? []).Any(value => !IPAddress.TryParse(value, out _))))
            throw new InvalidDataException("Журнал собственного TUN повреждён; восстановление неизвестного интерфейса запрещено.");
        if (data.NativeNrptDisplayName is not null &&
            (data.NativeTunName is null || data.NativeNrptDisplayName != "ior-dns-" + data.NativeTunName ||
             !IPAddress.TryParse(data.NativeDnsAddress, out var nativeDns) || nativeDns.AddressFamily != AddressFamily.InterNetwork))
            throw new InvalidDataException("Журнал DNS собственного TUN повреждён.");
        return new XrayTunRouteLease(journal, routes, data.InterfaceName, data.Address, data.Gateway,
            data.ForwardingInterfaceIndex, data.RestoreForwarding);
    }

    public static async Task<bool> PhysicalForwardingEnabledAsync(string interfaceName, CancellationToken token = default)
    {
        var network = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item => item.Name == interfaceName)
            ?? throw new InvalidOperationException("Физический интерфейс недоступен.");
        var index = network.GetIPProperties().GetIPv4Properties()?.Index
            ?? throw new InvalidOperationException("У интерфейса нет IPv4.");
        var value = (await RunPowerShellAsync($"(Get-NetIPInterface -AddressFamily IPv4 -InterfaceIndex {index} -ErrorAction Stop).Forwarding.ToString()", token)).Trim();
        return value switch { "Enabled" => true, "Disabled" => false,
            _ => throw new InvalidOperationException("Не удалось проверить переадресацию IPv4.") };
    }

    public bool IsPhysicalLinkUnchanged()
    {
        var network = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item =>
            item.Name == _interfaceName && item.OperationalStatus == OperationalStatus.Up);
        if (network is null) return false;
        var properties = network.GetIPProperties();
        return properties.UnicastAddresses.Any(item => item.Address.ToString() == _physicalAddress) &&
            properties.GatewayAddresses.Any(item => item.Address.ToString() == _gateway);
    }

    public async Task ConfigureNativeTunAsync(string name, string address, IPAddress dns, CancellationToken token = default, int mtu = 1400, bool prepareAddresses = true)
    {
        var parameters = NativeTunAddress.Parse(address);
        if(mtu is <1280 or >1500)throw new InvalidOperationException("NATIVE_TUN_MTU_INVALID");
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^ior-native-[a-f0-9]{8}$") ||
            dns.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException("Некорректные параметры собственного TUN.");
        if(!await TunAdapterDiscovery.WaitAsync(name,token))
            throw new IOException("NATIVE_TUN_ADAPTER_NOT_READY: "+TunAdapterDiscovery.DescribeOwnedAdapters());
        var adapter = NetworkInterface.GetAllNetworkInterfaces().SingleOrDefault(item => item.Name == name)
            ?? throw new IOException("Собственный TUN-адаптер не появился.");
        var index = adapter.GetIPProperties().GetIPv4Properties()!.Index;
        var previousDns = adapter.GetIPProperties().DnsAddresses.Select(item => item.ToString()).ToArray();
        var routes = new[] { new HostRoute("0.0.0.0/1", index, "0.0.0.0"), new HostRoute("128.0.0.0/1", index, "0.0.0.0"),
            new HostRoute("::/1", index, "::"), new HostRoute("8000::/1", index, "::") };
        var journal = JsonSerializer.Deserialize<LeaseJournal>(await File.ReadAllTextAsync(_journalPath, token))!;
        journal = journal with { Routes = [.. _ownedRoutes, .. routes], NativeTunName = name, NativeTunIndex = index,
            NativeTunOriginalDns = previousDns, NativeTunAddress = address.Split('/')[0],
            NativeNrptDisplayName = "ior-dns-" + name, NativeDnsAddress = dns.ToString() };
        var pending = _journalPath + ".pending";
        await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(journal), token);
        File.Move(pending, _journalPath, overwrite: true);
        _ownedRoutes = journal.Routes;
        try
        {
            var ip = parameters.Adapter.ToString();
            // This newly owned Wintun interface is point-to-point. Publish its
            // addresses before routes/DNS so Windows does not send early probes
            // with a physical source address while DAD is still tentative.
            var addressPreparation = prepareAddresses ? $@"
Set-NetIPInterface -InterfaceIndex {index} -AddressFamily IPv4 -DadTransmits 0 -ErrorAction Stop
Set-NetIPInterface -InterfaceIndex {index} -AddressFamily IPv6 -DadTransmits 0 -ErrorAction Stop" : "";
            var applyRoutes = string.Join(Environment.NewLine, routes.Select(route =>
                $"New-NetRoute -AddressFamily {(route.Prefix.Contains(':') ? "IPv6" : "IPv4")} -DestinationPrefix '{route.Prefix}' -InterfaceIndex {index} -NextHop '{route.NextHop}' -RouteMetric 1 -PolicyStore ActiveStore -ErrorAction Stop | Out-Null"));
            await RunPowerShellAsync($@"
if (@(Get-DnsClientNrptPolicy -Effective -ErrorAction Stop | Where-Object {{ $_.Namespace -and @($_.Namespace | Where-Object {{ -not [string]::IsNullOrWhiteSpace($_) }}).Count -gt 0 }}).Count + @(Get-DnsClientNrptRule -ErrorAction Stop | Where-Object {{ $_.Namespace -and @($_.Namespace | Where-Object {{ -not [string]::IsNullOrWhiteSpace($_) }}).Count -gt 0 }}).Count -ne 0) {{ throw 'NATIVE_DNS_POLICY_CONFLICT' }}
{addressPreparation}
New-NetIPAddress -InterfaceIndex {index} -IPAddress '{ip}' -PrefixLength {parameters.PrefixLength} -AddressFamily IPv4 -ErrorAction Stop | Out-Null
New-NetIPAddress -InterfaceIndex {index} -IPAddress 'fdfe:dcba:9876::1' -PrefixLength 64 -AddressFamily IPv6 -ErrorAction Stop | Out-Null
Set-NetIPInterface -InterfaceIndex {index} -AddressFamily IPv4 -InterfaceMetric 1 -AutomaticMetric Disabled -NlMtuBytes {mtu} -ErrorAction Stop
Set-NetIPInterface -InterfaceIndex {index} -AddressFamily IPv6 -InterfaceMetric 1 -AutomaticMetric Disabled -NlMtuBytes {mtu} -ErrorAction Stop
{(prepareAddresses ? $"if (@(Get-NetIPAddress -InterfaceIndex {index} -ErrorAction Stop | Where-Object {{ $_.IPAddress -in @('{ip}', 'fdfe:dcba:9876::1') -and $_.AddressState.ToString() -eq 'Preferred' }}).Count -ne 2) {{ throw 'NATIVE_TUN_ADDRESS_NOT_PREFERRED' }}" : "")}
Set-DnsClientServerAddress -InterfaceIndex {index} -ServerAddresses '{dns}' -ErrorAction Stop
{applyRoutes}
Add-DnsClientNrptRule -Namespace '.' -NameServers '{dns}' -DisplayName '{journal.NativeNrptDisplayName}' -Comment 'ior-owned:{name}' -ErrorAction Stop | Out-Null
Clear-DnsClientCache -ErrorAction Stop
$ownedNrpt = @(Get-DnsClientNrptRule -ErrorAction Stop | Where-Object {{ $_.DisplayName -eq '{journal.NativeNrptDisplayName}' -and $_.Comment -eq 'ior-owned:{name}' -and $_.NameServers -contains '{dns}' }})
$effectiveNrpt = @(Get-DnsClientNrptPolicy -Effective -ErrorAction Stop | Where-Object {{ $_.Namespace -contains '.' -and $_.NameServers -contains '{dns}' }})
if ($ownedNrpt.Count -ne 1 -or $effectiveNrpt.Count -ne 1) {{ throw 'NATIVE_DNS_POLICY_NOT_APPLIED' }}", token);
            var configured=NetworkInterface.GetAllNetworkInterfaces().Single(item=>item.Name==name).GetIPProperties();
            if(configured.GetIPv4Properties()?.Mtu!=mtu || configured.GetIPv6Properties()?.Mtu!=mtu)throw new IOException("NATIVE_TUN_MTU_NOT_APPLIED");
        }
        catch { await ReleaseAsync(CancellationToken.None); throw; }
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        await _releaseGate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_journalPath)) return;
            var journal = JsonSerializer.Deserialize<LeaseJournal>(await File.ReadAllTextAsync(_journalPath, cancellationToken))!;
            Exception? dnsRollbackError = null;
            if (journal.NativeNrptDisplayName is { } policyName)
            {
                if (journal.NativeTunName is null || !System.Text.RegularExpressions.Regex.IsMatch(journal.NativeTunName,"^ior-native-[a-f0-9]{8}$") ||
                    policyName != "ior-dns-" + journal.NativeTunName || !IPAddress.TryParse(journal.NativeDnsAddress, out _))
                    throw new InvalidDataException("Журнал DNS собственного TUN повреждён.");
                try
                {
                    await RunPowerShellAsync($@"
$identified = @(Get-DnsClientNrptRule -ErrorAction Stop | Where-Object {{ $_.DisplayName -eq '{policyName}' -and $_.Comment -eq 'ior-owned:{journal.NativeTunName}' }})
$owned = @($identified | Where-Object {{ @($_.Namespace).Count -eq 1 -and $_.Namespace -contains '.' -and @($_.NameServers).Count -eq 1 -and $_.NameServers -contains '{journal.NativeDnsAddress}' }})
if ($identified.Count -ne $owned.Count) {{ throw 'NATIVE_DNS_POLICY_CHANGED' }}
foreach ($rule in $owned) {{ Remove-DnsClientNrptRule -Name $rule.Name -Force -Confirm:$false -ErrorAction Stop }}
if ($owned.Count -gt 0) {{ Clear-DnsClientCache -ErrorAction Stop }}", cancellationToken);
                }
                catch (Exception error) when (error is InvalidOperationException or IOException or TimeoutException)
                { dnsRollbackError = error; }
            }
            // One shell instead of starting PowerShell for every route. Continue cleanup
            // after a failed removal, while retaining the journal for recovery.
            var routeCleanup = string.Join(Environment.NewLine, _ownedRoutes.Select(route => $@"
try {{
$matching = @($currentRoutes | Where-Object {{ $_.DestinationPrefix -eq '{route.Prefix}' -and $_.InterfaceIndex -eq {route.InterfaceIndex} -and $_.NextHop -eq '{route.NextHop}' }})
foreach ($route in $matching) {{ Remove-NetRoute -InputObject $route -Confirm:$false -ErrorAction Stop }}
}} catch {{ $cleanupErrors.Add($_.Exception.Message) }}"));
            if (_ownedRoutes.Length > 0)
            {
                try
                {
                    await RunPowerShellAsync("$cleanupErrors = [System.Collections.Generic.List[string]]::new();\n$currentRoutes = @(Get-NetRoute -PolicyStore ActiveStore -ErrorAction Stop);\n" + routeCleanup +
                        "\nif ($cleanupErrors.Count -gt 0) { throw ($cleanupErrors -join '; ') }", cancellationToken);
                }
                catch (Exception error) when (error is InvalidOperationException or IOException or TimeoutException)
                { dnsRollbackError ??= error; }
            }
            if (journal.NativeTunName is { } tunName)
            {
                var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item => item.Name == tunName &&
                    item.GetIPProperties().GetIPv4Properties()?.Index == journal.NativeTunIndex);
                if (adapter is not null)
                {
                    var original = journal.NativeTunOriginalDns ?? [];
                    if (original.Any(value => !IPAddress.TryParse(value,out _)))
                        throw new InvalidDataException("Некорректный адрес DNS в журнале отката.");
                    var dnsArguments = original.Length == 0 ? "-ResetServerAddresses" :
                        "-ServerAddresses @("+string.Join(",",original.Select(value=>"'"+value+"'"))+")";
                    await RollbackCommandAsync($"Set-DnsClientServerAddress -InterfaceIndex {journal.NativeTunIndex} {dnsArguments} -ErrorAction Stop", cancellationToken);
                    if(journal.NativeTunAddress is { } nativeAddress && IPAddress.TryParse(nativeAddress,out _))
                        await RunPowerShellAsync($"Get-NetIPAddress -InterfaceIndex {journal.NativeTunIndex} -ErrorAction SilentlyContinue | Where-Object {{ $_.IPAddress -eq '{nativeAddress}' -or $_.IPAddress -eq 'fdfe:dcba:9876::1' }} | Remove-NetIPAddress -Confirm:$false -ErrorAction Stop",cancellationToken);
                }
            }
            if (_restoreForwarding)
            {
                // Never restore onto a different adapter which reused the interface index.
                var network = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item =>
                    item.Name == _interfaceName && item.GetIPProperties().GetIPv4Properties()?.Index == _forwardingInterfaceIndex);
                if (network is null) throw new InvalidOperationException("Восстановление переадресации отложено: исходный интерфейс недоступен. Журнал сохранён.");
                await RunPowerShellAsync($"Set-NetIPInterface -AddressFamily IPv4 -InterfaceIndex {_forwardingInterfaceIndex} -Forwarding Enabled -PolicyStore ActiveStore -ErrorAction Stop", cancellationToken);
                if (!await PhysicalForwardingEnabledAsync(_interfaceName, cancellationToken))
                    throw new InvalidOperationException("Не удалось подтвердить восстановление переадресации IPv4. Журнал сохранён для повторного восстановления.");
            }
            if (dnsRollbackError is not null)
                throw new IOException("NETWORK_ROLLBACK_PENDING: журнал сохранён для повторного восстановления. " + dnsRollbackError.Message, dnsRollbackError);
            File.Delete(_journalPath);
        }
        finally { _releaseGate.Release(); }
    }

    public static async Task EnsureNativeDnsPolicyAvailableAsync(CancellationToken token = default)
    {
        var count = (await RunPowerShellAsync("@(Get-DnsClientNrptPolicy -Effective -ErrorAction Stop | Where-Object { $_.Namespace -and @($_.Namespace | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -gt 0 }).Count + @(Get-DnsClientNrptRule -ErrorAction Stop | Where-Object { $_.Namespace -and @($_.Namespace | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -gt 0 }).Count", token)).Trim();
        if (count != "0")
            throw new InvalidOperationException(DnsPolicyConflict.Message(await RunPowerShellAsync("@{Rules=@(Get-DnsClientNrptRule -ErrorAction Stop); Effective=@(Get-DnsClientNrptPolicy -Effective -ErrorAction Stop)} | ConvertTo-Json -Depth 8",token)));
    }
    public static async Task<bool> VerifyNativeDnsPolicyAsync(string name, IPAddress dns, CancellationToken token = default)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(name,"^ior-native-[a-f0-9]{8}$") || dns.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var applied = await RunPowerShellAsync($@"
$owned = @(Get-DnsClientNrptRule -ErrorAction Stop | Where-Object {{ $_.DisplayName -eq 'ior-dns-{name}' -and $_.Comment -eq 'ior-owned:{name}' -and $_.NameServers -contains '{dns}' }})
$effective = @(Get-DnsClientNrptPolicy -Effective -ErrorAction Stop | Where-Object {{ $_.Namespace -contains '.' -and $_.NameServers -contains '{dns}' }})
if ($owned.Count -eq 1 -and $effective.Count -eq 1) {{ 'YES' }} else {{ 'NO' }}", token);
        return applied.Trim() == "YES";
    }

    private static Task<string> RunPowerShellAsync(string script, CancellationToken cancellationToken)
    {
        var kind=script.Contains("Remove-",StringComparison.Ordinal) || script.Contains("-Forwarding Enabled",StringComparison.Ordinal) || script.Contains("-ResetServerAddresses",StringComparison.Ordinal)
            ? WindowsNetworkOperation.Rollback : script.Contains("New-",StringComparison.Ordinal) || script.Contains("Set-",StringComparison.Ordinal) || script.Contains("Add-",StringComparison.Ordinal)
            ? WindowsNetworkOperation.Apply : WindowsNetworkOperation.Inspect;
        return kind==WindowsNetworkOperation.Rollback ? RollbackCommandAsync(script,cancellationToken) : WindowsNetworkCommand.RunAsync(script,cancellationToken,kind);
    }
    private static async Task<string> RollbackCommandAsync(string script,CancellationToken token)
    {
        try {return await WindowsNetworkCommand.RunAsync(script,token,WindowsNetworkOperation.Rollback);}
        catch(Exception error) when(error is IOException or TimeoutException) {return await WindowsNetworkCommand.RunIsolatedAsync(script,token);}
    }
}
