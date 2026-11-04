namespace HappBox;

public static class CoreEngineService
{
    public static string? ActiveBackend(AppState state, bool isRunning) => isRunning ? (state.RunningCore ?? CoreKind.SingBox) switch
    {
        CoreKind.IorCore => "ior-core",
        CoreKind.XrayCore => "xray-core",
        _ => "sing-box"
    } : null;

    public sealed record Capabilities(bool Tcp, bool Udp, bool Dns, bool Routing, bool Proxy, bool Tun, ProxyProtocol[] Protocols);

    public static Capabilities Capability(CoreKind core, AppState? state = null) => core switch
    {
        CoreKind.SingBox => new(true, true, true, true, true, true, Enum.GetValues<ProxyProtocol>().Where(protocol => (int)protocol != 8 && protocol is not (ProxyProtocol.Vmess or ProxyProtocol.Shadowsocks or ProxyProtocol.Trojan)).ToArray()),
        CoreKind.XrayCore => new(true, true, true, true, true, NativeWindowsPlatform.TunAvailable,
            [ProxyProtocol.Vless, ProxyProtocol.Hysteria2, ProxyProtocol.WireGuard, ProxyProtocol.Socks, ProxyProtocol.Http]),
        CoreKind.IorCore => new(true, true, true, true, true, NativeWindowsPlatform.TunAvailable,
            [ProxyProtocol.Hysteria2, ProxyProtocol.Vless, ProxyProtocol.WireGuard, ProxyProtocol.AmneziaWG]),
        _ => new(false, false, false, false, false, false, [])
    };

    public static bool CanSelect(CoreKind core) => Enum.IsDefined(core);

    public static bool CanConnect(CoreKind core) => core is CoreKind.SingBox or CoreKind.XrayCore or CoreKind.IorCore;

    public static bool CanRun(AppState state, ProxyProfile? profile, out string reason)
    {
        if (profile is null) { reason = "Сначала выберите сервер."; return false; }
        if(RemovedProtocolPolicy.IsRemoved(profile)){reason=RemovedProtocolPolicy.Message;return false;}
        if (profile.Protocol == ProxyProtocol.AmneziaWG && (state.SelectedCore != CoreKind.IorCore || !state.TunEnabled))
        { reason = "AmneziaWG доступен только с ior-core и TUN."; return false; }
        if (profile.Protocol == ProxyProtocol.AmneziaWG && state.AwgEngine==AwgEngine.Go && !NativeProtocolComponents.VerifyAwg())
        { reason = "AWG: отсутствует проверенный пакетный движок."; return false; }
        switch (state.SelectedCore)
        {
            case CoreKind.IorCore:
                var promotedCombination = profile.Protocol == ProxyProtocol.Hysteria2 ||
                    profile.Protocol == ProxyProtocol.Vless && profile.RealityEnabled;
                if (!File.Exists(IorCorePath(state)))
                { reason = "Файл ior-core.exe отсутствует в этой сборке."; return false; }
                if (!state.MixedProxyEnabled)
                { reason = "Для ior-core нужен включённый локальный прокси."; return false; }
                if (!CoreRuntimeContract.TryCreateStart(state, profile, out _, out reason)) return false;
                if (state.TunEnabled && !NativeWindowsPlatform.TunAvailable)
                { reason = "ior-core TUN требует Windows 10 1607 (сборка 14393) или новее, либо Windows 11."; return false; }
                if(state.TunEnabled)
                {
                    if(profile.Protocol is not (ProxyProtocol.Hysteria2 or ProxyProtocol.Vmess or ProxyProtocol.Shadowsocks or ProxyProtocol.Trojan or ProxyProtocol.WireGuard or ProxyProtocol.AmneziaWG) && (profile.Protocol != ProxyProtocol.Vless || !profile.RealityEnabled))
                    { reason = "ior-core TUN сейчас испытывается для VLESS Reality; этот протокол требует отдельной проверки TUN."; return false; }
                }
                reason = ""; return true;
            case CoreKind.SingBox:

                var singBoxRoute = RoutingSelectionPolicy.Effective(state);
                if (singBoxRoute is { Source: RoutingProfileSource.Clash } &&
                    !SingBoxClashRuleCompiler.TryCompile(singBoxRoute, out _, out reason)) return false;
                reason = "";
                return true;
            case CoreKind.XrayCore:
                var xrayPath = XrayCorePath(state);
                if (!File.Exists(xrayPath))
                { reason = "Xray не установлен. Добавьте проверенный xray.exe в папку core/xray приложения."; return false; }
                if (profile.Protocol == ProxyProtocol.Hysteria2 && !SupportsHysteria2(xrayPath))
                { reason = "Этот xray.exe не поддерживает Hysteria2. Нужен Xray 26.9.9 или новее; встроенный бинарник обновлён."; return false; }
                if (state.TunEnabled)
                {
                    if (!NativeWindowsPlatform.TunAvailable)
                    { reason = "Xray TUN требует Windows 10 1607 или новее, либо Windows 11."; return false; }
                    return XrayConfigBuilder.CanBuildTun(state, profile, out reason);
                }
                return XrayConfigBuilder.CanBuild(state, profile, out reason);
            default:
                reason = ConnectionUnavailableReason(state.SelectedCore);
                return false;
        }
    }

