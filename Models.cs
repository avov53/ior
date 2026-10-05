using System.Text.Json.Serialization;

namespace HappBox;

public sealed class AppState
{
    [JsonIgnore]
    public NativeTunReadinessResult? NativeTunReadiness { get; set; }
    public int SchemaVersion { get; set; } = 2;
    public string SingBoxPath { get; set; } = "sing-box.exe";
    public CoreKind SelectedCore { get; set; } = CoreKind.SingBox;
    public CoreKind? RunningCore { get; set; }
    public string? RunningServerId { get; set; }
    public string? RunningServerName { get; set; }
    public ProxyProfile? RunningProfileSnapshot { get; set; }
    public string? IorCorePath { get; set; }
    public string? XrayCorePath { get; set; }
    public string BootstrapDnsServer { get; set; } = "1.1.1.1";
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
    public bool PowerSavingMode { get; set; }
    public bool ExperimentalCoreTesting { get; set; }
    public bool RealityUdp443Enabled { get; set; }
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
    public List<string> HappRoutingLinks { get; set; } = [];
    public List<HappRoutingProfile> HappRoutingProfiles { get; set; } = [];
    public string? SelectedHappRoutingProfileId { get; set; }
    public List<ConnectionWorkspaceProfile> ConnectionProfiles { get; set; } = [];
    public string? SelectedConnectionProfileId { get; set; }
    public bool AutoRefreshSubscriptions { get; set; } = true;
    public bool AutoValidateConfig { get; set; } = true;
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
    public List<string> NavItemOrder { get; set; } = ["servers", "settings", "config", "logs"];
    public List<string> HiddenNavItems { get; set; } = [];
    public bool ShowNavTooltips { get; set; } = true;
    public bool ShowNavSeparators { get; set; } = true;
    public bool UsePanelGlass { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public List<ServiceMonitorTarget> ServiceMonitorTargets { get; set; } = [];
    public List<ProxySubscription> Subscriptions { get; set; } = [];
    public List<ProxyProfile> Profiles { get; set; } = [];

    [JsonIgnore]
    public bool Windows10TunCompatibilityMode { get; set; }
}

public sealed class ServiceMonitorTarget
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Website";
    public string Url { get; set; } = "https://example.com";
    public string? PresetId { get; set; }
    public bool Visible { get; set; } = true;
}

public sealed class ProxyProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New profile";
    public ProxyProtocol Protocol { get; set; }
    public string Server { get; set; } = "";
    public int Port { get; set; }
    public string? UserId { get; set; }
    public string? Username { get; set; }
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
    public string? TunnelDns { get; set; }
    public string? Plugin { get; set; }
    public List<string> UnsupportedParameters { get; set; } = [];
    public string? CountryCode { get; set; }
    public string? CountryName { get; set; }
    public DateTime? CountryLookupUtc { get; set; }
    public long? SubscriptionUploadBytes { get; set; }
    public long? SubscriptionDownloadBytes { get; set; }
    public long? SubscriptionTotalBytes { get; set; }
    public DateTime? SubscriptionExpireUtc { get; set; }
    public string? SubscriptionId { get; set; }

    [JsonIgnore]
    public string DisplayName => $"{Name}  [{Protocol}]";
}

