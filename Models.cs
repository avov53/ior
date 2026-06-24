using System.Text.Json.Serialization;

namespace HappBox;

public sealed class AppState
{
    public string SingBoxPath { get; set; } = "sing-box.exe";
    public bool TunEnabled { get; set; } = true;
    public bool MixedProxyEnabled { get; set; } = true;
    public int MixedProxyPort { get; set; } = 2080;
    public int TunMtu { get; set; } = 9000;
    public bool SniffEnabled { get; set; } = true;
    public bool AutoRoute { get; set; } = true;
    public bool StrictRoute { get; set; } = true;
    public bool StackSystem { get; set; } = false;
    public bool AllowInsecureCertificates { get; set; } = false;
    public bool SetSystemProxy { get; set; } = true;
    public string TunAddress { get; set; } = "172.19.0.1/30";
    public string DnsServer { get; set; } = "1.1.1.1";
    public string DnsStrategy { get; set; } = "prefer_ipv4";
    public string LogLevel { get; set; } = "info";
    public bool RoutingDefaultsSeeded { get; set; }
    public List<string> RoutingBypassSites { get; set; } =
    [
        "ozon.ru",
        "www.ozon.ru",
        "yandex.ru",
        "ya.ru",
        "wildberries.ru",
        "www.wildberries.ru"
    ];
    public List<string> RoutingProxySites { get; set; } = [];
    public string? SelectedProfileId { get; set; }
    public int? ActiveProcessId { get; set; }
    public DateTime? ActiveConnectionStartedUtc { get; set; }
    public bool SystemProxyApplied { get; set; }
    public bool? PreviousProxyEnabled { get; set; }
    public string? PreviousProxyServer { get; set; }
    public string? PreviousProxyOverride { get; set; }
    public AppLanguage Language { get; set; } = AppLanguage.Russian;
    public AppTheme Theme { get; set; } = AppTheme.Dark;
    public int AccentColorArgb { get; set; } = unchecked((int)0xFF7C69FF);
    public NavPanelPosition NavPanelPosition { get; set; } = NavPanelPosition.Left;
    public NavIconSize NavIconSize { get; set; } = NavIconSize.Normal;
    public List<string> NavItemOrder { get; set; } = ["servers", "settings", "config", "logs", "info"];
    public List<string> HiddenNavItems { get; set; } = [];
    public bool ShowNavTooltips { get; set; } = true;
    public bool ShowNavSeparators { get; set; } = true;
    public bool UsePanelGlass { get; set; } = true;
    public List<ProxyProfile> Profiles { get; set; } = [];

    [JsonIgnore]
    public bool Windows10TunCompatibilityMode { get; set; }
}

public sealed class ProxyProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New profile";
    public ProxyProtocol Protocol { get; set; }
    public string Server { get; set; } = "";
    public int Port { get; set; }
    public string? UserId { get; set; }
    public string? Password { get; set; }
    public string? Method { get; set; }
    public string? Flow { get; set; }
    public bool TlsEnabled { get; set; }
    public bool RealityEnabled { get; set; }
    public bool AllowInsecure { get; set; }
    public string? ServerName { get; set; }
    public string? PublicKey { get; set; }
    public string? ShortId { get; set; }
    public string? Fingerprint { get; set; }
    public string? Security { get; set; }
    public int? AlterId { get; set; }
    public string? Network { get; set; }
    public string? Host { get; set; }
    public string? Path { get; set; }
    public string? ServiceName { get; set; }
    public string? ObfsType { get; set; }
    public string? ObfsPassword { get; set; }
    public string? PrivateKey { get; set; }
    public string? PeerPublicKey { get; set; }
    public string? PreSharedKey { get; set; }
    public string? LocalAddress { get; set; }
    public string? AllowedIps { get; set; }
    public int? PersistentKeepalive { get; set; }
    public int? Mtu { get; set; }
    public string? CountryCode { get; set; }
    public string? CountryName { get; set; }
    public DateTime? CountryLookupUtc { get; set; }

    [JsonIgnore]
    public string DisplayName => $"{Name}  [{Protocol}]";
}

public enum ProxyProtocol
{
    Vless,
    Vmess,
    Trojan,
    Shadowsocks,
    Hysteria2,
    WireGuard
}

public enum NavPanelPosition
{
    Left,
    Top
}

public enum NavIconSize
{
    Compact,
    Normal,
    Large
}

public enum AppTheme
{
    Dark,
    Darker,
    Violet,
    Graphite
}

public enum AppLanguage
{
    Russian,
    English
}