    public static string ConnectionUnavailableReason(CoreKind core) => core switch
    {
        CoreKind.IorCore => "ior-core: VLESS, Hysteria2, WireGuard и AmneziaWG. Доступность зависит от режима и совместимости правил.",
        CoreKind.XrayCore => "Xray TUN доступен на Windows 10/11. При запуске проверяются маршруты и физический выход DIRECT.",
        _ => "Выбранное ядро не поддерживает подключение."
    };

    public static object[] AvailableCores(AppState state) =>
    [
        new { id = "iorCore", name = "ior-core", selectable = true, available = File.Exists(IorCorePath(state)), installed = File.Exists(IorCorePath(state)), version = FileVersion(IorCorePath(state)), backend = "ior-core", detail = "Proxy/TUN Windows 10/11: VLESS Reality, Hysteria2, WireGuard и AmneziaWG. Совместимость проверяется перед запуском.", capabilities = Capability(CoreKind.IorCore,state), combinations = NativeCombinations(state) },
        new { id = "singBox", name = "sing-box", selectable = true, available = true, installed = File.Exists(state.SingBoxPath), version = FileVersion(state.SingBoxPath), backend = "sing-box", detail = "Рабочий сетевой движок", capabilities = Capability(CoreKind.SingBox) },
        new { id = "xrayCore", name = "xray-core", selectable = true, available = File.Exists(XrayCorePath(state)), installed = File.Exists(XrayCorePath(state)), version = FileVersion(XrayCorePath(state)), backend = "xray-core", detail = "Proxy/TUN Windows 10/11: VLESS Reality, Hysteria2, WireGuard", capabilities = Capability(CoreKind.XrayCore) }
    ];

    public static object[] NativeCombinations(AppState state, Version? windowsVersion = null) => new[] { ProxyProtocol.Hysteria2, ProxyProtocol.Vless, ProxyProtocol.WireGuard, ProxyProtocol.AmneziaWG }.SelectMany(protocol => (protocol == ProxyProtocol.AmneziaWG ? new[] { "tun" } : new[] { "proxy", "tun" }).Select(mode =>
    {
        var promoted = protocol is ProxyProtocol.Hysteria2 or ProxyProtocol.Vless;
        var version = windowsVersion ?? Environment.OSVersion.Version;
        var windowsSupported = mode != "tun" || NativeWindowsPlatform.SupportsTun(version);
        return (object)new
        {
            protocol = protocol == ProxyProtocol.Vless ? "VLESS Reality" : protocol.ToString(), mode,
            status = !windowsSupported ? "unverifiedWindowsVersion" : "supported",
            implementation = protocol == ProxyProtocol.AmneziaWG ? state.AwgEngine==AwgEngine.Rust ? "ior-core AWG Rust" : "ior-core + официальный AWG Go" : "ior-core Rust",
            verified = promoted && windowsSupported && (mode != "tun" || NativeWindowsPlatform.IsWindows11(version))
        };
    })).ToArray();

    public static string IorCorePath(AppState state) => state.IorCorePath ?? Path.Combine(AppContext.BaseDirectory, "core", "ior-core", "ior-core.exe");
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,string> BuildHashes = new();
    public static string? NativeBuildId(AppState state)
    {
        try {
        var file=new FileInfo(IorCorePath(state)); if(!file.Exists)return null;
        return BuildHashes.GetOrAdd(file.FullName+"|"+file.Length+"|"+file.LastWriteTimeUtc.Ticks,_=>{
            using var stream=file.OpenRead();return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant()[..12];
        });
        } catch(IOException){return null;} catch(UnauthorizedAccessException){return null;}
    }

    public static string XrayCorePath(AppState state) => state.XrayCorePath ?? Path.Combine(AppContext.BaseDirectory, "core", "xray", "xray.exe");

    private static bool SupportsHysteria2(string binary)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(binary)
            {
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
            };
            start.ArgumentList.Add("version");
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null) return false;
            if (!process.WaitForExit(2000))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }
            var output = process.StandardOutput.ReadLine() ?? "";
            var match = System.Text.RegularExpressions.Regex.Match(output, @"\bXray\s+(\d+)\.(\d+)\.(\d+)\b");
            return match.Success && new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value)) >= new Version(26, 9, 9);
        }
        catch { return false; }
    }

    private static string? FileVersion(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try { return System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductVersion; }
        catch { return null; }
    }
}