public sealed class HappRoutingProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Happ routing";
    public List<string> Links { get; set; } = [];
    public string? ManagedBySubscriptionId { get; set; }
    public bool NameIsCustom { get; set; }
    public RoutingProfileSource Source { get; set; } = RoutingProfileSource.Happ;
    public bool GlobalProxy { get; set; } = true;
    public List<string> ProxySites { get; set; } = [];
    public List<string> DirectSites { get; set; } = [];
    public List<string> BlockSites { get; set; } = [];
    public List<string> ProxyIp { get; set; } = [];
    public List<string> DirectIp { get; set; } = [];
    public List<string> BlockIp { get; set; } = [];
    public List<string> ProxyDomains { get; set; } = [];
    public List<string> DirectDomains { get; set; } = [];
    public List<string> BlockDomains { get; set; } = [];
    public List<string> ProxyKeywords { get; set; } = [];
    public List<string> DirectKeywords { get; set; } = [];
    public List<string> BlockKeywords { get; set; } = [];
    public string? OriginalClashYaml { get; set; }
    public List<string> ClashProxyGroups { get; set; } = [];
    public List<string> ClashUnmappedRules { get; set; } = [];
    public List<ClashRuleEntry> ClashOrderedRules { get; set; } = [];
    // Canonical ordered representation. Legacy profiles are converted on read
    // by OrderedRoutePlan without rewriting the user's saved routing profile.
    public List<OrderedRouteRule> OrderedRules { get; set; } = [];
    public RoutePlanV2? RoutePlan { get; set; }

    [JsonIgnore]
    public string DisplayName => Name;
}

public sealed class ClashRuleEntry
{
    public string Kind { get; set; } = "";
    public string Value { get; set; } = "";
    public string Target { get; set; } = "";
    public bool NoResolve { get; set; }
    public string Raw { get; set; } = "";
}

public sealed class OrderedRouteRule
{
    public string Id { get; set; } = "";
    public List<string> Parameters { get; set; } = [];
    public string Kind { get; set; } = "";
    public string Value { get; set; } = "";
    public string Target { get; set; } = "";
    public bool NoResolve { get; set; }
    public RoutingProfileSource Source { get; set; }
    public string Raw { get; set; } = "";
}

public sealed class ProxySubscription
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Subscription";
    public bool NameIsCustom { get; set; }
    public string Url { get; set; } = "";
    public string? SupportUrl { get; set; }
    public string? ProfileWebPageUrl { get; set; }
    public string? Announcement { get; set; }
    [System.Text.Json.Serialization.JsonConverter(typeof(UtcTimestampJsonConverter))]
    public DateTime? LastUpdatedUtc { get; set; }
    public string? LastError { get; set; }
    public long? LastResponseMs { get; set; }
    public int? LastImportedServers { get; set; }
    public int? LastRejectedServers { get; set; }
    public long? UploadBytes { get; set; }
    public long? DownloadBytes { get; set; }
    public long? TotalBytes { get; set; }
    public bool HasUsageInfo { get; set; }
    public DateTime? ExpireUtc { get; set; }
    public string? RoutingProfileId { get; set; }
    public bool AutoRefreshEnabled { get; set; } = true;
    public int RefreshIntervalHours { get; set; } = 12;
    public int? ServerRefreshIntervalHours { get; set; }
    public bool RefreshIntervalIsCustom { get; set; }
    [System.Text.Json.Serialization.JsonConverter(typeof(UtcTimestampJsonConverter))]
    public DateTime? NextRefreshUtc { get; set; }
}

public sealed class ConnectionWorkspaceProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Основной";
    public int ColorArgb { get; set; } = unchecked((int)0xFFB8F356);
    public string? SubscriptionId { get; set; }
    public string? RoutingProfileId { get; set; }
    public string? DefaultServerId { get; set; }
    public List<string> ConfigIds { get; set; } = [];
    public IorTrafficMode IorTrafficMode { get; set; } = IorTrafficMode.Standard;
}

public enum IorTrafficMode
{
    Standard,
    Gaming
}

public enum RoutingProfileSource
{
    Ior,
    Happ,
    Clash
}

public enum ProxyProtocol
{
    Vless,
    Vmess,
    Trojan,
    Shadowsocks,
    Hysteria2,
    WireGuard,
    Socks,
    Http
}

public enum CoreKind
{
    IorCore,
    SingBox,
    XrayCore
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
    Graphite,
    Midnight,
    RedNeon,
    Emerald,
    Amber,
    PureLight,
    IceBlue,
    WhiteRed,
    MintGlass,
    LightLavender,
    PeachCream,
    SkyGradient
}

public enum AppLanguage
{
    Russian,
    English
}
