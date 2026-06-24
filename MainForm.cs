using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO.Compression;
using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace HappBox;

public sealed class MainForm : Form
{
    private const string AppVersion = "0.1.1";
    private const string GitHubRepositoryUrl = "https://github.com/avov53/ior";
    private const string GitHubLatestReleaseApiUrl = "https://api.github.com/repos/avov53/ior/releases/latest";
    private const int SingBoxStartupTimeoutMs = 6000;
    private const int SingBoxStartupRetryDelayMs = 700;
    private const int EmLineScroll = 0x00B6;
    private const int EmGetFirstVisibleLine = 0x00CE;
    private static readonly string[] DefaultRoutingBypassSites =
    [
        "ozon.ru",
        "www.ozon.ru",
        "yandex.ru",
        "ya.ru",
        "wildberries.ru",
        "www.wildberries.ru"
    ];

    private static Color Bg = Color.FromArgb(18, 20, 20);
    private static Color NavBg = Color.FromArgb(13, 15, 15);
    private static Color PanelBg = Color.FromArgb(25, 27, 27);
    private static Color CardBg = Color.FromArgb(54, 55, 55);
    private static Color CardBg2 = Color.FromArgb(37, 39, 39);
    private static Color PreviewBg = Color.FromArgb(28, 30, 38);
    private static Color Accent = Color.FromArgb(124, 105, 255);
    private static Color Accent2 = Color.FromArgb(90, 116, 220);
    private static Color TextColor = Color.White;
    private static Color Muted = Color.FromArgb(170, 170, 178);
    private static Color Line = Color.FromArgb(77, 78, 86);
    private static readonly string[] DefaultNavItemOrder = ["servers", "settings", "config", "logs", "info"];

    private readonly string _appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ior");
    private readonly string _statePath;
    private readonly string _configPath;
    private readonly AppState _state;
    private readonly List<string> _logs = [];

    private readonly ListBox _profiles = new();
    private readonly Panel _serverContentHost = new();
    private readonly Panel _emptyStatePanel = new();
    private readonly Panel _inlineImportPanel = new();
    private readonly TextBox _inlineImportBox = new();
    private readonly HttpClient _httpClient = new();
    private readonly Label _selectedName = new();
    private readonly Label _selectedProtocol = new();
    private readonly Label _selectedServer = new();
    private readonly Label _statusLabel = new();
    private readonly Label _timerLabel = new();
    private readonly ToggleButton _powerButton = new();
    private readonly Panel _networkStatsPanel = new();
    private readonly Label _downloadSpeedValue = new();
    private readonly Label _uploadSpeedValue = new();
    private readonly Label _pingValue = new();
    private readonly Label _qualityValue = new();
    private readonly Dictionary<string, Label> _serviceStatusLabels = [];
    private readonly HashSet<string> _countryLookupsInFlight = [];
    private readonly System.Windows.Forms.Timer _connectTimer = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _trafficTimer = new() { Interval = 1000 };
    private readonly List<SideNavButton> _navButtons = [];
    private readonly ToolTip _navToolTip = CreateNavToolTip();

    private DateTime? _connectedAt;
    private bool _startupUpdatePromptShown;
    private long _lastReceivedBytes;
    private long _lastSentBytes;
    private bool _serviceCheckRunning;
    private bool _connectionTransitionInProgress;
    private DateTime _lastServiceCheckUtc = DateTime.MinValue;
    private Process? _process;
    private bool? _previousProxyEnabled;
    private string? _previousProxyServer;
    private string? _previousProxyOverride;
    private TableLayoutPanel? _contentHost;
    private TableLayoutPanel? _shellRoot;
    private Control? _serverListView;
    private Control? _connectionView;
    private TableLayoutPanel? _serverListLayout;
    private ChevronIcon? _serverExpandButton;
    private Control? _titleBar;
    private Label? _minCaptionButton;
    private Label? _maxCaptionButton;
    private Label? _closeCaptionButton;
    private SideNavButton? _serversNavButton;
    private SideNavButton? _settingsNavButton;
    private SideNavButton? _configNavButton;
    private SideNavButton? _logsNavButton;
    private SideNavButton? _infoNavButton;
    private AppView _currentView = AppView.Servers;
    private AppView _previousView = AppView.Servers;
    private bool _serversExpanded = true;
    private long? _lastLatencyMs;
    private int _healthyServiceCount;
    private int _checkedServiceCount;

    public MainForm()
    {
        Directory.CreateDirectory(_appDir);
        _statePath = Path.Combine(_appDir, "state.json");
        _configPath = Path.Combine(_appDir, "config.generated.json");
        _state = LoadState(_statePath);
        ApplyPackageCompatibilityDefaults(_state);
        ApplyAppearanceSettings();
        if (_state.SingBoxPath == "sing-box.exe" ||
            !File.Exists(_state.SingBoxPath) ||
            IsPortableBundledSingBoxPath(_state.SingBoxPath))
        {
            _state.SingBoxPath = BundledSingBoxPath();
        }

        Text = "ior";
        var workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1220, 810);
        Width = Math.Min(1120, Math.Max(920, workingArea.Width - 80));
        Height = Math.Min(810, Math.Max(620, workingArea.Height - 80));
        MinimumSize = new Size(900, 620);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        BackColor = Bg;
        Font = new Font("Segoe UI", 9.5f);
        DoubleBuffered = true;

        _connectTimer.Tick += (_, _) => UpdateConnectionTimer();
        _trafficTimer.Tick += async (_, _) =>
        {
            UpdateTrafficStats();
            await UpdatePingAndServicesAsync();
        };
        BuildUi();
        RefreshProfiles();
        RefreshMainPreview();
        ResetTrafficCounters();
        RecoverConnectionState();
        QueueCountryLookups(_state.Profiles);
        _trafficTimer.Start();
        Shown += async (_, _) => await CheckForStartupUpdateAsync();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        StopSingBox();
        SaveState();
        base.OnFormClosing(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyWindowBackdrop();
    }

    private void BuildUi()
    {
        _shellRoot = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 1,
            ColumnCount = 1,
            BackColor = Bg,
            Padding = Padding.Empty
        };
        _shellRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(_shellRoot);
        RebuildShellLayout(showCurrentView: false);
        ShowServersView();
    }

    private void RebuildShellLayout(bool showCurrentView = true)
    {
        if (_shellRoot is null) return;
        _shellRoot.SuspendLayout();
        _shellRoot.Controls.Clear();
        NormalizeNavCustomization();
        _navButtons.Clear();
        _serversNavButton = null;
        _settingsNavButton = null;
        _configNavButton = null;
        _logsNavButton = null;
        _infoNavButton = null;

        var isTop = _state.NavPanelPosition == NavPanelPosition.Top;
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = isTop ? 1 : 2,
            RowCount = isTop ? 2 : 1,
            BackColor = Bg,
            Padding = Padding.Empty
        };
        if (isTop)
        {
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, NavRailThickness()));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.Controls.Add(BuildSidebar(), 0, 0);
        }
        else
        {
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, NavRailThickness()));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(BuildSidebar(), 0, 0);
        }
        _shellRoot.Controls.Add(root, 0, 0);

        _contentHost = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Bg,
            Padding = Padding.Empty
        };
        root.Controls.Add(_contentHost, isTop ? 0 : 1, isTop ? 1 : 0);
        _shellRoot.ResumeLayout(true);
        if (showCurrentView) ShowCurrentView();
    }

    private void ResetCachedMainViews()
    {
        _serverListView?.Dispose();
        _connectionView?.Dispose();
        _serverListView = null;
        _connectionView = null;
    }

    private Control BuildTitleBar()
    {
        var bar = new Panel { Dock = DockStyle.Fill, BackColor = NavBg };
        _titleBar = bar;
        WindowButtonKind? hoveredButton = null;
        WindowButtonKind? pressedButton = null;
        bar.MouseMove += (_, e) =>
        {
            var hit = HitCaptionButton(bar, e.Location);
            if (hoveredButton == hit) return;
            hoveredButton = hit;
            bar.Cursor = hit is null ? Cursors.Default : Cursors.Hand;
            bar.Invalidate();
        };
        bar.MouseLeave += (_, _) =>
        {
            hoveredButton = null;
            pressedButton = null;
            bar.Cursor = Cursors.Default;
            bar.Invalidate();
        };
        bar.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            pressedButton = HitCaptionButton(bar, e.Location);
            if (pressedButton is null) DragWindow(bar, e);
            else bar.Invalidate();
        };
        bar.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || pressedButton is null) return;
            var released = HitCaptionButton(bar, e.Location);
            var action = pressedButton;
            pressedButton = null;
            bar.Invalidate();
            if (released != action) return;

            if (action == WindowButtonKind.Minimize) WindowState = FormWindowState.Minimized;
            else if (action == WindowButtonKind.Maximize) WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
            else Close();
        };

        var icon = new Label
        {
            Text = "i",
            Width = 20,
            Height = 22,
            Location = new Point(7, 8),
            BackColor = Accent,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter
        };
        icon.MouseDown += DragWindow;
        bar.Controls.Add(icon);

        var title = new Label
        {
            Text = $"ior {AppVersion}",
            AutoSize = true,
            Location = new Point(29, 10),
            ForeColor = TextColor,
            Font = new Font("Segoe UI", 9.5f)
        };
        title.MouseDown += DragWindow;
        bar.Controls.Add(title);

        _closeCaptionButton = CaptionLabel("X", () => Close(), closeButton: true);
        _maxCaptionButton = CaptionLabel("□", () => WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized);
        _minCaptionButton = CaptionLabel("_", () => WindowState = FormWindowState.Minimized);
        bar.Controls.Add(_minCaptionButton);
        bar.Controls.Add(_maxCaptionButton);
        bar.Controls.Add(_closeCaptionButton);
        LayoutWindowCaptionButtons();
        return bar;
    }

    private void LayoutWindowCaptionButtons()
    {
        if (_titleBar is null || _minCaptionButton is null || _maxCaptionButton is null || _closeCaptionButton is null) return;
        var width = _titleBar.ClientSize.Width;
        if (width <= 0) return;

        _closeCaptionButton.Location = new Point(width - 46, 0);
        _maxCaptionButton.Location = new Point(width - 92, 0);
        _minCaptionButton.Location = new Point(width - 138, 0);
        _closeCaptionButton.BringToFront();
        _maxCaptionButton.BringToFront();
        _minCaptionButton.BringToFront();
    }

    private static Label CaptionLabel(string text, Action action, bool closeButton = false)
    {
        var label = new Label
        {
            Text = text,
            Width = 46,
            Height = 42,
            BackColor = NavBg,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", closeButton ? 12f : 11f, FontStyle.Regular),
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand
        };
        label.MouseEnter += (_, _) => label.BackColor = closeButton ? Color.FromArgb(196, 43, 28) : Color.FromArgb(35, 37, 37);
        label.MouseLeave += (_, _) => label.BackColor = NavBg;
        label.Click += (_, _) => action();
        return label;
    }

    private static WindowButtonKind? HitCaptionButton(Control bar, Point point)
    {
        foreach (var kind in new[] { WindowButtonKind.Minimize, WindowButtonKind.Maximize, WindowButtonKind.Close })
        {
            if (CaptionButtonBounds(bar, kind).Contains(point)) return kind;
        }

        return null;
    }

    private static Rectangle CaptionButtonBounds(Control bar, WindowButtonKind kind)
    {
        var offset = kind switch
        {
            WindowButtonKind.Close => 1,
            WindowButtonKind.Maximize => 2,
            _ => 3
        };
        return new Rectangle(bar.Width - (46 * offset), 0, 46, 42);
    }

    private static void DrawCaptionButton(Graphics g, Rectangle rect, WindowButtonKind kind, bool hover, bool pressed)
    {
        var bg = NavBg;
        if (hover) bg = kind == WindowButtonKind.Close ? Color.FromArgb(196, 43, 28) : Color.FromArgb(35, 37, 37);
        if (pressed) bg = kind == WindowButtonKind.Close ? Color.FromArgb(160, 32, 22) : Color.FromArgb(45, 47, 47);

        using (var fill = new SolidBrush(bg))
        {
            g.FillRectangle(fill, rect);
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using var pen = new Pen(Color.White, 1.7f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        var cx = rect.Left + rect.Width / 2;
        var cy = rect.Top + rect.Height / 2;
        switch (kind)
        {
            case WindowButtonKind.Minimize:
                g.DrawLine(pen, cx - 6, cy + 5, cx + 6, cy + 5);
                break;
            case WindowButtonKind.Maximize:
                g.DrawRectangle(pen, cx - 6, cy - 5, 12, 10);
                break;
            case WindowButtonKind.Close:
                g.DrawLine(pen, cx - 5, cy - 5, cx + 5, cy + 5);
                g.DrawLine(pen, cx + 5, cy - 5, cx - 5, cy + 5);
                break;
        }
    }

    private static Control TitleButton(WindowButtonKind kind, Action action)
    {
        var button = new WindowCaptionButton
        {
            Kind = kind,
            Width = 46,
            Height = 42,
            Cursor = Cursors.Hand,
            TabStop = false,
            Margin = Padding.Empty
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static Label HeaderIcon(string text, Action? action = null)
    {
        var lbl = new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(164, 158, 221),
            Font = new Font("Segoe UI Symbol", text.Length > 1 ? 14f : 17f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand
        };
        lbl.MouseEnter += (_, _) => lbl.ForeColor = Color.White;
        lbl.MouseLeave += (_, _) => lbl.ForeColor = Color.FromArgb(164, 158, 221);
        if (action != null) lbl.Click += (_, _) => action();
        return lbl;
    }

    private static Button HeaderButton(string text, Action action, Color? foreColor = null)
    {
        var button = new RoundedButton
        {
            Text = text,
            Radius = 7,
            Dock = DockStyle.Fill,
            BackColor = CardBg,
            BorderColor = CardBg,
            ForeColor = foreColor ?? Color.FromArgb(164, 158, 221),
            Font = new Font("Segoe UI Symbol", text.Length > 1 ? 13f : 15f, FontStyle.Regular),
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand,
            TabStop = false,
            Margin = Padding.Empty
        };
        button.MouseEnter += (_, _) => button.BackColor = Color.FromArgb(74, 75, 75);
        button.MouseLeave += (_, _) => button.BackColor = CardBg;
        button.Click += (_, _) => action();
        return button;
    }

    private Control BuildSidebar()
    {
        NormalizeNavCustomization();
        var isTop = _state.NavPanelPosition == NavPanelPosition.Top;
        var itemSize = NavItemSize();
        var visibleItems = _state.NavItemOrder
            .Where(id => !_state.HiddenNavItems.Contains(id, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var separatorCount = _state.ShowNavSeparators
            ? 1 + (visibleItems.Contains("info", StringComparer.OrdinalIgnoreCase) ? 1 : 0)
            : 0;
        var slotCount = visibleItems.Count + 2 + separatorCount;
        var nav = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = isTop ? 1 : slotCount + 1,
            ColumnCount = isTop ? slotCount + 1 : 1,
            BackColor = NavBg,
            Padding = isTop ? new Padding(18, 8, 18, 8) : new Padding(0, 18, 0, 16)
        };
        if (isTop)
        {
            nav.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            for (var i = 0; i < slotCount; i++)
            {
                nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, itemSize + 2));
            }
            nav.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        }
        else
        {
            nav.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var i = 0; i < slotCount; i++)
            {
                nav.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 0 ? itemSize + 6 : itemSize + 2));
            }
            nav.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        }

        var index = 0;
        AddNavButton(nav, SideButton("→", NavigateBack, T("nav.back"), selectOnClick: false), index++, isTop);
        AddNavButton(nav, SideButton("+", () => { NavigateTo(AppView.Servers); ShowInlineImport(); }, T("nav.import"), selectOnClick: false), index++, isTop);
        if (_state.ShowNavSeparators) AddNavButton(nav, new NavSeparator { PanelPosition = _state.NavPanelPosition }, index++, isTop);
        foreach (var id in visibleItems)
        {
            if (_state.ShowNavSeparators && id == "info")
            {
                AddNavButton(nav, new NavSeparator { PanelPosition = _state.NavPanelPosition }, index++, isTop);
            }
            AddNavButton(nav, BuildNavItemButton(id), index++, isTop);
        }
        return nav;
    }

    private int NavItemSize()
    {
        return _state.NavIconSize switch
        {
            NavIconSize.Compact => 46,
            NavIconSize.Large => 66,
            _ => 56
        };
    }

    private int NavRailThickness()
    {
        return _state.NavIconSize switch
        {
            NavIconSize.Compact => 66,
            NavIconSize.Large => 92,
            _ => 78
        };
    }

    private static bool GlassSupported()
    {
        return Environment.OSVersion.Version.Major >= 10;
    }

    private void ApplyWindowBackdrop()
    {
        if (!IsHandleCreated || !_state.UsePanelGlass || !GlassSupported()) return;
        try
        {
            var backdrop = 2;
            DwmSetWindowAttribute(Handle, 38, ref backdrop, sizeof(int));
        }
        catch
        {
            // Older Windows 10 builds do not expose the backdrop attribute.
        }
    }

    private static void AddNavButton(TableLayoutPanel nav, Control button, int index, bool isTop)
    {
        if (isTop) nav.Controls.Add(button, index, 0);
        else nav.Controls.Add(button, 0, index);
    }

    private SideNavButton BuildNavItemButton(string id)
    {
        var (icon, tooltip, view) = NavItemInfo(id);
        var button = (SideNavButton)SideButton(icon, () => NavigateTo(view), NavItemTitle(id), active: _currentView == view);
        switch (id)
        {
            case "servers":
                _serversNavButton = button;
                break;
            case "settings":
                _settingsNavButton = button;
                break;
            case "config":
                _configNavButton = button;
                break;
            case "logs":
                _logsNavButton = button;
                break;
            case "info":
                _infoNavButton = button;
                break;
        }

        return button;
    }

    private static (string Icon, string Tooltip, AppView View) NavItemInfo(string id)
    {
        return id switch
        {
            "servers" => ("◎", "Servers", AppView.Servers),
            "settings" => ("⚙", "Settings", AppView.Settings),
            "config" => ("config-box", "Config", AppView.Config),
            "logs" => ("logs-list", "Logs", AppView.Logs),
            "info" => ("i", "Info", AppView.Info),
            _ => ("◎", "Servers", AppView.Servers)
        };
    }

    private string NavItemTitle(string id)
    {
        return id switch
        {
            "servers" => T("nav.servers"),
            "settings" => T("nav.settings"),
            "config" => T("nav.config"),
            "logs" => T("nav.logs"),
            "info" => T("nav.info"),
            _ => T("nav.servers")
        };
    }

    private string T(string key)
    {
        var ru = _state.Language == AppLanguage.Russian;
        return key switch
        {
            "nav.servers" => ru ? "Серверы" : "Servers",
            "nav.settings" => ru ? "Настройки" : "Settings",
            "nav.config" => ru ? "Конфиг" : "Config",
            "nav.logs" => ru ? "Логи" : "Logs",
            "nav.info" => ru ? "Информация" : "Info",
            "nav.back" => ru ? "Назад" : "Back",
            "nav.import" => ru ? "Импорт" : "Import",
            "servers.title" => ru ? "Серверы" : "Servers",
            "servers.list" => ru ? "Список серверов" : "Server list",
            "servers.empty" => ru ? "Серверов пока нет" : "No servers yet",
            "servers.paste" => ru ? "Вставить из буфера" : "Paste from clipboard",
            "servers.enter" => ru ? "+ Ввести" : "+ Enter",
            "servers.add" => ru ? "Добавить" : "Add",
            "servers.placeholder" => ru ? "Вставьте ссылку или подписку" : "Paste a link or subscription",
            "servers.delete.all" => ru ? "Удалить все серверы" : "Delete all servers",
            "servers.rename" => ru ? "Переименовать" : "Rename",
            "servers.delete" => ru ? "Удалить" : "Delete",
            "servers.delete.confirm" => ru ? "Удалить этот сервер из списка?" : "Delete this server from the list?",
            "servers.server" => ru ? "Сервер" : "Server",
            "servers.name" => ru ? "Имя сервера" : "Server name",
            "servers.none" => ru ? "Нет выбранного сервера" : "No server selected",
            "servers.add.subscription" => ru ? "Добавьте подписку или ссылку" : "Add a subscription or link",
            "settings.title" => ru ? "Настройки" : "Settings",
            "settings.core" => ru ? "Основное" : "Core",
            "settings.connection" => ru ? "Подключение" : "Connection",
            "settings.dns" => "DNS",
            "settings.routing" => ru ? "Маршрутизация" : "Routing",
            "settings.advanced" => ru ? "Расширенные" : "Advanced",
            "settings.customization" => ru ? "Кастомизация" : "Customization",
            "settings.browse" => ru ? "Обзор" : "Browse",
            "settings.save" => ru ? "Сохранить" : "Save",
            "settings.mode" => ru ? "Режим" : "Mode",
            "settings.proxy" => ru ? "Прокси" : "Proxy",
            "settings.proxy.port" => ru ? "Порт прокси" : "Proxy port",
            "settings.tun.address" => ru ? "Адрес TUN" : "TUN address",
            "settings.dns.server" => ru ? "DNS-сервер" : "DNS server",
            "settings.strategy" => ru ? "Стратегия" : "Strategy",
            "settings.proxy.mode.only" => ru ? "Только для режима прокси" : "Proxy mode only",
            "settings.excluded.sites" => ru ? "Исключённые сайты" : "Excluded sites",
            "settings.proxied.sites" => ru ? "Проксируемые сайты" : "Proxied sites",
            "settings.route" => ru ? "Маршрут" : "Route",
            "settings.log.level" => ru ? "Уровень логов" : "Log level",
            "settings.sniff" => ru ? "Анализировать трафик" : "Sniff traffic",
            "settings.auto.route" => ru ? "Автомаршрут" : "Auto route",
            "settings.strict.route" => ru ? "Строгий маршрут" : "Strict route",
            "settings.system.stack" => ru ? "Системный стек" : "System stack",
            "settings.allow.insecure" => ru ? "Разрешить небезопасные сертификаты" : "Allow insecure certificates",
            "settings.system.proxy" => ru ? "Включать системный прокси Windows в режиме Proxy" : "Set Windows system proxy in Proxy mode",
            "custom.position" => ru ? "Положение" : "Position",
            "custom.theme" => ru ? "Тема" : "Theme",
            "custom.language" => ru ? "Язык" : "Language",
            "custom.accent" => ru ? "Акцент" : "Accent",
            "custom.icon.size" => ru ? "Размер иконок" : "Icon size",
            "custom.visible.tabs" => ru ? "Видимые вкладки" : "Visible tabs",
            "custom.options" => ru ? "Опции" : "Options",
            "custom.tooltips" => ru ? "Подсказки" : "Tooltips",
            "custom.separators" => ru ? "Разделители" : "Separators",
            "custom.glass" => ru ? "Стеклянная панель" : "Glass panel",
            "custom.pick" => ru ? "Выбрать" : "Pick",
            "custom.reset" => ru ? "Сброс" : "Reset",
            "custom.export" => ru ? "Экспорт" : "Export",
            "custom.import" => ru ? "Импорт" : "Import",
            "custom.title" => ru ? "Настройка панели" : "Panel customization",
            "custom.hint" => ru ? "Перетаскивайте иконки вкладок внутри предпросмотра." : "Drag the tab icons inside the preview.",
            "config.title" => ru ? "Сгенерированный конфиг" : "Generated config",
            "config.write" => ru ? "Записать конфиг" : "Write config file",
            "config.select.profile" => ru ? "Сначала импортируйте и выберите профиль." : "Import and select a profile first.",
            "logs.title" => ru ? "Логи" : "Logs",
            "info.title" => ru ? "Информация" : "Information",
            "info.current.version" => ru ? "Текущая версия" : "Current version",
            "update.checking" => ru ? "Проверка обновлений..." : "Checking for updates...",
            "update.none" => ru ? "Обновлений пока нет или релизы GitHub ещё не опубликованы." : "No updates yet or GitHub releases have not been published.",
            "update.available" => ru ? "Доступна новая версия" : "New version available",
            "update.latest" => ru ? "Установлена последняя версия." : "The latest version is installed.",
            "update.check.failed" => ru ? "Не удалось проверить обновления" : "Could not check for updates",
            "update.check.network" => ru ? "нет доступа к GitHub или сбой SSL/сертификата. Подробности в логах." : "GitHub is unreachable or SSL/certificate validation failed. Check logs for details.",
            "update.button" => ru ? "Обновить" : "Update",
            "update.downloading" => ru ? "Скачивание обновления..." : "Downloading update...",
            "update.preparing" => ru ? "Подготовка файлов..." : "Preparing files...",
            "update.exe.missing" => ru ? "В архиве обновления не найден ior.exe." : "ior.exe was not found in the update archive.",
            "update.install.failed" => ru ? "Не удалось установить обновление" : "Could not install update",
            "import.title" => ru ? "Добавить сервер" : "Add server",
            "import.links" => ru ? "Ссылки или подписки" : "Links or subscriptions",
            "import.placeholder" => ru ? "vless://, vmess://, ss://, hysteria2:// или ссылка подписки" : "vless://, vmess://, ss://, hysteria2:// or a subscription link",
            "import.download.failed" => ru ? "Не удалось скачать подписку" : "Cannot download subscription",
            "import.none" => ru ? "Поддерживаемые профили не найдены." : "No supported profiles found.",
            "import.clipboard.empty" => ru ? "Буфер обмена пуст." : "Clipboard is empty.",
            "import.clipboard" => ru ? "Буфер обмена" : "Clipboard",
            "import.title.short" => ru ? "Импорт" : "Import",
            "common.cancel" => ru ? "Отмена" : "Cancel",
            "common.ok" => "OK",
            "start.title" => ru ? "Запуск" : "Start",
            "start.select.profile" => ru ? "Сначала импортируйте и выберите профиль." : "Import and select a profile first.",
            "connection.failed" => ru ? "Ошибка подключения" : "Connection failed",
            "connection.not.ready" => ru ? "sing-box не стал готовым. Подробности смотрите в логах." : "sing-box did not become ready. Check logs for details.",
            "singbox.cannot.start" => ru ? "Не удалось запустить sing-box" : "Cannot start sing-box",
            "config.check.failed" => ru ? "Проверка конфига не прошла" : "Config check failed",
            "config.check.timeout" => ru ? "Проверка sing-box превысила время ожидания." : "sing-box check timed out.",
            "config.rejected" => ru ? "sing-box отклонил сгенерированный конфиг. Подробности смотрите в логах." : "sing-box rejected the generated config. Check logs for details.",
            "config.check.cannot.run" => ru ? "Не удалось запустить проверку sing-box" : "Cannot run sing-box check",
            "ping.title" => ru ? "Пинг" : "Ping",
            "ping.failed" => ru ? "Пинг не удался" : "Ping failed",
            "metrics.download" => ru ? "Вход" : "Download",
            "metrics.upload" => ru ? "Исход" : "Upload",
            "metrics.ping" => ru ? "Пинг" : "Ping",
            "metrics.quality" => ru ? "Качество" : "Quality",
            "quality.good" => ru ? "Хорошее" : "Good",
            "quality.fair" => ru ? "Среднее" : "Fair",
            "quality.poor" => ru ? "Плохое" : "Poor",
            "status.connected" => ru ? "ПОДКЛЮЧЁН" : "CONNECTED",
            "status.disconnected" => ru ? "НЕ ПОДКЛЮЧЕН" : "DISCONNECTED",
            "dialog.singbox.title" => ru ? "Выберите sing-box.exe" : "Select sing-box.exe",
            "dialog.exe.files" => ru ? "Исполняемые файлы" : "Executable files",
            "dialog.all.files" => ru ? "Все файлы" : "All files",
            "layout.export.title" => ru ? "Экспорт настроек интерфейса" : "Export layout settings",
            "layout.import.title" => ru ? "Импорт настроек интерфейса" : "Import layout settings",
            "layout.import.error" => ru ? "Импорт интерфейса" : "Import layout",
            "layout.filter" => ru ? "Настройки интерфейса (*.json)|*.json|Все файлы (*.*)|*.*" : "Layout settings (*.json)|*.json|All files (*.*)|*.*",
            _ => key
        };
    }

    private void NormalizeNavCustomization()
    {
        _state.NavItemOrder = NormalizeNavItemOrder(_state.NavItemOrder);
        _state.HiddenNavItems = NormalizeHiddenNavItems(_state.HiddenNavItems);
        if (!Enum.IsDefined(_state.NavIconSize)) _state.NavIconSize = NavIconSize.Normal;
        if (!Enum.IsDefined(_state.Theme)) _state.Theme = AppTheme.Dark;
        if (!Enum.IsDefined(_state.Language)) _state.Language = AppLanguage.Russian;
    }

    private static List<string> NormalizeNavItemOrder(IEnumerable<string>? order)
    {
        var result = new List<string>();
        var known = DefaultNavItemOrder.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (order is not null)
        {
            foreach (var item in order)
            {
                var id = item.Trim().ToLowerInvariant();
                if (known.Contains(id) && !result.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(id);
                }
            }
        }

        foreach (var id in DefaultNavItemOrder)
        {
            if (!result.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(id);
            }
        }

        return result;
    }

    private static List<string> NormalizeHiddenNavItems(IEnumerable<string>? hidden)
    {
        var result = new List<string>();
        var known = DefaultNavItemOrder.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (hidden is null) return result;

        foreach (var item in hidden)
        {
            var id = item.Trim().ToLowerInvariant();
            if (id == "settings") continue;
            if (known.Contains(id) && !result.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(id);
            }
        }

        return result;
    }

    private void ApplyAppearanceSettings()
    {
        Accent = Color.FromArgb(_state.AccentColorArgb);
        if (Accent.A == 0) Accent = Color.FromArgb(124, 105, 255);
        Accent2 = Blend(Accent, Color.White, 0.24f);

        (Bg, NavBg, PanelBg, CardBg, CardBg2, PreviewBg, Muted, Line) = _state.Theme switch
        {
            AppTheme.Darker => (
                Color.FromArgb(8, 9, 10),
                Color.FromArgb(5, 6, 7),
                Color.FromArgb(15, 16, 18),
                Color.FromArgb(35, 36, 39),
                Color.FromArgb(20, 21, 24),
                Color.FromArgb(15, 16, 22),
                Color.FromArgb(154, 157, 166),
                Color.FromArgb(54, 56, 64)),
            AppTheme.Violet => (
                Color.FromArgb(22, 18, 30),
                Color.FromArgb(16, 12, 25),
                Color.FromArgb(30, 25, 42),
                Color.FromArgb(58, 48, 76),
                Color.FromArgb(38, 31, 52),
                Color.FromArgb(34, 27, 49),
                Color.FromArgb(190, 180, 205),
                Color.FromArgb(85, 75, 103)),
            AppTheme.Graphite => (
                Color.FromArgb(22, 23, 24),
                Color.FromArgb(17, 18, 19),
                Color.FromArgb(30, 31, 33),
                Color.FromArgb(55, 56, 59),
                Color.FromArgb(39, 40, 43),
                Color.FromArgb(32, 34, 39),
                Color.FromArgb(178, 181, 188),
                Color.FromArgb(78, 80, 86)),
            _ => (
                Color.FromArgb(18, 20, 20),
                Color.FromArgb(13, 15, 15),
                Color.FromArgb(25, 27, 27),
                Color.FromArgb(54, 55, 55),
                Color.FromArgb(37, 39, 39),
                Color.FromArgb(28, 30, 38),
                Color.FromArgb(170, 170, 178),
                Color.FromArgb(77, 78, 86))
        };

        TextColor = Color.White;
    }

    private static Color Blend(Color from, Color to, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            (int)(from.A + (to.A - from.A) * amount),
            (int)(from.R + (to.R - from.R) * amount),
            (int)(from.G + (to.G - from.G) * amount),
            (int)(from.B + (to.B - from.B) * amount));
    }

    private Control BuildServerList()
    {
        var area = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            BackColor = Bg,
            Padding = new Padding(31, 28, 39, 26)
        };
        area.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        area.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));
        area.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        area.Controls.Add(new Label
        {
            Text = T("servers.title"),
            Dock = DockStyle.Fill,
            ForeColor = TextColor,
            Font = new Font("Segoe UI", 24f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        var serverCard = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = CardBg2,
            BorderColor = CardBg2,
            Radius = 12,
            Padding = new Padding(3),
            Margin = new Padding(0, 16, 0, 0)
        };
        var serverLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            BackColor = Color.Transparent
        };
        _serverListLayout = serverLayout;
        serverLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        serverLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var serverHeader = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            BackColor = CardBg,
            Padding = new Padding(18, 0, 16, 0),
            Margin = new Padding(0, 0, 0, 1)
        };
        serverHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 34));
        serverHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        serverHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        serverHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
        _serverExpandButton = new ChevronIcon { Dock = DockStyle.Fill, Expanded = _serversExpanded };
        _serverExpandButton.Click += (_, _) => SetServersExpanded(!_serversExpanded);
        serverHeader.Controls.Add(_serverExpandButton, 0, 0);
        var serverTitle = new Label
        {
            Text = T("servers.list"),
            Dock = DockStyle.Fill,
            ForeColor = TextColor,
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Cursor = Cursors.Hand
        };
        serverTitle.Click += (_, _) => SetServersExpanded(!_serversExpanded);
        serverHeader.Controls.Add(serverTitle, 1, 0);
        serverHeader.Controls.Add(HeaderIcon("⟳", () => RefreshProfiles()), 2, 0);
        var serverMenuButton = HeaderIcon("⋯");
        serverMenuButton.Click += (_, _) => ShowServersMenu(serverMenuButton);
        serverHeader.Controls.Add(serverMenuButton, 3, 0);
        serverLayout.Controls.Add(serverHeader, 0, 0);

        _profiles.Dock = DockStyle.Fill;
        _profiles.BorderStyle = BorderStyle.None;
        _profiles.BackColor = CardBg2;
        _profiles.ForeColor = TextColor;
        _profiles.DrawMode = DrawMode.OwnerDrawFixed;
        _profiles.ItemHeight = 72;
        _profiles.DrawItem += DrawProfileItem;
        _profiles.MouseDown += ProfilesMouseDown;
        _profiles.SelectedIndexChanged += (_, _) =>
        {
            if (_profiles.SelectedItem is ProxyProfile profile) _state.SelectedProfileId = profile.Id;
            RefreshMainPreview();
            SaveState();
        };
        _serverContentHost.Dock = DockStyle.Fill;
        _serverContentHost.BackColor = CardBg2;
        _serverContentHost.Padding = new Padding(1, 0, 1, 1);
        _serverContentHost.Controls.Add(_profiles);
        _serverContentHost.Controls.Add(BuildEmptyState());
        _serverContentHost.Controls.Add(BuildInlineImportPanel());
        serverLayout.Controls.Add(_serverContentHost, 0, 1);
        serverCard.Controls.Add(serverLayout);
        area.Controls.Add(serverCard, 0, 2);
        return area;
    }

    private Control BuildEmptyState()
    {
        _emptyStatePanel.Dock = DockStyle.Fill;
        _emptyStatePanel.BackColor = CardBg2;
        _emptyStatePanel.Visible = false;

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            BackColor = CardBg2,
            Padding = new Padding(26)
        };
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));

        var hint = new Label
        {
            Text = T("servers.empty"),
            Dock = DockStyle.Fill,
            ForeColor = Muted,
            Font = new Font("Segoe UI", 12f),
            TextAlign = ContentAlignment.MiddleCenter
        };
        content.Controls.Add(hint, 0, 0);

        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            BackColor = CardBg2,
            Padding = new Padding(0, 18, 0, 18)
        };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 174));
        var pasteButton = MainButton(T("servers.paste"), ImportFromClipboard, 0);
        pasteButton.Dock = DockStyle.Fill;
        var manualButton = MainButton(T("servers.enter"), ShowInlineImport, 164, true);
        manualButton.Dock = DockStyle.Fill;
        actions.Controls.Add(pasteButton, 0, 0);
        actions.Controls.Add(manualButton, 1, 0);
        content.Controls.Add(actions, 0, 1);

        _emptyStatePanel.Controls.Add(content);
        return _emptyStatePanel;
    }

    private Control BuildInlineImportPanel()
    {
        _inlineImportPanel.Dock = DockStyle.Bottom;
        _inlineImportPanel.Height = 0;
        _inlineImportPanel.BackColor = Color.Transparent;
        _inlineImportPanel.Visible = false;
        _inlineImportPanel.Padding = new Padding(14);

        var close = new RoundedButton
        {
            Text = "X",
            Radius = 6,
            Width = 30,
            Height = 28,
            BackColor = CardBg,
            BorderColor = CardBg,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Location = new Point(_inlineImportPanel.Width - 42, 10)
        };
        close.Click += (_, _) => HideInlineImport();
        _inlineImportPanel.Resize += (_, _) => close.Location = new Point(_inlineImportPanel.Width - 42, 10);

        _inlineImportBox.Multiline = true;
        _inlineImportBox.BorderStyle = BorderStyle.None;
        _inlineImportBox.BackColor = Color.FromArgb(43, 44, 44);
        _inlineImportBox.ForeColor = Color.White;
        _inlineImportBox.PlaceholderText = T("servers.placeholder");
        _inlineImportBox.Location = new Point(14, 46);
        _inlineImportBox.Width = 300;
        _inlineImportBox.Height = 76;
        _inlineImportBox.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        _inlineImportPanel.Resize += (_, _) => _inlineImportBox.Width = Math.Max(120, _inlineImportPanel.Width - 178);

        var add = MainButton(T("servers.add"), async () =>
        {
            if (await ImportProfilesFromTextAsync(_inlineImportBox.Text))
            {
                _inlineImportBox.Clear();
                HideInlineImport();
            }
        }, 120);
        add.Location = new Point(_inlineImportPanel.Width - 146, 70);
        add.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        _inlineImportPanel.Resize += (_, _) => add.Location = new Point(_inlineImportPanel.Width - 146, 70);
        _inlineImportPanel.Controls.Add(close);
        _inlineImportPanel.Controls.Add(_inlineImportBox);
        _inlineImportPanel.Controls.Add(add);

        return _inlineImportPanel;
    }

    private Control BuildConnectionPanel()
    {
        var area = new ConnectionPanel
        {
            Dock = DockStyle.Fill,
            BackColor = PreviewBg,
            Padding = new Padding(42)
        };

        _powerButton.IsOn = false;
        _powerButton.Width = 148;
        _powerButton.Height = 62;
        _powerButton.Cursor = Cursors.Hand;
        _powerButton.Click += (_, _) => ToggleConnection();

        _statusLabel.Text = T("status.disconnected");
        _statusLabel.ForeColor = Color.FromArgb(188, 188, 196);
        _statusLabel.Font = new Font("Segoe UI", 10f);
        _statusLabel.TextAlign = ContentAlignment.MiddleCenter;
        _statusLabel.AutoSize = false;
        _statusLabel.BackColor = Color.Transparent;
        _statusLabel.Width = 240;
        _statusLabel.Height = 24;

        _timerLabel.Text = "00:00:00";
        _timerLabel.ForeColor = TextColor;
        _timerLabel.Font = new Font("Segoe UI", 10.5f);
        _timerLabel.TextAlign = ContentAlignment.MiddleCenter;
        _timerLabel.AutoSize = false;
        _timerLabel.BackColor = Color.Transparent;
        _timerLabel.Width = 136;
        _timerLabel.Height = 24;
        _timerLabel.Visible = false;

        _selectedName.ForeColor = TextColor;
        _selectedName.Font = new Font("Segoe UI", 12f);
        _selectedName.TextAlign = ContentAlignment.MiddleCenter;
        _selectedName.AutoSize = false;
        _selectedName.Width = 360;
        _selectedName.Height = 34;

        _selectedProtocol.ForeColor = Muted;
        _selectedProtocol.Font = new Font("Segoe UI", 9.5f);
        _selectedProtocol.TextAlign = ContentAlignment.MiddleCenter;
        _selectedProtocol.AutoSize = false;
        _selectedProtocol.Width = 360;
        _selectedProtocol.Height = 28;

        _selectedServer.ForeColor = Muted;
        _selectedServer.Font = new Font("Segoe UI", 9.5f);
        _selectedServer.TextAlign = ContentAlignment.MiddleCenter;
        _selectedServer.AutoSize = false;
        _selectedServer.Width = 360;
        _selectedServer.Height = 28;

        var modeSwitch = new ModeSwitchControl
        {
            Width = 210,
            Height = 48,
            IsTun = _state.TunEnabled,
            ProxyText = T("settings.proxy")
        };
        modeSwitch.ModeChanged += (_, tunEnabled) =>
        {
            ApplyConnectionMode(tunEnabled);
            modeSwitch.IsTun = _state.TunEnabled;
        };

        BuildNetworkStatsPanel();

        area.Resize += (_, _) => LayoutConnectionPanel(area, modeSwitch);
        area.Controls.Add(_powerButton);
        area.Controls.Add(_statusLabel);
        area.Controls.Add(_timerLabel);
        area.Controls.Add(_selectedName);
        area.Controls.Add(_selectedProtocol);
        area.Controls.Add(_selectedServer);
        area.Controls.Add(_networkStatsPanel);
        area.Controls.Add(modeSwitch);
        LayoutConnectionPanel(area, modeSwitch);
        return area;
    }

    private void LayoutConnectionPanel(Control area, Control modeSwitch)
    {
        var cx = area.ClientSize.Width / 2;
        var h = area.ClientSize.Height;
        var statsWidth = Math.Max(430, Math.Min(650, area.ClientSize.Width - 32));
        if (_networkStatsPanel.Width != statsWidth)
        {
            _networkStatsPanel.Width = statsWidth;
        }

        _powerButton.Location = new Point(cx - _powerButton.Width / 2, h / 2 - 120);
        _statusLabel.Location = new Point(cx - _statusLabel.Width / 2, h / 2 - 40);
        _timerLabel.Location = new Point(cx - _timerLabel.Width / 2, h / 2 - 15);
        _selectedName.Location = new Point(cx - _selectedName.Width / 2, h - 258);
        _selectedProtocol.Location = new Point(cx - _selectedProtocol.Width / 2, h - 226);
        _selectedServer.Location = new Point(cx - _selectedServer.Width / 2, h - 202);
        _networkStatsPanel.Location = new Point(cx - _networkStatsPanel.Width / 2, h - 168);
        modeSwitch.Location = new Point(cx - modeSwitch.Width / 2, h - 60);
    }

    private void BuildNetworkStatsPanel()
    {
        _networkStatsPanel.Width = 650;
        _networkStatsPanel.Height = 96;
        _networkStatsPanel.BackColor = Color.Transparent;

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 2,
            BackColor = Color.Transparent
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        grid.Controls.Add(BuildMetricCard(T("metrics.download"), _downloadSpeedValue), 0, 0);
        grid.Controls.Add(BuildMetricCard(T("metrics.upload"), _uploadSpeedValue), 1, 0);
        grid.Controls.Add(BuildMetricCard(T("metrics.ping"), _pingValue), 2, 0);
        grid.Controls.Add(BuildMetricCard(T("metrics.quality"), _qualityValue), 3, 0);
        grid.Controls.Add(BuildServiceCard("Discord"), 0, 1);
        grid.Controls.Add(BuildServiceCard("YouTube"), 1, 1);
        grid.Controls.Add(BuildServiceCard("Telegram"), 2, 1);

        _downloadSpeedValue.Text = "0 KB/s";
        _uploadSpeedValue.Text = "0 KB/s";
        _pingValue.Text = "-";
        _qualityValue.Text = "-";

        _networkStatsPanel.Controls.Clear();
        _networkStatsPanel.Controls.Add(grid);
    }

    private Control BuildMetricCard(string title, Label value)
    {
        var card = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            Radius = 7,
            BackColor = Color.FromArgb(38, 40, 46),
            BorderColor = Color.FromArgb(62, 64, 72),
            Margin = new Padding(4),
            Padding = new Padding(8, 4, 8, 4)
        };
        var titleLabel = new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = 16,
            ForeColor = Muted,
            Font = new Font("Segoe UI", 7.8f),
            TextAlign = ContentAlignment.MiddleLeft
        };
        value.Dock = DockStyle.Fill;
        value.ForeColor = TextColor;
        value.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        value.TextAlign = ContentAlignment.MiddleLeft;
        card.Controls.Add(value);
        card.Controls.Add(titleLabel);
        return card;
    }

    private Control BuildServiceCard(string serviceName)
    {
        var value = new Label
        {
            Text = "-",
            Dock = DockStyle.Right,
            Width = 18,
            ForeColor = Muted,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter
        };
        _serviceStatusLabels[serviceName] = value;

        var card = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            Radius = 7,
            BackColor = Color.FromArgb(38, 40, 46),
            BorderColor = Color.FromArgb(62, 64, 72),
            Margin = new Padding(3),
            Padding = new Padding(5, 4, 5, 4)
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18));
        var name = new Label
        {
            Text = serviceName,
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(218, 220, 228),
            Font = new Font("Segoe UI", 8.0f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        layout.Controls.Add(name, 0, 0);
        layout.Controls.Add(value, 1, 0);
        card.Controls.Add(layout);
        return card;
    }

    private Control SideButton(string text, Action action, string tooltip, bool active = false, bool selectOnClick = true)
    {
        var button = new SideNavButton
        {
            Dock = DockStyle.Fill,
            IconText = text,
            Active = active,
            PanelPosition = _state.NavPanelPosition,
            IconScale = _state.NavIconSize switch
            {
                NavIconSize.Compact => 0.9f,
                NavIconSize.Large => 1.15f,
                _ => 1f
            },
            Cursor = Cursors.Hand,
            Margin = new Padding(0, 2, 4, 2)
        };
        _navButtons.Add(button);
        button.Click += (_, _) =>
        {
            if (selectOnClick) SetActiveSideButton(button);
            action();
        };
        if (_state.ShowNavTooltips) _navToolTip.SetToolTip(button, tooltip);
        return button;
    }

    private static ToolTip CreateNavToolTip()
    {
        var toolTip = new ToolTip
        {
            OwnerDraw = true,
            InitialDelay = 450,
            ReshowDelay = 80,
            AutoPopDelay = 4000,
            ShowAlways = true,
            BackColor = CardBg2,
            ForeColor = TextColor
        };

        toolTip.Popup += (_, e) =>
        {
            using var font = new Font("Segoe UI", 8.5f, FontStyle.Regular);
            var size = TextRenderer.MeasureText(toolTip.GetToolTip(e.AssociatedControl), font, Size.Empty, TextFormatFlags.NoPrefix);
            e.ToolTipSize = new Size(size.Width + 18, size.Height + 12);
        };

        toolTip.Draw += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(Point.Empty, e.Bounds.Size);
            using var bg = new SolidBrush(CardBg2);
            using var border = new Pen(Color.FromArgb(74, 76, 86), 1f);
            using var path = RoundedRectPath(Rectangle.Inflate(bounds, -1, -1), 5);
            e.Graphics.FillPath(bg, path);
            e.Graphics.DrawPath(border, path);

            using var font = new Font("Segoe UI", 8.5f, FontStyle.Regular);
            var textBounds = Rectangle.Inflate(bounds, -9, -5);
            TextRenderer.DrawText(
                e.Graphics,
                e.ToolTipText,
                font,
                textBounds,
                Color.FromArgb(226, 228, 235),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };

        return toolTip;
    }

    private void SetActiveSideButton(SideNavButton activeButton)
    {
        foreach (var button in _navButtons)
        {
            button.Active = button == activeButton;
            button.Invalidate();
        }
    }

    private void SetActiveViewButton(AppView view)
    {
        var button = view switch
        {
            AppView.Servers => _serversNavButton,
            AppView.Settings => _settingsNavButton,
            AppView.Config => _configNavButton,
            AppView.Logs => _logsNavButton,
            AppView.Info => _infoNavButton,
            _ => null
        };
        if (button is not null) SetActiveSideButton(button);
    }

    private void NavigateBack()
    {
        var target = _previousView;
        _previousView = _currentView;
        NavigateTo(target, recordHistory: false);
    }

    private void NavigateTo(AppView view, bool recordHistory = true)
    {
        if (recordHistory && view != _currentView)
        {
            _previousView = _currentView;
        }

        _currentView = view;
        SetActiveViewButton(view);

        switch (view)
        {
            case AppView.Servers:
                ShowServersView();
                break;
            case AppView.Settings:
                ShowEmbeddedPage(BuildSettingsView());
                break;
            case AppView.Config:
                ShowEmbeddedPage(BuildToolPage(T("config.title"), BuildConfigSection()));
                break;
            case AppView.Logs:
                ShowEmbeddedPage(BuildToolPage(T("logs.title"), BuildLogSection()));
                break;
            case AppView.Info:
                ShowEmbeddedPage(BuildToolPage(T("info.title"), BuildInformationSection()));
                break;
        }
    }

    private void ShowCurrentView()
    {
        SetActiveViewButton(_currentView);
        switch (_currentView)
        {
            case AppView.Servers:
                ShowServersView();
                break;
            case AppView.Settings:
                ShowEmbeddedPage(BuildSettingsView());
                break;
            case AppView.Config:
                ShowEmbeddedPage(BuildToolPage(T("config.title"), BuildConfigSection()));
                break;
            case AppView.Logs:
                ShowEmbeddedPage(BuildToolPage(T("logs.title"), BuildLogSection()));
                break;
            case AppView.Info:
                ShowEmbeddedPage(BuildToolPage(T("info.title"), BuildInformationSection()));
                break;
        }
    }

    private Button MainButton(string text, Action action, int width = 0, bool secondary = false)
    {
        var button = new RoundedButton
        {
            Text = text,
            Radius = 8,
            Width = width == 0 ? 150 : width,
            Height = 40,
            FlatStyle = FlatStyle.Flat,
            BackColor = secondary ? Color.FromArgb(40, 42, 42) : Accent,
            BorderColor = secondary ? Color.FromArgb(64, 66, 66) : Accent,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Margin = new Padding(4)
        };
        button.FlatAppearance.BorderSize = 0;
        button.MouseEnter += (_, _) => button.BackColor = secondary ? Color.FromArgb(55, 57, 57) : Color.FromArgb(143, 126, 255);
        button.MouseLeave += (_, _) => button.BackColor = secondary ? Color.FromArgb(40, 42, 42) : Accent;
        button.MouseDown += (_, _) => button.Padding = new Padding(1, 2, 0, 0);
        button.MouseUp += (_, _) => button.Padding = Padding.Empty;
        button.Click += (_, _) => action();
        return button;
    }

    private void ShowServersView(bool closeInlineImport = true)
    {
        if (_contentHost is null) return;

        _contentHost.SuspendLayout();
        _contentHost.Controls.Clear();
        _contentHost.ColumnStyles.Clear();
        _contentHost.RowStyles.Clear();
        _contentHost.ColumnCount = 2;
        _contentHost.RowCount = 1;
        _contentHost.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 583));
        _contentHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _serverListView ??= BuildServerList();
        _connectionView ??= BuildConnectionPanel();
        _contentHost.Controls.Add(_serverListView, 0, 0);
        _contentHost.Controls.Add(_connectionView, 1, 0);
        _contentHost.ResumeLayout(true);

        if (closeInlineImport) HideInlineImport();
        RefreshProfiles();
        RefreshMainPreview();
    }

    private void ShowSettingsWindow()
    {
        ShowEmbeddedPage(BuildSettingsView());
    }

    private void ShowConfigWindow()
    {
        ShowEmbeddedPage(BuildToolPage(T("config.title"), BuildConfigSection()));
    }

    private void ShowLogWindow()
    {
        ShowEmbeddedPage(BuildToolPage(T("logs.title"), BuildLogSection()));
    }

    private void ShowEmbeddedPage(Control page)
    {
        if (_contentHost is null) return;

        _contentHost.SuspendLayout();
        _contentHost.Controls.Clear();
        _contentHost.ColumnStyles.Clear();
        _contentHost.RowStyles.Clear();
        _contentHost.ColumnCount = 1;
        _contentHost.RowCount = 1;
        _contentHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _contentHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _contentHost.Controls.Add(page, 0, 0);
        _contentHost.ResumeLayout(true);
        AnimatePageEntrance(page);
    }

    private void AnimatePageEntrance(Control page)
    {
        var step = 0;
        page.Margin = new Padding(10, 0, 0, 0);
        var timer = new System.Windows.Forms.Timer { Interval = 16 };
        timer.Tick += (_, _) =>
        {
            step++;
            var offset = Math.Max(0, 10 - step * 2);
            page.Margin = new Padding(offset, 0, 0, 0);
            page.Invalidate();
            if (offset > 0) return;
            timer.Stop();
            timer.Dispose();
        };
        timer.Start();
    }

    private Control BuildToolPage(string title, Control content)
    {
        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            BackColor = Bg,
            Padding = new Padding(31, 28, 39, 26)
        };
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            ForeColor = TextColor,
            Font = new Font("Segoe UI", 24f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        page.Controls.Add(content, 0, 1);
        return page;
    }

    private Control BuildSettingsView()
    {
        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            BackColor = Bg,
            Padding = new Padding(31, 28, 39, 26)
        };
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        page.Controls.Add(new Label
        {
            Text = T("settings.title"),
            Dock = DockStyle.Fill,
            ForeColor = TextColor,
            Font = new Font("Segoe UI", 24f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Bg
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var sections = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Bg,
            Padding = new Padding(0, 6, 18, 0)
        };

        var contentHost = new SettingsScrollPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Bg
        };

        var buttons = new List<RoundedButton>();
        void SelectSection(RoundedButton selected, Control content)
        {
            foreach (var button in buttons)
            {
                button.BackColor = CardBg2;
                button.BorderColor = Color.FromArgb(62, 64, 64);
                button.ForeColor = Muted;
            }

            selected.BackColor = Accent;
            selected.BorderColor = Accent;
            selected.ForeColor = Color.White;
            contentHost.Content = content;
        }

        RoundedButton AddSection(string title, Func<Control> build)
        {
            var button = new RoundedButton
            {
                Text = title,
                Width = 164,
                Height = 38,
                Radius = 8,
                BackColor = CardBg2,
                BorderColor = Color.FromArgb(62, 64, 64),
                ForeColor = Muted,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Margin = new Padding(0, 0, 0, 10),
                Cursor = Cursors.Hand
            };
            button.Click += (_, _) => SelectSection(button, build());
            buttons.Add(button);
            sections.Controls.Add(button);
            return button;
        }

        var general = AddSection(T("settings.core"), BuildGeneralSettingsSection);
        AddSection(T("settings.connection"), BuildConnectionSettingsSection);
        AddSection(T("settings.dns"), BuildDnsSettingsSection);
        AddSection(T("settings.routing"), BuildRoutingSettingsSection);
        AddSection(T("settings.advanced"), BuildAdvancedSettingsSection);
        AddSection(T("settings.customization"), BuildCustomizationSettingsSection);
        AddSection(T("nav.config"), BuildConfigSection);
        AddSection(T("nav.logs"), BuildLogSection);

        body.Controls.Add(sections, 0, 0);
        body.Controls.Add(contentHost, 1, 0);
        page.Controls.Add(body, 0, 1);

        SelectSection(general, BuildGeneralSettingsSection());
        return page;
    }

    private Control BuildGeneralSettingsSection()
    {
        var panel = SettingsCard();
        var singBoxPath = new TextBox { Text = _state.SingBoxPath };
        StyleInput(singBoxPath);
        singBoxPath.Dock = DockStyle.Fill;

        panel.Controls.Add(FieldLabel("sing-box.exe"), 0, 0);
        panel.Controls.Add(SettingsTextInput(singBoxPath), 1, 0);
        panel.Controls.Add(LightButton(T("settings.browse"), () => BrowseSingBox(singBoxPath)), 2, 0);
        panel.Controls.Add(LightButton(T("settings.save"), () =>
        {
            _state.SingBoxPath = singBoxPath.Text.Trim();
            SaveSettings();
        }, primary: true), 1, 1);
        return panel;
    }

    private Control BuildConnectionSettingsSection()
    {
        var panel = SettingsCard();
        var tunEnabled = new CheckBox { Text = "TUN", AutoSize = true, Checked = _state.TunEnabled };
        var mixedEnabled = new CheckBox { Text = T("settings.proxy"), AutoSize = true, Checked = _state.MixedProxyEnabled };
        var mixedPort = new SettingsNumberInput { Minimum = 1, Maximum = 65535, Width = 110, Value = Math.Clamp(_state.MixedProxyPort, 1, 65535) };
        var tunMtu = new SettingsNumberInput { Minimum = 1280, Maximum = 10000, Width = 110, Value = Math.Clamp(_state.TunMtu, 1280, 10000) };
        var tunAddress = new TextBox { Width = 180, Text = _state.TunAddress };
        StyleInput(tunAddress);

        var modes = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, BackColor = Bg };
        StyleCheck(tunEnabled);
        StyleCheck(mixedEnabled);
        modes.Controls.Add(tunEnabled);
        modes.Controls.Add(mixedEnabled);

        panel.Controls.Add(FieldLabel(T("settings.mode")), 0, 0);
        panel.Controls.Add(modes, 1, 0);
        panel.Controls.Add(FieldLabel(T("settings.proxy.port")), 0, 1);
        panel.Controls.Add(mixedPort, 1, 1);
        panel.Controls.Add(FieldLabel(T("settings.tun.address")), 0, 2);
        panel.Controls.Add(SettingsTextInput(tunAddress, 180), 1, 2);
        panel.Controls.Add(FieldLabel("TUN MTU"), 0, 3);
        panel.Controls.Add(tunMtu, 1, 3);
        panel.Controls.Add(LightButton(T("settings.save"), () =>
        {
            _state.TunEnabled = tunEnabled.Checked;
            _state.MixedProxyEnabled = mixedEnabled.Checked;
            _state.MixedProxyPort = (int)mixedPort.Value;
            _state.TunAddress = tunAddress.Text.Trim();
            _state.TunMtu = (int)tunMtu.Value;
            SaveSettings();
        }, primary: true), 1, 4);
        return panel;
    }

    private Control BuildDnsSettingsSection()
    {
        var panel = SettingsCard();
        var dnsServer = new TextBox { Width = 180, Text = _state.DnsServer };
        var dnsStrategy = Combo(["prefer_ipv4", "prefer_ipv6", "ipv4_only", "ipv6_only"], _state.DnsStrategy);
        StyleInput(dnsServer);
        panel.Controls.Add(FieldLabel(T("settings.dns.server")), 0, 0);
        panel.Controls.Add(SettingsTextInput(dnsServer, 180), 1, 0);
        panel.Controls.Add(FieldLabel(T("settings.strategy")), 0, 1);
        panel.Controls.Add(dnsStrategy, 1, 1);
        panel.Controls.Add(LightButton(T("settings.save"), () =>
        {
            _state.DnsServer = dnsServer.Text.Trim();
            _state.DnsStrategy = dnsStrategy.Text;
            SaveSettings();
        }, primary: true), 1, 2);
        return panel;
    }

    private Control BuildRoutingSettingsSection()
    {
        var panel = SettingsCard();
        panel.RowCount = 4;
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        var bypassSites = new TextBox
        {
            Text = string.Join(Environment.NewLine, _state.RoutingBypassSites),
            Multiline = true,
            AcceptsReturn = true,
            WordWrap = false,
            ScrollBars = ScrollBars.None
        };
        var proxySites = new TextBox
        {
            Text = string.Join(Environment.NewLine, _state.RoutingProxySites),
            Multiline = true,
            AcceptsReturn = true,
            WordWrap = false,
            ScrollBars = ScrollBars.None
        };
        StyleInput(bypassSites);
        StyleInput(proxySites);

        var modeHint = FieldLabel(T("settings.proxy.mode.only"));
        modeHint.Margin = new Padding(8, 6, 8, 8);
        panel.Controls.Add(modeHint, 1, 0);
        panel.Controls.Add(FieldLabel(T("settings.excluded.sites")), 0, 1);
        panel.Controls.Add(SettingsTextInput(bypassSites, height: 142), 1, 1);
        panel.Controls.Add(FieldLabel(T("settings.proxied.sites")), 0, 2);
        panel.Controls.Add(SettingsTextInput(proxySites, height: 142), 1, 2);
        panel.Controls.Add(LightButton(T("settings.save"), () =>
        {
            _state.RoutingBypassSites = ParseSiteList(bypassSites.Text);
            _state.RoutingProxySites = ParseSiteList(proxySites.Text);
            SaveSettings();
        }, primary: true), 1, 3);
        return panel;
    }

    private Control BuildAdvancedSettingsSection()
    {
        var panel = SettingsCard();
        var sniffEnabled = new CheckBox { Text = T("settings.sniff"), AutoSize = true, Checked = _state.SniffEnabled };
        var autoRoute = new CheckBox { Text = T("settings.auto.route"), AutoSize = true, Checked = _state.AutoRoute };
        var strictRoute = new CheckBox { Text = T("settings.strict.route"), AutoSize = true, Checked = _state.StrictRoute };
        var stackSystem = new CheckBox { Text = T("settings.system.stack"), AutoSize = true, Checked = _state.StackSystem };
        var allowInsecure = new CheckBox { Text = T("settings.allow.insecure"), AutoSize = true, Checked = _state.AllowInsecureCertificates };
        var setSystemProxy = new CheckBox { Text = T("settings.system.proxy"), AutoSize = true, Checked = _state.SetSystemProxy };
        var logLevel = Combo(["trace", "debug", "info", "warn", "error", "fatal", "panic"], _state.LogLevel);

        var routeOptions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, BackColor = Bg };
        foreach (var box in new[] { sniffEnabled, autoRoute, strictRoute, stackSystem, allowInsecure, setSystemProxy })
        {
            StyleCheck(box);
            routeOptions.Controls.Add(box);
        }

        panel.Controls.Add(FieldLabel(T("settings.route")), 0, 0);
        panel.Controls.Add(routeOptions, 1, 0);
        panel.Controls.Add(FieldLabel(T("settings.log.level")), 0, 1);
        panel.Controls.Add(logLevel, 1, 1);
        panel.Controls.Add(LightButton(T("settings.save"), () =>
        {
            _state.SniffEnabled = sniffEnabled.Checked;
            _state.AutoRoute = autoRoute.Checked;
            _state.StrictRoute = strictRoute.Checked;
            _state.StackSystem = stackSystem.Checked;
            _state.AllowInsecureCertificates = allowInsecure.Checked;
            _state.SetSystemProxy = setSystemProxy.Checked;
            _state.LogLevel = logLevel.Text;
            SaveSettings();
        }, primary: true), 1, 2);
        return panel;
    }

    private Control BuildCustomizationSettingsSection()
    {
        NormalizeNavCustomization();
        var order = _state.NavItemOrder.ToList();
        var position = Combo([PositionDisplayName(NavPanelPosition.Left), PositionDisplayName(NavPanelPosition.Top)], PositionDisplayName(_state.NavPanelPosition));
        var theme = Combo(
            [ThemeDisplayName(AppTheme.Dark), ThemeDisplayName(AppTheme.Darker), ThemeDisplayName(AppTheme.Violet), ThemeDisplayName(AppTheme.Graphite)],
            ThemeDisplayName(_state.Theme));
        var language = Combo(["\u0420\u0443\u0441\u0441\u043a\u0438\u0439", "English"], LanguageDisplayName(_state.Language));
        var iconSize = Combo(
            [IconSizeDisplayName(NavIconSize.Compact), IconSizeDisplayName(NavIconSize.Normal), IconSizeDisplayName(NavIconSize.Large)],
            IconSizeDisplayName(_state.NavIconSize));
        var showTooltips = new CheckBox { Text = T("custom.tooltips"), AutoSize = true, Checked = _state.ShowNavTooltips };
        var showSeparators = new CheckBox { Text = T("custom.separators"), AutoSize = true, Checked = _state.ShowNavSeparators };
        var useGlass = new CheckBox { Text = T("custom.glass"), AutoSize = true, Checked = _state.UsePanelGlass && GlassSupported(), Enabled = GlassSupported() };
        foreach (var box in new[] { showTooltips, showSeparators, useGlass })
        {
            StyleCheck(box);
        }

        var visibility = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = Bg,
            Margin = new Padding(4, 8, 10, 4)
        };
        var visibilityBoxes = new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in DefaultNavItemOrder)
        {
            var box = new CheckBox
            {
                Text = NavItemTitle(id),
                AutoSize = true,
                Checked = !_state.HiddenNavItems.Contains(id, StringComparer.OrdinalIgnoreCase),
                Enabled = id != "settings",
                Margin = new Padding(0, 0, 12, 6)
            };
            StyleCheck(box);
            visibilityBoxes[id] = box;
            visibility.Controls.Add(box);
        }

        var accentPreview = new RoundedPanel
        {
            Width = 42,
            Height = 28,
            Radius = 7,
            BackColor = Accent,
            BorderColor = Accent,
            Margin = new Padding(4, 10, 8, 4)
        };
        var accentRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Bg,
            Margin = Padding.Empty
        };
        accentRow.Controls.Add(accentPreview);
        accentRow.Controls.Add(LightButton(T("custom.pick"), () =>
        {
            using var dialog = new ColorDialog
            {
                Color = accentPreview.BackColor,
                FullOpen = true
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            accentPreview.BackColor = dialog.Color;
            accentPreview.BorderColor = dialog.Color;
            accentPreview.Invalidate();
        }));

        var flags = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = Bg,
            Margin = new Padding(4, 8, 10, 4)
        };
        flags.Controls.Add(showTooltips);
        flags.Controls.Add(showSeparators);
        flags.Controls.Add(useGlass);
        var preview = new NavCustomizationPreview(order, _state.NavPanelPosition, _state.NavIconSize)
        {
            Dock = DockStyle.Top,
            Margin = new Padding(4, 8, 10, 20)
        };

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 10,
            BackColor = Bg,
            Padding = new Padding(0, 6, 0, 22)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 320));

        void ApplyPreviewHeight()
        {
            var selectedPosition = TryParsePosition(position.Text, out var parsedPosition)
                ? parsedPosition
                : NavPanelPosition.Left;
            var selectedSize = TryParseIconSize(iconSize.Text, out var parsedSize)
                ? parsedSize
                : NavIconSize.Normal;
            var itemSize = selectedSize switch
            {
                NavIconSize.Compact => 46,
                NavIconSize.Large => 66,
                _ => 56
            };
            var itemCount = order.Count + 2;
            var height = selectedPosition == NavPanelPosition.Top
                ? Math.Max(160, itemSize + 64)
                : itemCount * itemSize + Math.Max(0, itemCount - 1) * 6 + 32;

            preview.Height = height;
            panel.RowStyles[9].Height = height + 46;
            panel.PerformLayout();
        }
        ApplyPreviewHeight();

        var title = new Label
        {
            Text = T("custom.title"),
            Dock = DockStyle.Fill,
            ForeColor = TextColor,
            Font = new Font("Segoe UI", 13f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var hint = new Label
        {
            Text = T("custom.hint"),
            Dock = DockStyle.Fill,
            ForeColor = Muted,
            Font = new Font("Segoe UI", 9f),
            TextAlign = ContentAlignment.MiddleLeft
        };

        position.TextChanged += (_, _) =>
        {
            preview.Position = TryParsePosition(position.Text, out var next)
                ? next
                : NavPanelPosition.Left;
            ApplyPreviewHeight();
        };
        iconSize.TextChanged += (_, _) =>
        {
            preview.IconSize = TryParseIconSize(iconSize.Text, out var next)
                ? next
                : NavIconSize.Normal;
            ApplyPreviewHeight();
        };

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = Bg,
            Margin = new Padding(0, 8, 0, 0),
            Padding = Padding.Empty
        };
        actions.Controls.Add(LightButton(T("settings.save"), () =>
        {
            var previousLanguage = _state.Language;
            if (TryParseTheme(theme.Text, out var selectedTheme)) _state.Theme = selectedTheme;
            if (TryParseLanguage(language.Text, out var selectedLanguage)) _state.Language = selectedLanguage;
            if (TryParseIconSize(iconSize.Text, out var selectedSize)) _state.NavIconSize = selectedSize;
            _state.AccentColorArgb = accentPreview.BackColor.ToArgb();
            _state.HiddenNavItems = visibilityBoxes
                .Where(pair => !pair.Value.Checked)
                .Select(pair => pair.Key)
                .ToList();
            _state.ShowNavTooltips = showTooltips.Checked;
            _state.ShowNavSeparators = showSeparators.Checked;
            _state.UsePanelGlass = useGlass.Checked;
            _state.NavPanelPosition = preview.Position;
            _state.NavItemOrder = NormalizeNavItemOrder(preview.Order);
            ApplyAppearanceSettings();
            ApplyWindowBackdrop();
            SaveSettings();
            if (previousLanguage != _state.Language)
            {
                ResetCachedMainViews();
            }
            RebuildShellLayout(showCurrentView: false);
            ShowSettingsWindow();
        }, primary: true, width: 128));
        actions.Controls.Add(LightButton(T("custom.reset"), () =>
        {
            position.Text = PositionDisplayName(NavPanelPosition.Left);
            theme.Text = ThemeDisplayName(AppTheme.Dark);
            language.Text = LanguageDisplayName(AppLanguage.Russian);
            iconSize.Text = IconSizeDisplayName(NavIconSize.Normal);
            accentPreview.BackColor = Color.FromArgb(124, 105, 255);
            accentPreview.BorderColor = accentPreview.BackColor;
            foreach (var pair in visibilityBoxes) pair.Value.Checked = true;
            showTooltips.Checked = true;
            showSeparators.Checked = true;
            useGlass.Checked = GlassSupported();
            preview.IconSize = NavIconSize.Normal;
            preview.Reset(DefaultNavItemOrder, NavPanelPosition.Left);
        }, width: 112));
        actions.Controls.Add(LightButton(T("custom.export"), () => ExportLayoutSettings(), width: 112));
        actions.Controls.Add(LightButton(T("custom.import"), () =>
        {
            var previousLanguage = _state.Language;
            if (ImportLayoutSettings())
            {
                ApplyAppearanceSettings();
                ApplyWindowBackdrop();
                if (previousLanguage != _state.Language)
                {
                    ResetCachedMainViews();
                }
                RebuildShellLayout(showCurrentView: false);
                ShowSettingsWindow();
            }
        }, width: 112));

        panel.Controls.Add(FieldLabel(T("custom.position")), 0, 0);
        panel.Controls.Add(position, 1, 0);
        panel.Controls.Add(FieldLabel(T("custom.theme")), 0, 1);
        panel.Controls.Add(theme, 1, 1);
        panel.Controls.Add(FieldLabel(T("custom.language")), 0, 2);
        panel.Controls.Add(language, 1, 2);
        panel.Controls.Add(FieldLabel(T("custom.accent")), 0, 3);
        panel.Controls.Add(accentRow, 1, 3);
        panel.Controls.Add(FieldLabel(T("custom.icon.size")), 0, 4);
        panel.Controls.Add(iconSize, 1, 4);
        panel.Controls.Add(FieldLabel(T("custom.visible.tabs")), 0, 5);
        panel.Controls.Add(visibility, 1, 5);
        panel.SetColumnSpan(visibility, 2);
        panel.Controls.Add(FieldLabel(T("custom.options")), 0, 6);
        panel.Controls.Add(flags, 1, 6);
        panel.SetColumnSpan(flags, 2);
        panel.Controls.Add(actions, 1, 7);
        panel.SetColumnSpan(actions, 2);
        panel.Controls.Add(title, 1, 8);
        panel.Controls.Add(hint, 2, 8);
        panel.SetColumnSpan(hint, 1);
        panel.Controls.Add(preview, 1, 9);
        panel.SetColumnSpan(preview, 2);
        return panel;
    }

    private static string LanguageDisplayName(AppLanguage language)
    {
        return language == AppLanguage.English ? "English" : "\u0420\u0443\u0441\u0441\u043a\u0438\u0439";
    }

    private static bool TryParseLanguage(string value, out AppLanguage language)
    {
        language = value.Trim().Equals("English", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.English
            : AppLanguage.Russian;
        return true;
    }

    private string PositionDisplayName(NavPanelPosition position)
    {
        var ru = _state.Language == AppLanguage.Russian;
        return position switch
        {
            NavPanelPosition.Top => ru ? "Сверху" : "Top",
            _ => ru ? "Слева" : "Left"
        };
    }

    private bool TryParsePosition(string value, out NavPanelPosition position)
    {
        if (value.Equals("Top", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Сверху", StringComparison.OrdinalIgnoreCase))
        {
            position = NavPanelPosition.Top;
            return true;
        }

        position = NavPanelPosition.Left;
        return true;
    }

    private string ThemeDisplayName(AppTheme theme)
    {
        var ru = _state.Language == AppLanguage.Russian;
        return theme switch
        {
            AppTheme.Darker => ru ? "Темнее" : "Darker",
            AppTheme.Violet => ru ? "Фиолетовая" : "Violet",
            AppTheme.Graphite => ru ? "Графитовая" : "Graphite",
            _ => ru ? "Тёмная" : "Dark"
        };
    }

    private bool TryParseTheme(string value, out AppTheme theme)
    {
        if (value.Equals("Darker", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Темнее", StringComparison.OrdinalIgnoreCase))
        {
            theme = AppTheme.Darker;
            return true;
        }

        if (value.Equals("Violet", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Фиолетовая", StringComparison.OrdinalIgnoreCase))
        {
            theme = AppTheme.Violet;
            return true;
        }

        if (value.Equals("Graphite", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Графитовая", StringComparison.OrdinalIgnoreCase))
        {
            theme = AppTheme.Graphite;
            return true;
        }

        theme = AppTheme.Dark;
        return true;
    }

    private string IconSizeDisplayName(NavIconSize size)
    {
        var ru = _state.Language == AppLanguage.Russian;
        return size switch
        {
            NavIconSize.Compact => ru ? "Компактный" : "Compact",
            NavIconSize.Large => ru ? "Крупный" : "Large",
            _ => ru ? "Обычный" : "Normal"
        };
    }

    private bool TryParseIconSize(string value, out NavIconSize size)
    {
        if (value.Equals("Compact", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Компактный", StringComparison.OrdinalIgnoreCase))
        {
            size = NavIconSize.Compact;
            return true;
        }

        if (value.Equals("Large", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("Крупный", StringComparison.OrdinalIgnoreCase))
        {
            size = NavIconSize.Large;
            return true;
        }

        size = NavIconSize.Normal;
        return true;
    }

    private static SettingsSelect Combo(string[] values, string selected)
    {
        return new SettingsSelect(values, values.Contains(selected) ? selected : values[0])
        {
            Width = 180,
            Margin = new Padding(4, 8, 10, 8)
        };
    }

    private TableLayoutPanel SettingsCard()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 3,
            RowCount = 5,
            AutoSize = true,
            BackColor = Bg,
            Padding = new Padding(0, 6, 0, 0)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        return panel;
    }

    private Control BuildConfigSection()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, BackColor = Bg };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(LightButton(T("config.write"), WriteConfig), 0, 0);
        var configBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            Font = new Font("Consolas", 10),
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(20, 22, 22),
            ForeColor = Color.FromArgb(200, 200, 200),
            Text = SelectedProfile() is null ? "" : SingBoxConfigBuilder.Build(_state, SelectedProfile()!)
        };
        panel.Controls.Add(SettingsTextInput(configBox, height: 420), 0, 1);
        return panel;
    }

    private Control BuildLogSection()
    {
        var logBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            Font = new Font("Consolas", 10),
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(20, 22, 22),
            ForeColor = Color.FromArgb(200, 200, 200),
            Text = string.Join(Environment.NewLine, _logs)
        };
        return SettingsTextInput(logBox, height: 420);
    }

    private Control BuildInformationSection()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = Bg,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(0, 8, 0, 0)
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        var githubLink = new LinkLabel
        {
            Text = GitHubRepositoryUrl,
            Dock = DockStyle.Fill,
            LinkColor = Color.FromArgb(168, 157, 255),
            ActiveLinkColor = Color.White,
            VisitedLinkColor = Color.FromArgb(168, 157, 255),
            BackColor = Bg,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        };
        githubLink.LinkClicked += (_, _) => OpenExternalUrl(GitHubRepositoryUrl);
        panel.Controls.Add(githubLink, 0, 0);

        panel.Controls.Add(new Label
        {
            Text = $"{T("info.current.version")}: {AppVersion}",
            Dock = DockStyle.Fill,
            ForeColor = TextColor,
            BackColor = Bg,
            Font = new Font("Segoe UI", 10f),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 1);

        var updateStatus = new Label
        {
            Text = T("update.checking"),
            Dock = DockStyle.Fill,
            ForeColor = Muted,
            BackColor = Bg,
            Font = new Font("Segoe UI", 9.5f),
            TextAlign = ContentAlignment.MiddleLeft
        };
        panel.Controls.Add(updateStatus, 0, 2);

        var updateButton = LightButton(T("update.button"), () => { }, primary: true);
        updateButton.Visible = false;
        panel.Controls.Add(updateButton, 0, 3);

        _ = CheckForUpdatesAsync(updateStatus, updateButton);
        return panel;
    }

    private async Task CheckForUpdatesAsync(Label statusLabel, Button updateButton)
    {
        try
        {
            var release = await GetLatestReleaseAsync();
            if (release is null)
            {
                statusLabel.Text = T("update.none");
                return;
            }

            var latestVersion = NormalizeVersion(release.Tag);
            if (IsNewerVersion(latestVersion, AppVersion))
            {
                statusLabel.Text = $"{T("update.available")}: {release.Tag}";
                updateButton.Visible = true;
                updateButton.Click += async (_, _) => await ShowUpdateDialogAsync(release);
            }
            else
            {
                statusLabel.Text = T("update.latest");
            }
        }
        catch (Exception ex)
        {
            statusLabel.Text = GetUpdateCheckFailureMessage(ex);
            Log($"Update check failed: {FormatExceptionChain(ex)}");
        }
    }

    private async Task CheckForStartupUpdateAsync()
    {
        if (_startupUpdatePromptShown) return;
        _startupUpdatePromptShown = true;

        try
        {
            var release = await GetLatestReleaseAsync();
            if (release is null) return;
            if (IsNewerVersion(NormalizeVersion(release.Tag), AppVersion))
            {
                await ShowUpdateDialogAsync(release);
            }
        }
        catch (Exception ex)
        {
            Log($"Startup update check failed: {FormatExceptionChain(ex)}");
        }
    }

    private string GetUpdateCheckFailureMessage(Exception ex)
    {
        if (IsNetworkOrTlsFailure(ex))
        {
            return $"{T("update.check.failed")}: {T("update.check.network")}";
        }

        return $"{T("update.check.failed")}: {GetInnermostException(ex).Message}";
    }

    private static bool IsNetworkOrTlsFailure(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException or SocketException or IOException or AuthenticationException)
            {
                return true;
            }

            if (current.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static Exception GetInnermostException(Exception ex)
    {
        while (ex.InnerException is not null)
        {
            ex = ex.InnerException;
        }

        return ex;
    }

    private static string FormatExceptionChain(Exception ex)
    {
        var parts = new List<string>();
        for (var current = ex; current is not null; current = current.InnerException)
        {
            parts.Add($"{current.GetType().Name}: {current.Message}");
        }

        return string.Join(" -> ", parts);
    }

    private async Task<ReleaseInfo?> GetLatestReleaseAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, GitHubLatestReleaseApiUrl);
        request.Headers.UserAgent.ParseAdd($"ior/{AppVersion}");
        using var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var latestTag = root.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
        if (string.IsNullOrWhiteSpace(latestTag)) return null;

        var releaseUrl = root.TryGetProperty("html_url", out var url)
            ? url.GetString() ?? GitHubRepositoryUrl + "/releases/latest"
            : GitHubRepositoryUrl + "/releases/latest";
        var asset = FindUpdateAsset(root);
        return new ReleaseInfo(latestTag, releaseUrl, asset.Name, asset.DownloadUrl);
    }

    private static (string? Name, string? DownloadUrl) FindUpdateAsset(JsonElement releaseRoot)
    {
        if (!releaseRoot.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
        }

        var packageKeywords = IsWindows10Package()
            ? new[] { "windows-10", "win10" }
            : new[] { "windows-11", "win11" };

        foreach (var keyword in packageKeywords)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
                var downloadUrl = asset.TryGetProperty("browser_download_url", out var downloadValue) ? downloadValue.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(downloadUrl)) continue;
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return (name, downloadUrl);
                }
            }
        }

        return (null, null);
    }

    private Task ShowUpdateDialogAsync(ReleaseInfo release)
    {
        using var dialog = new UpdateDialog(release.Tag, release.AssetName is not null, _state.Language);
        dialog.UpdateRequested += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(release.AssetDownloadUrl))
            {
                OpenExternalUrl(release.ReleaseUrl);
                dialog.Close();
                return;
            }

            await DownloadAndInstallUpdateAsync(release, dialog);
        };
        dialog.ShowDialog(this);
        return Task.CompletedTask;
    }

    private async Task DownloadAndInstallUpdateAsync(ReleaseInfo release, UpdateDialog dialog)
    {
        var updateRoot = Path.Combine(Path.GetTempPath(), "ior-update-" + Guid.NewGuid().ToString("N"));
        var zipPath = Path.Combine(updateRoot, release.AssetName ?? "ior-update.zip");
        var extractPath = Path.Combine(updateRoot, "files");
        Directory.CreateDirectory(updateRoot);

        try
        {
            dialog.SetBusy(T("update.downloading"));
            using var response = await _httpClient.GetAsync(release.AssetDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength;
            await using (var input = await response.Content.ReadAsStreamAsync())
            await using (var output = File.Create(zipPath))
            {
                var buffer = new byte[128 * 1024];
                long readTotal = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer);
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read));
                    readTotal += read;
                    if (total is > 0)
                    {
                        dialog.SetBusy($"{T("update.downloading")} {readTotal * 100 / total.Value}%");
                    }
                }
            }

            dialog.SetBusy(T("update.preparing"));
            ZipFile.ExtractToDirectory(zipPath, extractPath, overwriteFiles: true);
            var sourcePath = ResolveExtractedUpdateRoot(extractPath);
            if (!File.Exists(Path.Combine(sourcePath, "ior.exe")))
            {
                throw new InvalidOperationException(T("update.exe.missing"));
            }

            StartUpdateScript(sourcePath);
            Application.Exit();
        }
        catch (Exception ex)
        {
            dialog.SetError($"{T("update.install.failed")}: {ex.Message}");
            Log($"Update failed: {ex.Message}");
        }
    }

    private static string ResolveExtractedUpdateRoot(string extractPath)
    {
        if (File.Exists(Path.Combine(extractPath, "ior.exe"))) return extractPath;

        var packageFolder = IsWindows10Package() ? "windows-10" : "windows-11";
        var packagePath = Path.Combine(extractPath, packageFolder);
        if (File.Exists(Path.Combine(packagePath, "ior.exe"))) return packagePath;

        var directories = Directory.GetDirectories(extractPath);
        if (directories.Length == 1 && File.Exists(Path.Combine(directories[0], "ior.exe")))
        {
            return directories[0];
        }

        var nestedPackagePath = Directory
            .GetDirectories(extractPath, packageFolder, SearchOption.AllDirectories)
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "ior.exe")));
        if (nestedPackagePath is not null) return nestedPackagePath;

        var nestedExe = Directory.GetFiles(extractPath, "ior.exe", SearchOption.AllDirectories).FirstOrDefault();
        return nestedExe is null ? extractPath : Path.GetDirectoryName(nestedExe) ?? extractPath;
    }

    private static void StartUpdateScript(string sourcePath)
    {
        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var scriptPath = Path.Combine(Path.GetTempPath(), "ior-update-" + Guid.NewGuid().ToString("N") + ".cmd");
        var pid = Environment.ProcessId;
        var script = $"""
@echo off
setlocal
set "SRC={sourcePath}"
set "DST={appDir}"
set "PID={pid}"
:wait
tasklist /FI "PID eq %PID%" | find "%PID%" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto wait
)
robocopy "%SRC%" "%DST%" /E /R:3 /W:1 /NFL /NDL /NJH /NJS >nul
start "" "%DST%\ior.exe"
endlocal
del "%~f0"
""";
        File.WriteAllText(scriptPath, script);
        Process.Start(new ProcessStartInfo
        {
            FileName = scriptPath,
            UseShellExecute = true,
            WorkingDirectory = appDir,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private static string NormalizeVersion(string value)
    {
        return value.Trim().TrimStart('v', 'V');
    }

    private static bool IsNewerVersion(string latest, string current)
    {
        return Version.TryParse(latest, out var latestVersion) &&
               Version.TryParse(current, out var currentVersion) &&
               latestVersion > currentVersion;
    }

    private static void OpenExternalUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            Clipboard.SetText(url);
        }
    }

    private TabPage SettingsTab()
    {
        var page = new TabPage(T("settings.title")) { BackColor = Bg, Padding = new Padding(18) };
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 3,
            RowCount = 5,
            AutoSize = true,
            BackColor = Bg
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        page.Controls.Add(panel);

        var singBoxPath = new TextBox { Text = _state.SingBoxPath };
        var tunEnabled = new CheckBox { Text = "TUN", AutoSize = true, Checked = _state.TunEnabled };
        var mixedEnabled = new CheckBox { Text = T("settings.proxy"), AutoSize = true, Checked = _state.MixedProxyEnabled };
        var mixedPort = new SettingsNumberInput { Minimum = 1, Maximum = 65535, Width = 110, Value = Math.Clamp(_state.MixedProxyPort, 1, 65535) };
        var dnsServer = new TextBox { Width = 150, Text = _state.DnsServer };

        panel.Controls.Add(FieldLabel("sing-box.exe"), 0, 0);
        StyleInput(singBoxPath);
        singBoxPath.Dock = DockStyle.Fill;
        panel.Controls.Add(SettingsTextInput(singBoxPath), 1, 0);
        panel.Controls.Add(LightButton(T("settings.browse"), () => BrowseSingBox(singBoxPath)), 2, 0);

        panel.Controls.Add(FieldLabel(T("settings.mode")), 0, 1);
        var modes = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, BackColor = Bg };
        StyleCheck(tunEnabled);
        StyleCheck(mixedEnabled);
        modes.Controls.Add(tunEnabled);
        modes.Controls.Add(mixedEnabled);
        panel.Controls.Add(modes, 1, 1);

        panel.Controls.Add(FieldLabel(T("settings.proxy.port")), 0, 2);
        panel.Controls.Add(mixedPort, 1, 2);

        panel.Controls.Add(FieldLabel("DNS"), 0, 3);
        StyleInput(dnsServer);
        panel.Controls.Add(SettingsTextInput(dnsServer, 150), 1, 3);

        panel.Controls.Add(LightButton(T("settings.save"), () =>
        {
            _state.SingBoxPath = singBoxPath.Text.Trim();
            _state.TunEnabled = tunEnabled.Checked;
            _state.MixedProxyEnabled = mixedEnabled.Checked;
            _state.MixedProxyPort = (int)mixedPort.Value;
            _state.DnsServer = dnsServer.Text.Trim();
            SaveState();
            RefreshMainPreview();
        }, primary: true), 1, 4);
        return page;
    }

    private TabPage ConfigTab()
    {
        var page = new TabPage(T("config.title")) { BackColor = Bg, Padding = new Padding(14) };
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, BackColor = Bg };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Controls.Add(LightButton(T("config.write"), WriteConfig), 0, 0);
        var configBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            Font = new Font("Consolas", 10),
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(20, 22, 22),
            ForeColor = Color.FromArgb(200, 200, 200),
            Text = SelectedProfile() is null ? "" : SingBoxConfigBuilder.Build(_state, SelectedProfile()!)
        };
        panel.Controls.Add(SettingsTextInput(configBox, height: 420), 0, 1);
        page.Controls.Add(panel);
        return page;
    }

    private TabPage LogTab()
    {
        var page = new TabPage(T("logs.title")) { BackColor = Bg, Padding = new Padding(14) };
        var logBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            Font = new Font("Consolas", 10),
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(20, 22, 22),
            ForeColor = Color.FromArgb(200, 200, 200),
            Text = string.Join(Environment.NewLine, _logs)
        };
        page.Controls.Add(SettingsTextInput(logBox, height: 420));
        return page;
    }

    private static Label FieldLabel(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = Muted,
            Font = new Font("Segoe UI", 9.5f),
            Margin = new Padding(8, 12, 8, 8)
        };
    }

    private static void StyleInput(TextBox box)
    {
        box.BorderStyle = BorderStyle.None;
        box.BackColor = CardBg2;
        box.ForeColor = TextColor;
        box.Margin = new Padding(4, 8, 10, 8);
    }

    private static RoundedPanel SettingsTextInput(TextBox box, int? width = null, int height = 34)
    {
        var wrap = new RoundedPanel
        {
            Width = width ?? Math.Max(180, box.Width),
            Height = height,
            Dock = width is null ? DockStyle.Fill : DockStyle.None,
            BackColor = CardBg2,
            BorderColor = Color.FromArgb(82, 84, 92),
            Radius = 6,
            Margin = new Padding(4, 8, 10, 8),
            Padding = new Padding(10, box.Multiline ? 8 : 7, 10, 6)
        };
        box.ScrollBars = ScrollBars.None;
        box.Dock = DockStyle.Fill;
        box.Margin = Padding.Empty;

        if (box.Multiline && height > 60)
        {
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = CardBg2,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 14));

            var scroll = new DarkScrollBar
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(2, 1, 0, 1)
            };

            void UpdateScroll()
            {
                var lineHeight = Math.Max(1, box.Font.Height);
                var visibleLines = Math.Max(1, box.ClientSize.Height / lineHeight);
                scroll.Maximum = Math.Max(0, box.Lines.Length - visibleLines);
                scroll.LargeChange = visibleLines;
                scroll.Value = Math.Min(scroll.Maximum, GetFirstVisibleLine(box));
                scroll.Visible = true;
            }

            void ScrollTo(int value)
            {
                var current = GetFirstVisibleLine(box);
                var next = Math.Clamp(value, 0, scroll.Maximum);
                if (next == current) return;
                SendMessage(box.Handle, EmLineScroll, IntPtr.Zero, next - current);
                scroll.Value = next;
                box.Invalidate();
            }

            scroll.ScrollRequested += (_, value) => ScrollTo(value);
            box.MouseWheel += (_, e) =>
            {
                ScrollTo(GetFirstVisibleLine(box) - Math.Sign(e.Delta) * 3);
                UpdateScroll();
            };
            box.TextChanged += (_, _) => UpdateScroll();
            box.Resize += (_, _) => UpdateScroll();
            box.KeyUp += (_, _) => UpdateScroll();
            box.MouseUp += (_, _) => UpdateScroll();
            box.HandleCreated += (_, _) => box.BeginInvoke((Action)UpdateScroll);

            layout.Controls.Add(box, 0, 0);
            layout.Controls.Add(scroll, 1, 0);
            wrap.Controls.Add(layout);
        }
        else
        {
            wrap.Controls.Add(box);
        }
        return wrap;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, int lParam);

    private static int GetFirstVisibleLine(TextBox box)
    {
        return box.IsHandleCreated ? (int)SendMessage(box.Handle, EmGetFirstVisibleLine, IntPtr.Zero, 0) : 0;
    }

    private static List<string> ParseSiteList(string value)
    {
        return value
            .Split(['\r', '\n', ',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(site => site.Trim())
            .Where(site => site.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void StyleCheck(CheckBox box)
    {
        box.ForeColor = TextColor;
        box.BackColor = Bg;
        box.Margin = new Padding(0, 10, 18, 4);
    }

    private Button LightButton(string text, Action action, bool primary = false, int width = 132)
    {
        var button = new RoundedButton
        {
            Text = text,
            Radius = 7,
            Width = width,
            Height = 36,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Margin = new Padding(4, 7, 4, 7),
            BackColor = primary ? Accent : CardBg2,
            BorderColor = primary ? Accent : Color.FromArgb(80, 80, 80),
            ForeColor = Color.White,
            Cursor = Cursors.Hand
        };
        button.MouseEnter += (_, _) => button.BackColor = primary ? Color.FromArgb(143, 126, 255) : Color.FromArgb(48, 50, 50);
        button.MouseLeave += (_, _) => button.BackColor = primary ? Accent : CardBg2;
        button.Click += (_, _) => action();
        return button;
    }

    private void DrawProfileItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _profiles.Items.Count) return;
        var profile = (ProxyProfile)_profiles.Items[e.Index];
        var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var rect = new Rectangle(e.Bounds.Left + 8, e.Bounds.Top + 6, e.Bounds.Width - 16, e.Bounds.Height - 10);
        using var bg = new SolidBrush(selected ? Color.FromArgb(72, 73, 74) : CardBg2);
        using var title = new SolidBrush(TextColor);
        using var subtitle = new SolidBrush(Muted);
        using var accent = new SolidBrush(Accent);
        using var cardPath = RoundedRectPath(rect, 8);
        g.FillPath(bg, cardPath);
        if (selected)
        {
            using var accentPath = RoundedRectPath(new Rectangle(rect.Left, rect.Top + 2, 4, rect.Height - 4), 3);
            g.FillPath(accent, accentPath);
        }
        using var separator = new Pen(Color.FromArgb(56, 57, 57));
        g.DrawLine(separator, rect.Left + 8, rect.Bottom - 1, rect.Right - 8, rect.Bottom - 1);
        using var titleFont = new Font("Segoe UI", 11f, FontStyle.Bold);
        using var subtitleFont = new Font("Segoe UI", 8.8f);
        using var arrowFont = new Font("Segoe UI", 18f);
        DrawCountryFlag(g, profile, new Rectangle(rect.Left + 18, rect.Top + 22, 28, 18));
        g.DrawString(profile.Name, titleFont, title, rect.Left + 62, rect.Top + 14);
        g.DrawString($"{ProtocolLabel(profile)} / {profile.Server}:{profile.Port}", subtitleFont, subtitle, rect.Left + 62, rect.Top + 39);
        g.DrawString("›", arrowFont, subtitle, rect.Right - 30, rect.Top + 12);
    }

    private void ProfilesMouseDown(object? sender, MouseEventArgs e)
    {
        var index = _profiles.IndexFromPoint(e.Location);
        if (index < 0 || index >= _profiles.Items.Count) return;

        var itemBounds = _profiles.GetItemRectangle(index);
        var cardBounds = new Rectangle(itemBounds.Left + 8, itemBounds.Top + 6, itemBounds.Width - 16, itemBounds.Height - 10);
        var arrowBounds = new Rectangle(cardBounds.Right - 46, cardBounds.Top, 46, cardBounds.Height);
        if (!arrowBounds.Contains(e.Location) && e.Button != MouseButtons.Right) return;

        _profiles.SelectedIndex = index;
        if (_profiles.Items[index] is ProxyProfile profile)
        {
            ShowServerMenu(profile, new Point(arrowBounds.Left, arrowBounds.Bottom - 4));
        }
    }

    private void ShowServerMenu(ProxyProfile profile, Point location)
    {
        var menu = new ContextMenuStrip
        {
            BackColor = Color.FromArgb(42, 44, 50),
            ForeColor = TextColor,
            ShowImageMargin = false,
            Padding = new Padding(6),
            Renderer = new DarkMenuRenderer()
        };
        menu.Items.Add(T("servers.rename"), null, (_, _) => RenameProfile(profile));
        menu.Items.Add(T("servers.delete"), null, (_, _) => DeleteProfile(profile));
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        menu.Show(_profiles, location);
    }

    private void ShowServersMenu(Control anchor)
    {
        var menu = new ContextMenuStrip
        {
            BackColor = Color.FromArgb(42, 44, 50),
            ForeColor = TextColor,
            ShowImageMargin = false,
            Padding = new Padding(6),
            Renderer = new DarkMenuRenderer()
        };
        menu.Items.Add(T("servers.delete.all"), null, (_, _) => DeleteAllProfiles());
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        menu.Show(anchor, new Point(anchor.Width - menu.Width, anchor.Height - 2));
    }

    private void RenameProfile(ProxyProfile profile)
    {
        var name = PromptForServerName(profile.Name);
        if (string.IsNullOrWhiteSpace(name)) return;

        profile.Name = name.Trim();
        RefreshProfiles();
        RefreshMainPreview();
        SaveState();
    }

    private void DeleteProfile(ProxyProfile profile)
    {
        var result = MessageBox.Show(
            T("servers.delete.confirm"),
            T("servers.server"),
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (result != DialogResult.Yes) return;

        _state.Profiles.RemoveAll(x => x.Id == profile.Id);
        _state.SelectedProfileId = _state.Profiles.FirstOrDefault()?.Id;
        RefreshProfiles();
        RefreshMainPreview();
        SaveState();
    }

    private void DeleteAllProfiles()
    {
        _state.Profiles.Clear();
        _state.SelectedProfileId = null;
        RefreshProfiles();
        RefreshMainPreview();
        SaveState();
    }

    private string? PromptForServerName(string currentName)
    {
        using var dialog = new Form
        {
            Text = T("servers.name"),
            Width = 420,
            Height = 168,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            BackColor = Bg,
            ForeColor = TextColor,
            Font = Font
        };
        var input = new TextBox
        {
            Text = currentName,
            Left = 18,
            Top = 26,
            Width = 366,
            BackColor = CardBg2,
            ForeColor = TextColor,
            BorderStyle = BorderStyle.FixedSingle
        };
        var ok = LightButton("OK", () => dialog.DialogResult = DialogResult.OK, true);
        ok.Left = 108;
        ok.Top = 74;
        var cancel = LightButton(T("common.cancel"), () => dialog.DialogResult = DialogResult.Cancel);
        cancel.Left = 246;
        cancel.Top = 74;
        dialog.Controls.Add(input);
        dialog.Controls.Add(ok);
        dialog.Controls.Add(cancel);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        input.SelectAll();

        return dialog.ShowDialog(this) == DialogResult.OK ? input.Text : null;
    }

    private static void DrawCountryFlag(Graphics g, ProxyProfile profile, Rectangle bounds)
    {
        var code = CountryCodeForDisplay(profile);
        using var outline = new Pen(Color.FromArgb(82, 84, 94), 1f);
        using var path = RoundedRectPath(bounds, 4);
        g.SetClip(path);

        switch (code)
        {
            case "SE":
                using (var blue = new SolidBrush(Color.FromArgb(0, 106, 167)))
                {
                    g.FillRectangle(blue, bounds);
                }
                using (var yellow = new SolidBrush(Color.FromArgb(254, 204, 0)))
                {
                    g.FillRectangle(yellow, bounds.Left + 8, bounds.Top, 4, bounds.Height);
                    g.FillRectangle(yellow, bounds.Left, bounds.Top + 7, bounds.Width, 4);
                }
                break;
            case "FI":
                g.FillRectangle(Brushes.White, bounds);
                using (var blue = new SolidBrush(Color.FromArgb(0, 47, 108)))
                {
                    g.FillRectangle(blue, bounds.Left + 8, bounds.Top, 5, bounds.Height);
                    g.FillRectangle(blue, bounds.Left, bounds.Top + 7, bounds.Width, 5);
                }
                break;
            case "DE":
                g.FillRectangle(Brushes.Black, bounds.Left, bounds.Top, bounds.Width, bounds.Height / 3);
                g.FillRectangle(Brushes.Red, bounds.Left, bounds.Top + bounds.Height / 3, bounds.Width, bounds.Height / 3);
                g.FillRectangle(Brushes.Gold, bounds.Left, bounds.Top + bounds.Height * 2 / 3, bounds.Width, bounds.Height / 3 + 1);
                break;
            case "FR":
                g.FillRectangle(Brushes.RoyalBlue, bounds.Left, bounds.Top, bounds.Width / 3, bounds.Height);
                g.FillRectangle(Brushes.White, bounds.Left + bounds.Width / 3, bounds.Top, bounds.Width / 3, bounds.Height);
                g.FillRectangle(Brushes.Red, bounds.Left + bounds.Width * 2 / 3, bounds.Top, bounds.Width / 3 + 1, bounds.Height);
                break;
            case "NL":
                g.FillRectangle(Brushes.Firebrick, bounds.Left, bounds.Top, bounds.Width, bounds.Height / 3);
                g.FillRectangle(Brushes.White, bounds.Left, bounds.Top + bounds.Height / 3, bounds.Width, bounds.Height / 3);
                g.FillRectangle(Brushes.RoyalBlue, bounds.Left, bounds.Top + bounds.Height * 2 / 3, bounds.Width, bounds.Height / 3 + 1);
                break;
            case "US":
                for (var i = 0; i < 7; i++)
                {
                    using var stripe = new SolidBrush(i % 2 == 0 ? Color.Firebrick : Color.White);
                    g.FillRectangle(stripe, bounds.Left, bounds.Top + i * 3, bounds.Width, 3);
                }
                g.FillRectangle(Brushes.Navy, bounds.Left, bounds.Top, 12, 10);
                break;
            case "RU":
                g.FillRectangle(Brushes.White, bounds.Left, bounds.Top, bounds.Width, bounds.Height / 3);
                g.FillRectangle(Brushes.RoyalBlue, bounds.Left, bounds.Top + bounds.Height / 3, bounds.Width, bounds.Height / 3);
                g.FillRectangle(Brushes.Firebrick, bounds.Left, bounds.Top + bounds.Height * 2 / 3, bounds.Width, bounds.Height / 3 + 1);
                break;
            case "MY":
                for (var i = 0; i < 7; i++)
                {
                    using var stripe = new SolidBrush(i % 2 == 0 ? Color.Firebrick : Color.White);
                    g.FillRectangle(stripe, bounds.Left, bounds.Top + i * 3, bounds.Width, 3);
                }
                using (var blue = new SolidBrush(Color.FromArgb(1, 35, 105)))
                using (var yellow = new SolidBrush(Color.FromArgb(255, 204, 0)))
                {
                    g.FillRectangle(blue, bounds.Left, bounds.Top, 13, 10);
                    g.FillEllipse(yellow, bounds.Left + 3, bounds.Top + 2, 7, 7);
                    g.FillEllipse(blue, bounds.Left + 5, bounds.Top + 2, 7, 7);
                }
                break;
            case "GB":
                using (var blue = new SolidBrush(Color.FromArgb(1, 33, 105)))
                {
                    g.FillRectangle(blue, bounds);
                }
                using (var white = new Pen(Color.White, 5f))
                using (var red = new Pen(Color.FromArgb(200, 16, 46), 2.5f))
                {
                    g.DrawLine(white, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
                    g.DrawLine(white, bounds.Right, bounds.Top, bounds.Left, bounds.Bottom);
                    g.DrawLine(red, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
                    g.DrawLine(red, bounds.Right, bounds.Top, bounds.Left, bounds.Bottom);
                    g.FillRectangle(Brushes.White, bounds.Left + 11, bounds.Top, 6, bounds.Height);
                    g.FillRectangle(Brushes.White, bounds.Left, bounds.Top + 6, bounds.Width, 6);
                    using var redBrush = new SolidBrush(Color.FromArgb(200, 16, 46));
                    g.FillRectangle(redBrush, bounds.Left + 12, bounds.Top, 4, bounds.Height);
                    g.FillRectangle(redBrush, bounds.Left, bounds.Top + 7, bounds.Width, 4);
                }
                break;
            default:
                using (var fill = new SolidBrush(Color.FromArgb(46, 48, 58)))
                {
                    g.FillRectangle(fill, bounds);
                }
                using (var font = new Font("Segoe UI", 6.8f, FontStyle.Bold))
                using (var brush = new SolidBrush(Color.FromArgb(214, 216, 226)))
                {
                    var label = code == "UN" ? "--" : code;
                    g.DrawString(label, font, brush, bounds.Left + 4, bounds.Top + 3);
                }
                break;
        }

        g.ResetClip();
        g.DrawPath(outline, path);
    }

    private static string CountryCodeForDisplay(ProxyProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.CountryCode) && profile.CountryCode.Length == 2)
        {
            return profile.CountryCode.ToUpperInvariant();
        }

        return InferCountryCode(profile);
    }

    private static GraphicsPath RoundedRectPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        if (d <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static string InferCountryCode(ProxyProfile profile)
    {
        var source = $"{profile.Name} {profile.Server}".ToLowerInvariant();
        var tld = profile.Server.Split('.').LastOrDefault()?.ToLowerInvariant();
        var byTld = tld switch
        {
            "se" => "SE",
            "de" => "DE",
            "fr" => "FR",
            "nl" => "NL",
            "uk" or "gb" => "GB",
            "us" => "US",
            "ru" => "RU",
            "fi" => "FI",
            "pl" => "PL",
            "my" => "MY",
            _ => null
        };
        if (byTld is not null) return byTld;

        if (source.Contains("sweden") || source.Contains("stockholm")) return "SE";
        if (source.Contains("germany") || source.Contains("frankfurt")) return "DE";
        if (source.Contains("france") || source.Contains("paris")) return "FR";
        if (source.Contains("netherlands") || source.Contains("amsterdam")) return "NL";
        if (source.Contains("united kingdom") || source.Contains("london")) return "GB";
        if (source.Contains("usa") || source.Contains("united states") || source.Contains("new york")) return "US";
        if (source.Contains("russia") || source.Contains("moscow")) return "RU";
        if (source.Contains("finland") || source.Contains("helsinki")) return "FI";
        if (source.Contains("malaysia") || source.Contains("kuala lumpur")) return "MY";
        return "UN";
    }

    private async Task<bool> ImportProfilesFromTextAsync(string text)
    {
        var importText = text.Trim();
        if (LooksLikeSubscriptionUrl(importText))
        {
            try
            {
                Log($"Downloading subscription: {importText}");
                importText = await _httpClient.GetStringAsync(importText);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, T("import.download.failed"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                Log($"Subscription download failed: {ex.Message}");
                return false;
            }
        }

        var parsed = ProfileParser.ParseMany(importText);
        if (parsed.Count == 0)
        {
            MessageBox.Show(T("import.none"), T("import.title.short"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        foreach (var profile in parsed)
        {
            var existing = _state.Profiles.FindIndex(x =>
                x.Protocol == profile.Protocol &&
                x.Server.Equals(profile.Server, StringComparison.OrdinalIgnoreCase) &&
                x.Port == profile.Port &&
                (x.UserId ?? x.Password ?? x.PrivateKey) == (profile.UserId ?? profile.Password ?? profile.PrivateKey));

            if (existing >= 0) _state.Profiles[existing] = profile;
            else _state.Profiles.Add(profile);
        }

        _state.SelectedProfileId = parsed[0].Id;
        RefreshProfiles();
        RefreshMainPreview();
        SaveState();
        Log($"Imported {parsed.Count} profile(s).");
        return true;
    }

    private static bool LooksLikeSubscriptionUrl(string text)
    {
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private void RemoveSelected()
    {
        if (_profiles.SelectedItem is not ProxyProfile profile) return;
        _state.Profiles.RemoveAll(x => x.Id == profile.Id);
        _state.SelectedProfileId = _state.Profiles.FirstOrDefault()?.Id;
        RefreshProfiles();
        RefreshMainPreview();
        SaveState();
    }

    private void ToggleConnection()
    {
        if (_connectionTransitionInProgress) return;

        _connectionTransitionInProgress = true;
        _powerButton.Enabled = false;
        try
        {
            var hasTrackedProcess = TryGetTrackedSingBoxProcess(out var trackedProcess);
            trackedProcess?.Dispose();
            if (_process is { HasExited: false } || hasTrackedProcess)
            {
                StopSingBox();
            }
            else StartSingBox();
        }
        finally
        {
            _connectionTransitionInProgress = false;
            _powerButton.Enabled = true;
        }
    }

    private static void ApplyPackageCompatibilityDefaults(AppState state)
    {
        state.Windows10TunCompatibilityMode = IsWindows10Package();
        if (!state.Windows10TunCompatibilityMode) return;

        if (state.TunMtu > 1500)
        {
            state.TunMtu = 1500;
        }

        if (state.StrictRoute)
        {
            state.StrictRoute = false;
        }
    }

    private static bool IsWindows10Package()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return File.Exists(Path.Combine(baseDir, "ior.win10")) ||
               Path.GetFileName(baseDir).Equals("windows-10", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyConnectionMode(bool tunEnabled)
    {
        var wasRunning = _process is { HasExited: false };
        if (wasRunning) StopSingBox();

        _state.TunEnabled = tunEnabled;
        _state.MixedProxyEnabled = true;
        SaveState();
        RefreshMainPreview();
        Log(tunEnabled ? "Mode changed: TUN" : "Mode changed: Proxy");

        if (wasRunning) StartSingBox();
    }

    private void RecoverConnectionState()
    {
        if (TryGetTrackedSingBoxProcess(out var trackedProcess))
        {
            trackedProcess.Dispose();
            SetConnected();
            Log($"Recovered active sing-box process: PID {_state.ActiveProcessId}.");
            return;
        }

        if (_state.ActiveProcessId is not null || _state.SystemProxyApplied)
        {
            RestoreSystemProxyIfNeeded();
            SetDisconnected();
            Log("Cleared stale connection state.");
        }
        else if (IsAppSystemProxyActive())
        {
            DisableAppSystemProxy();
            Log("Disabled stale ior system proxy.");
        }
    }

    private bool TryGetTrackedSingBoxProcess(out Process process)
    {
        process = null!;
        if (_state.ActiveProcessId is not { } processId)
        {
            return TryGetSingBoxProcessByListeningPort(out process);
        }

        try
        {
            var candidate = Process.GetProcessById(processId);
            if (candidate.HasExited)
            {
                candidate.Dispose();
                return TryGetSingBoxProcessByListeningPort(out process);
            }

            if (!candidate.ProcessName.Contains("sing-box", StringComparison.OrdinalIgnoreCase))
            {
                candidate.Dispose();
                return TryGetSingBoxProcessByListeningPort(out process);
            }

            process = candidate;
            return true;
        }
        catch
        {
            return TryGetSingBoxProcessByListeningPort(out process);
        }
    }

    private bool TryGetSingBoxProcessByListeningPort(out Process process)
    {
        process = null!;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "netstat",
                Arguments = "-ano -p TCP",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var netstat = Process.Start(startInfo);
            if (netstat is null) return false;

            var output = netstat.StandardOutput.ReadToEnd();
            if (!netstat.WaitForExit(3000)) return false;

            foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.Contains($":{_state.MixedProxyPort}", StringComparison.OrdinalIgnoreCase) ||
                    !line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5 || !int.TryParse(parts[^1], out var portProcessId)) continue;

                var candidate = Process.GetProcessById(portProcessId);
                if (candidate.HasExited ||
                    !candidate.ProcessName.Contains("sing-box", StringComparison.OrdinalIgnoreCase))
                {
                    candidate.Dispose();
                    continue;
                }

                _state.ActiveProcessId = candidate.Id;
                _state.ActiveConnectionStartedUtc ??= DateTime.UtcNow;
                SaveState();
                process = candidate;
                return true;
            }
        }
        catch (Exception ex)
        {
            Log($"Port recovery failed: {ex.Message}");
        }

        return false;
    }

    private void StartSingBox()
    {
        StartSingBoxCore(allowStartupRetry: true);
    }

    private void StartSingBoxCore(bool allowStartupRetry)
    {
        if (_process is { HasExited: false })
        {
            Log("sing-box is already running.");
            return;
        }

        var profile = SelectedProfile();
        if (profile is null)
        {
            MessageBox.Show(T("start.select.profile"), T("start.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SaveSettings();
        if (!TryWriteConfig(profile)) return;
        if (!ValidateSingBoxConfig()) return;

        var startInfo = new ProcessStartInfo
        {
            FileName = _state.SingBoxPath,
            Arguments = $"run -c \"{_configPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        try
        {
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, e) => LogFromProcess(e.Data);
            _process.ErrorDataReceived += (_, e) => LogFromProcess(e.Data);
            var startedProcess = _process;
            _process.Exited += (_, _) => BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(_process, startedProcess)) return;
                var exitCode = TryGetExitCode(startedProcess);
                SetDisconnected();
                Log($"sing-box exited with code {exitCode}.");
            }));
            _process.Start();
            _state.ActiveProcessId = _process.Id;
            _state.ActiveConnectionStartedUtc = DateTime.UtcNow;
            SaveState();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            var ready = WaitForSingBoxStartup(_process, SingBoxStartupTimeoutMs);
            if (!ready)
            {
                var exited = _process.HasExited;
                var exitCode = exited ? TryGetExitCode(_process) : "still running";
                if (!exited)
                {
                    _process.Kill(entireProcessTree: true);
                    _process.WaitForExit(3000);
                }
                SetDisconnected();
                Log($"sing-box startup did not become ready ({exitCode}).");

                if (allowStartupRetry && _state.TunEnabled)
                {
                    Log("Retrying TUN startup once.");
                    Thread.Sleep(SingBoxStartupRetryDelayMs);
                    StartSingBoxCore(allowStartupRetry: false);
                    return;
                }

                MessageBox.Show(T("connection.not.ready"), T("connection.failed"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            ApplySystemProxyIfNeeded();
            SetConnected();
            Log("sing-box started.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, T("singbox.cannot.start"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool WaitForSingBoxStartup(Process process, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (process.WaitForExit(120)) return false;
            if (!_state.MixedProxyEnabled) return true;
            if (CanConnectToLocalPort(_state.MixedProxyPort)) return true;
        }

        return false;
    }

    private static bool CanConnectToLocalPort(int port)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync("127.0.0.1", port);
            return connectTask.Wait(100) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static string TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode.ToString() : "still running";
        }
        catch
        {
            return "unknown";
        }
    }

    private void StopSingBox()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
                Log("sing-box stopped.");
            }
            else if (TryGetTrackedSingBoxProcess(out var trackedProcess))
            {
                using (trackedProcess)
                {
                    trackedProcess.Kill(entireProcessTree: true);
                    trackedProcess.WaitForExit(3000);
                }
                Log("tracked sing-box process stopped.");
            }
        }
        catch (Exception ex)
        {
            Log($"Stop failed: {ex.Message}");
        }
        finally
        {
            RestoreSystemProxyIfNeeded();
            SetDisconnected();
        }
    }

    private void ApplySystemProxyIfNeeded()
    {
        if (!_state.SetSystemProxy || !_state.MixedProxyEnabled || _state.TunEnabled) return;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", writable: true);
            if (key is null) return;

            _previousProxyEnabled ??= Convert.ToInt32(key.GetValue("ProxyEnable", 0)) != 0;
            _previousProxyServer ??= key.GetValue("ProxyServer") as string;
            _previousProxyOverride ??= key.GetValue("ProxyOverride") as string;
            _state.PreviousProxyEnabled = _previousProxyEnabled;
            _state.PreviousProxyServer = _previousProxyServer;
            _state.PreviousProxyOverride = _previousProxyOverride;
            _state.SystemProxyApplied = true;

            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", $"http=127.0.0.1:{_state.MixedProxyPort};https=127.0.0.1:{_state.MixedProxyPort};socks=127.0.0.1:{_state.MixedProxyPort}", RegistryValueKind.String);
            key.SetValue("ProxyOverride", "<local>", RegistryValueKind.String);
            RefreshWindowsProxy();
            SaveState();
            Log($"Windows proxy enabled: 127.0.0.1:{_state.MixedProxyPort}");
        }
        catch (Exception ex)
        {
            Log($"Windows proxy setup failed: {ex.Message}");
        }
    }

    private void RestoreSystemProxyIfNeeded()
    {
        if (_previousProxyEnabled is null && !_state.SystemProxyApplied) return;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", writable: true);
            if (key is null) return;

            var previousProxyEnabled = _previousProxyEnabled ?? _state.PreviousProxyEnabled ?? false;
            var previousProxyServer = _previousProxyServer ?? _state.PreviousProxyServer;
            var previousProxyOverride = _previousProxyOverride ?? _state.PreviousProxyOverride;

            key.SetValue("ProxyEnable", previousProxyEnabled ? 1 : 0, RegistryValueKind.DWord);
            if (previousProxyServer is null) key.DeleteValue("ProxyServer", throwOnMissingValue: false);
            else key.SetValue("ProxyServer", previousProxyServer, RegistryValueKind.String);
            if (previousProxyOverride is null) key.DeleteValue("ProxyOverride", throwOnMissingValue: false);
            else key.SetValue("ProxyOverride", previousProxyOverride, RegistryValueKind.String);

            RefreshWindowsProxy();
            Log("Windows proxy restored.");
        }
        catch (Exception ex)
        {
            Log($"Windows proxy restore failed: {ex.Message}");
        }
        finally
        {
            _previousProxyEnabled = null;
            _previousProxyServer = null;
            _previousProxyOverride = null;
            _state.SystemProxyApplied = false;
            _state.PreviousProxyEnabled = null;
            _state.PreviousProxyServer = null;
            _state.PreviousProxyOverride = null;
            SaveState();
        }
    }

    private bool IsAppSystemProxyActive()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key is null) return false;
            var enabled = Convert.ToInt32(key.GetValue("ProxyEnable", 0)) != 0;
            var server = key.GetValue("ProxyServer") as string;
            return enabled && IsAppProxyServer(server);
        }
        catch
        {
            return false;
        }
    }

    private void DisableAppSystemProxy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", writable: true);
            if (key is null) return;
            if (!IsAppProxyServer(key.GetValue("ProxyServer") as string)) return;

            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            key.DeleteValue("ProxyServer", throwOnMissingValue: false);
            key.DeleteValue("ProxyOverride", throwOnMissingValue: false);
            RefreshWindowsProxy();
        }
        catch (Exception ex)
        {
            Log($"Stale proxy cleanup failed: {ex.Message}");
        }
    }

    private bool IsAppProxyServer(string? proxyServer)
    {
        if (string.IsNullOrWhiteSpace(proxyServer)) return false;
        return proxyServer.Contains($"127.0.0.1:{_state.MixedProxyPort}", StringComparison.OrdinalIgnoreCase) ||
               proxyServer.Contains($"localhost:{_state.MixedProxyPort}", StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    private static void RefreshWindowsProxy()
    {
        InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
    }

    private void SetConnected()
    {
        _connectedAt = _state.ActiveConnectionStartedUtc is { } startedUtc
            ? startedUtc.ToLocalTime()
            : DateTime.Now;
        _connectTimer.Start();
        _statusLabel.Text = T("status.connected");
        _statusLabel.ForeColor = Color.FromArgb(218, 218, 224);
        _timerLabel.Visible = true;
        _powerButton.IsOn = true;
        _powerButton.Invalidate();
        UpdateConnectionTimer();
    }

    private void SetDisconnected()
    {
        _connectedAt = null;
        _state.ActiveProcessId = null;
        _state.ActiveConnectionStartedUtc = null;
        SaveState();
        _connectTimer.Stop();
        _statusLabel.Text = T("status.disconnected");
        _statusLabel.ForeColor = Color.FromArgb(188, 188, 196);
        _timerLabel.Text = "00:00:00";
        _timerLabel.Visible = false;
        _powerButton.IsOn = false;
        _powerButton.Invalidate();
    }

    private void UpdateConnectionTimer()
    {
        if (_connectedAt is null) return;
        var elapsed = DateTime.Now - _connectedAt.Value;
        _timerLabel.Text = elapsed.ToString(@"hh\:mm\:ss");
    }

    private void BrowseSingBox(TextBox target)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = $"sing-box.exe|sing-box.exe|{T("dialog.exe.files")}|*.exe|{T("dialog.all.files")}|*.*",
            Title = T("dialog.singbox.title")
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            target.Text = dialog.FileName;
        }
    }

    private void SaveSettings()
    {
        SaveState();
        RefreshMainPreview();
    }

    private static string BundledSingBoxPath()
    {
        return Path.Combine(AppContext.BaseDirectory, "core", "sing-box.exe");
    }

    private static bool IsPortableBundledSingBoxPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            var fileName = Path.GetFileName(path);
            var coreDir = Path.GetDirectoryName(path);
            var appDir = coreDir is null ? null : Path.GetDirectoryName(coreDir);
            return fileName.Equals("sing-box.exe", StringComparison.OrdinalIgnoreCase) &&
                   coreDir is not null &&
                   Path.GetFileName(coreDir).Equals("core", StringComparison.OrdinalIgnoreCase) &&
                   appDir is not null &&
                   File.Exists(Path.Combine(appDir, "ior.exe"));
        }
        catch
        {
            return false;
        }
    }

    private void WriteConfig()
    {
        var profile = SelectedProfile();
        if (profile is null)
        {
            MessageBox.Show(T("config.select.profile"), T("nav.config"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        TryWriteConfig(profile);
    }

    private bool TryWriteConfig(ProxyProfile profile)
    {
        File.WriteAllText(_configPath, SingBoxConfigBuilder.Build(_state, profile));
        Log($"Config written: {_configPath}");
        return true;
    }

    private bool ValidateSingBoxConfig()
    {
        try
        {
            var check = new ProcessStartInfo
            {
                FileName = _state.SingBoxPath,
                Arguments = $"check -c \"{_configPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(check);
            if (process is null) return false;

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(10000))
            {
                process.Kill(entireProcessTree: true);
                MessageBox.Show(T("config.check.timeout"), T("config.check.failed"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                Log("sing-box check timed out.");
                return false;
            }

            foreach (var line in (output + Environment.NewLine + error).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                Log(line);
            }

            if (process.ExitCode == 0) return true;

            MessageBox.Show(T("config.rejected"), T("config.check.failed"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, T("config.check.cannot.run"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            Log($"sing-box check failed: {ex.Message}");
            return false;
        }
    }

    private void TestSelectedServer()
    {
        var profile = SelectedProfile();
        if (profile is null)
        {
            MessageBox.Show(T("start.select.profile"), T("ping.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            var latency = MeasureServerLatencyAsync(profile).GetAwaiter().GetResult();
            Log(latency is null
                ? $"Ping {profile.Server}:{profile.Port}: failed"
                : $"Ping {profile.Server}:{profile.Port}: {latency.Value} ms");
        }
        catch (Exception ex)
        {
            Log($"Ping failed: {ex.Message}");
            MessageBox.Show(ex.Message, T("ping.failed"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ResetTrafficCounters()
    {
        (_lastReceivedBytes, _lastSentBytes) = ReadTrafficBytes();
    }

    private void UpdateTrafficStats()
    {
        var (received, sent) = ReadTrafficBytes();
        var receivedDelta = Math.Max(0, received - _lastReceivedBytes);
        var sentDelta = Math.Max(0, sent - _lastSentBytes);
        _lastReceivedBytes = received;
        _lastSentBytes = sent;

        _downloadSpeedValue.Text = FormatBytesPerSecond(receivedDelta);
        _uploadSpeedValue.Text = FormatBytesPerSecond(sentDelta);
    }

    private static (long received, long sent) ReadTrafficBytes()
    {
        long received = 0;
        long sent = 0;
        foreach (var adapter in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (adapter.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
            var stats = adapter.GetIPv4Statistics();
            received += stats.BytesReceived;
            sent += stats.BytesSent;
        }

        return (received, sent);
    }

    private static string FormatBytesPerSecond(long bytes)
    {
        if (bytes >= 1024L * 1024L) return $"{bytes / 1024d / 1024d:0.0} MB/s";
        return $"{bytes / 1024d:0} KB/s";
    }

    private async Task UpdatePingAndServicesAsync()
    {
        if (_serviceCheckRunning) return;
        if ((DateTime.UtcNow - _lastServiceCheckUtc).TotalSeconds < 15) return;
        _serviceCheckRunning = true;
        _lastServiceCheckUtc = DateTime.UtcNow;

        try
        {
            var profile = SelectedProfile();
            if (profile is not null)
            {
                try
                {
                    var latency = await MeasureServerLatencyAsync(profile);
                    _lastLatencyMs = latency;
                    _pingValue.Text = latency is null ? "-" : $"{latency.Value} ms";
                }
                catch
                {
                    _lastLatencyMs = null;
                    _pingValue.Text = "-";
                }
            }

            _healthyServiceCount = 0;
            _checkedServiceCount = 0;
            await UpdateServiceStatusAsync("Discord", "https://discord.com");
            await UpdateServiceStatusAsync("YouTube", "https://www.youtube.com");
            await UpdateServiceStatusAsync("Telegram", "https://telegram.org");
            UpdateConnectionQuality(profile);
        }
        finally
        {
            _serviceCheckRunning = false;
        }
    }

    private static async Task<long?> MeasureServerLatencyAsync(ProxyProfile profile)
    {
        if (profile.Protocol == ProxyProtocol.WireGuard)
        {
            return await MeasureIcmpLatencyAsync(profile.Server);
        }

        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var sw = Stopwatch.StartNew();
            await client.ConnectAsync(profile.Server, profile.Port).WaitAsync(TimeSpan.FromSeconds(3));
            sw.Stop();
            return Math.Max(1, sw.ElapsedMilliseconds);
        }
        catch
        {
            return await MeasureIcmpLatencyAsync(profile.Server);
        }
    }

    private static async Task<long?> MeasureIcmpLatencyAsync(string host)
    {
        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            var reply = await ping.SendPingAsync(host, 2500);
            return reply.Status == System.Net.NetworkInformation.IPStatus.Success
                ? Math.Max(1, reply.RoundtripTime)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task UpdateServiceStatusAsync(string serviceName, string url)
    {
        if (!_serviceStatusLabels.TryGetValue(serviceName, out var label)) return;

        try
        {
            var ok = await ProbeServiceAsync(url);
            label.Text = ok ? "OK" : "!";
            label.ForeColor = ok ? Color.FromArgb(92, 220, 148) : Color.FromArgb(255, 190, 92);
            _checkedServiceCount++;
            if (ok) _healthyServiceCount++;
        }
        catch
        {
            label.Text = "!";
            label.ForeColor = Color.FromArgb(255, 116, 116);
            _checkedServiceCount++;
        }
    }

    private async Task<bool> ProbeServiceAsync(string url)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        if (await TryProbeServiceAsync(HttpMethod.Head, url, cts.Token) is { } headResult)
        {
            if (headResult) return true;
        }

        return await TryProbeServiceAsync(HttpMethod.Get, url, cts.Token) ?? false;
    }

    private async Task<bool?> TryProbeServiceAsync(HttpMethod method, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.TryAddWithoutValidation("User-Agent", $"ior/{AppVersion}");
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return (int)response.StatusCode < 500;
        }
        catch
        {
            return null;
        }
    }

    private void UpdateConnectionQuality(ProxyProfile? profile = null)
    {
        if (profile?.Protocol == ProxyProtocol.WireGuard && IsSingBoxRunning())
        {
            _qualityValue.Text = T("quality.good");
            _qualityValue.ForeColor = Color.FromArgb(92, 220, 148);
            return;
        }

        var latencyScore = _lastLatencyMs switch
        {
            null => 0,
            <= 120 => 2,
            <= 260 => 1,
            _ => 0
        };
        var serviceScore = _checkedServiceCount == 0
            ? 0
            : _healthyServiceCount * 2 >= _checkedServiceCount * 2 ? 2 :
              _healthyServiceCount > 0 ? 1 : 0;
        var score = latencyScore + serviceScore;

        if (score >= 4)
        {
            _qualityValue.Text = T("quality.good");
            _qualityValue.ForeColor = Color.FromArgb(92, 220, 148);
        }
        else if (score >= 2)
        {
            _qualityValue.Text = T("quality.fair");
            _qualityValue.ForeColor = Color.FromArgb(255, 190, 92);
        }
        else
        {
            _qualityValue.Text = T("quality.poor");
            _qualityValue.ForeColor = Color.FromArgb(255, 116, 116);
        }
    }

    private bool IsSingBoxRunning()
    {
        if (_process is { HasExited: false }) return true;
        if (TryGetTrackedSingBoxProcess(out var process))
        {
            process.Dispose();
            return true;
        }

        return false;
    }

    private void RefreshProfiles()
    {
        var source = _state.Profiles;

        _profiles.DataSource = null;
        _profiles.DataSource = source;
        _profiles.DisplayMember = nameof(ProxyProfile.DisplayName);
        var selected = source.FindIndex(x => x.Id == _state.SelectedProfileId);
        if (selected >= 0) _profiles.SelectedIndex = selected;
        var hasProfiles = source.Count > 0;
        _profiles.Visible = hasProfiles;
        _emptyStatePanel.Visible = !hasProfiles;
        if (hasProfiles) HideInlineImport();
        QueueCountryLookups(source);
    }

    private void QueueCountryLookups(IEnumerable<ProxyProfile> profiles)
    {
        foreach (var profile in profiles)
        {
            if (!ShouldLookupCountry(profile)) continue;
            _ = LookupCountryAsync(profile);
        }
    }

    private bool ShouldLookupCountry(ProxyProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Server)) return false;
        if (_countryLookupsInFlight.Contains(profile.Id)) return false;
        if (!string.IsNullOrWhiteSpace(profile.CountryCode) &&
            profile.CountryLookupUtc is { } lookupUtc &&
            DateTime.UtcNow - lookupUtc < TimeSpan.FromDays(30))
        {
            return false;
        }

        return profile.CountryLookupUtc is null ||
               DateTime.UtcNow - profile.CountryLookupUtc.Value > TimeSpan.FromHours(6);
    }

    private async Task LookupCountryAsync(ProxyProfile profile)
    {
        _countryLookupsInFlight.Add(profile.Id);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var url = $"http://ip-api.com/json/{Uri.EscapeDataString(profile.Server)}?fields=status,country,countryCode,message";
            using var response = await _httpClient.GetAsync(url, cts.Token);
            if (!response.IsSuccessStatusCode) return;

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token);
            var root = doc.RootElement;
            var statusText = root.TryGetProperty("status", out var status) ? status.GetString() : null;
            if (!string.Equals(statusText, "success", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var countryCode = root.TryGetProperty("countryCode", out var codeElement) ? codeElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(countryCode) || countryCode.Length != 2) return;

            profile.CountryCode = countryCode.ToUpperInvariant();
            profile.CountryName = root.TryGetProperty("country", out var countryElement) ? countryElement.GetString() : null;
            profile.CountryLookupUtc = DateTime.UtcNow;
            SaveState();
            _profiles.Invalidate();
        }
        catch (Exception ex)
        {
            Log($"Country lookup failed for {profile.Server}: {ex.Message}");
        }
        finally
        {
            _countryLookupsInFlight.Remove(profile.Id);
        }
    }

    private void RefreshMainPreview()
    {
        var profile = SelectedProfile();
        _selectedName.Text = profile?.Name ?? T("servers.none");
        _selectedProtocol.Text = profile is null ? T("servers.add.subscription") : ProtocolLabel(profile);
        _selectedServer.Text = profile is null ? "" : $"{profile.Server}:{profile.Port}";
        Invalidate(true);
    }

    private ProxyProfile? SelectedProfile()
    {
        return _state.Profiles.FirstOrDefault(x => x.Id == _state.SelectedProfileId) ??
               _profiles.SelectedItem as ProxyProfile ??
               _state.Profiles.FirstOrDefault();
    }

    private static string ProtocolLabel(ProxyProfile profile)
    {
        return profile.Protocol switch
        {
            ProxyProtocol.Vless when profile.RealityEnabled => "VLESS / TCP / REALITY",
            ProxyProtocol.Vless when profile.TlsEnabled => "VLESS / TCP / TLS",
            ProxyProtocol.Vless => "VLESS / TCP",
            ProxyProtocol.Hysteria2 => "HYSTERIA2 / TLS",
            ProxyProtocol.Shadowsocks => "SHADOWSOCKS",
            ProxyProtocol.WireGuard => "WIREGUARD",
            _ => profile.Protocol.ToString().ToUpperInvariant()
        };
    }

    private async void ImportFromClipboard()
    {
        try
        {
            var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
            if (string.IsNullOrWhiteSpace(text))
            {
                MessageBox.Show(T("import.clipboard.empty"), T("import.title.short"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            await ImportProfilesFromTextAsync(text);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, T("import.clipboard"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowInlineImport()
    {
        SetServersExpanded(true);
        using var dialog = BuildImportDialog(out var linksBox, out var wireGuardBox);
        var addButton = dialog.Controls.Find("AddImportButton", true).OfType<Button>().FirstOrDefault();
        if (addButton is not null)
        {
            addButton.Click += async (_, _) =>
            {
                var imported = false;
                var links = linksBox.Text.Trim();
                var wireGuardConfig = wireGuardBox.Text.Trim();

                if (!string.IsNullOrWhiteSpace(links))
                {
                    imported |= await ImportProfilesFromTextAsync(links);
                }

                if (!string.IsNullOrWhiteSpace(wireGuardConfig))
                {
                    imported |= await ImportProfilesFromTextAsync(wireGuardConfig);
                }

                if (imported)
                {
                    dialog.DialogResult = DialogResult.OK;
                    dialog.Close();
                }
            };
        }

        dialog.ShowDialog(this);
    }

    private void SetServersExpanded(bool expanded)
    {
        _serversExpanded = expanded;
        if (!_serverContentHost.IsDisposed) _serverContentHost.Visible = expanded;

        if (_serverListLayout is not null && _serverListLayout.RowStyles.Count > 1)
        {
            _serverListLayout.RowStyles[1].SizeType = expanded ? SizeType.Percent : SizeType.Absolute;
            _serverListLayout.RowStyles[1].Height = expanded ? 100 : 0;
            _serverListLayout.PerformLayout();
        }

        if (_serverExpandButton is not null && !_serverExpandButton.IsDisposed)
        {
            _serverExpandButton.Expanded = expanded;
        }
    }

    private void HideInlineImport()
    {
        if (_inlineImportPanel.IsDisposed) return;
        _inlineImportPanel.Visible = false;
    }

    private Form BuildImportDialog(out TextBox linksBox, out TextBox wireGuardBox)
    {
        var dialog = new Form
        {
            Text = T("import.title"),
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Width = 560,
            Height = 500,
            BackColor = Bg,
            ShowInTaskbar = false,
            Font = Font,
            Padding = new Padding(18)
        };
        dialog.Location = new Point(
            Left + Math.Max(0, (Width - dialog.Width) / 2),
            Top + Math.Max(0, (Height - dialog.Height) / 2));

        var shell = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            BackColor = CardBg2,
            BorderColor = Color.FromArgb(82, 84, 92),
            Radius = 10,
            Padding = new Padding(22)
        };
        dialog.Controls.Add(shell);

        var viewport = new ImportScrollPanel
        {
            Dock = DockStyle.Fill,
            BackColor = CardBg2
        };
        shell.Controls.Add(viewport);

        var layout = new TableLayoutPanel
        {
            Width = 486,
            Height = 470,
            BackColor = CardBg2,
            ColumnCount = 1,
            RowCount = 7
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        viewport.Content = layout;

        layout.Controls.Add(new Label
        {
            Text = T("import.title"),
            Dock = DockStyle.Fill,
            ForeColor = TextColor,
            Font = new Font("Segoe UI", 17f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        layout.Controls.Add(ImportFieldLabel(T("import.links")), 0, 1);
        var linksInput = new TextBox
        {
            Multiline = false,
            WordWrap = false,
            PlaceholderText = T("import.placeholder")
        };
        linksBox = linksInput;
        StyleInput(linksBox);
        layout.Controls.Add(ImportLinkTextInput(linksBox), 0, 2);

        layout.Controls.Add(ImportFieldLabel("WireGuard config"), 0, 3);
        var wireGuardInput = new TextBox
        {
            Multiline = true,
            AcceptsReturn = true,
            Font = new Font("Consolas", 9.5f),
            WordWrap = false,
            PlaceholderText = "[Interface]\r\nPrivateKey = ...\r\n\r\n[Peer]\r\nPublicKey = ...\r\nEndpoint = host:51820"
        };
        wireGuardBox = wireGuardInput;
        StyleInput(wireGuardBox);
        EnableWireGuardConfigDrop(wireGuardBox);
        layout.Controls.Add(ImportTextInput(wireGuardBox, height: 158), 0, 4);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = CardBg2,
            Padding = new Padding(0, 6, 0, 0)
        };
        var add = LightButton(T("servers.add"), () => { }, primary: true);
        add.Name = "AddImportButton";
        var cancel = LightButton(T("common.cancel"), () => dialog.Close());
        actions.Controls.Add(add);
        actions.Controls.Add(cancel);
        layout.Controls.Add(actions, 0, 6);

        dialog.Shown += (_, _) => linksInput.Focus();
        return dialog;
    }

    private static Label ImportFieldLabel(string text)
    {
        return new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            ForeColor = Muted,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.BottomLeft,
            Padding = new Padding(4, 0, 0, 4)
        };
    }

    private static RoundedPanel ImportTextInput(TextBox box, int height)
    {
        var wrap = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            Height = height,
            BackColor = CardBg2,
            BorderColor = Color.FromArgb(82, 84, 92),
            Radius = 6,
            Margin = new Padding(0, 6, 0, 6),
            Padding = new Padding(10, box.Multiline ? 8 : 9, 10, 8)
        };
        box.Dock = DockStyle.Fill;
        box.Margin = Padding.Empty;
        box.ScrollBars = ScrollBars.None;
        wrap.Controls.Add(box);
        return wrap;
    }

    private static RoundedPanel ImportLinkTextInput(TextBox box)
    {
        var wrap = new RoundedPanel
        {
            Dock = DockStyle.Fill,
            Height = 50,
            BackColor = CardBg2,
            BorderColor = Color.FromArgb(82, 84, 92),
            Radius = 6,
            Margin = new Padding(0, 6, 0, 6),
            Padding = new Padding(10, 7, 10, 6)
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = CardBg2,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 7));

        box.Dock = DockStyle.Fill;
        box.Margin = Padding.Empty;
        box.ScrollBars = ScrollBars.None;
        box.Multiline = false;
        box.WordWrap = false;

        var scroll = new DarkHorizontalScrollBar
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 1, 0, 0)
        };

        var currentOffset = 0;
        var averageCharWidth = Math.Max(1, TextRenderer.MeasureText("m", box.Font).Width);

        int VisibleChars()
        {
            return Math.Max(1, box.ClientSize.Width / averageCharWidth);
        }

        void UpdateScroll()
        {
            scroll.Maximum = Math.Max(0, box.TextLength - VisibleChars());
            scroll.Value = Math.Min(scroll.Maximum, currentOffset);
        }

        void ScrollTo(int value)
        {
            var next = Math.Clamp(value, 0, scroll.Maximum);
            var delta = next - currentOffset;
            if (delta == 0) return;

            SendMessage(box.Handle, EmLineScroll, new IntPtr(delta), 0);
            currentOffset = next;
            scroll.Value = currentOffset;
        }

        scroll.ScrollRequested += (_, value) => ScrollTo(value);
        box.TextChanged += (_, _) =>
        {
            currentOffset = 0;
            UpdateScroll();
        };
        box.Resize += (_, _) => UpdateScroll();
        box.HandleCreated += (_, _) => box.BeginInvoke((Action)UpdateScroll);

        layout.Controls.Add(box, 0, 0);
        layout.Controls.Add(scroll, 0, 1);
        wrap.Controls.Add(layout);
        return wrap;
    }

    private static void EnableWireGuardConfigDrop(TextBox target)
    {
        target.AllowDrop = true;
        target.DragEnter += (_, e) =>
        {
            e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ||
                       e.Data?.GetDataPresent(DataFormats.Text) == true
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        };
        target.DragDrop += (_, e) =>
        {
            try
            {
                if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    target.Text = File.ReadAllText(files[0]);
                    return;
                }

                if (e.Data?.GetData(DataFormats.Text) is string text)
                {
                    target.Text = text;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "WireGuard config", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
    }

    private static AppState LoadState(string path)
    {
        try
        {
            var state = File.Exists(path)
                ? JsonSerializer.Deserialize<AppState>(File.ReadAllText(path), JsonOptions()) ?? new AppState()
                : new AppState();
            SeedDefaultRoutingSites(state);
            return state;
        }
        catch
        {
            var state = new AppState();
            SeedDefaultRoutingSites(state);
            return state;
        }
    }

    private static void SeedDefaultRoutingSites(AppState state)
    {
        if (state.RoutingDefaultsSeeded) return;

        state.RoutingBypassSites = state.RoutingBypassSites
            .Concat(DefaultRoutingBypassSites)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        state.RoutingDefaultsSeeded = true;
    }

    private void SaveState()
    {
        File.WriteAllText(_statePath, JsonSerializer.Serialize(_state, JsonOptions()));
    }

    private void ExportLayoutSettings()
    {
        using var dialog = new SaveFileDialog
        {
            Title = T("layout.export.title"),
            Filter = T("layout.filter"),
            FileName = "ior-layout.json"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var layout = CurrentLayoutSettings();
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(layout, JsonOptions()));
    }

    private bool ImportLayoutSettings()
    {
        using var dialog = new OpenFileDialog
        {
            Title = T("layout.import.title"),
            Filter = T("layout.filter")
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;

        try
        {
            var layout = JsonSerializer.Deserialize<LayoutSettings>(File.ReadAllText(dialog.FileName), JsonOptions());
            if (layout is null) return false;

            _state.Theme = layout.Theme;
            _state.AccentColorArgb = layout.AccentColorArgb;
            _state.NavPanelPosition = layout.NavPanelPosition;
            _state.NavIconSize = layout.NavIconSize;
            _state.NavItemOrder = NormalizeNavItemOrder(layout.NavItemOrder);
            _state.HiddenNavItems = NormalizeHiddenNavItems(layout.HiddenNavItems);
            _state.ShowNavTooltips = layout.ShowNavTooltips;
            _state.ShowNavSeparators = layout.ShowNavSeparators;
            _state.UsePanelGlass = layout.UsePanelGlass;
            NormalizeNavCustomization();
            SaveState();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, T("layout.import.error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private LayoutSettings CurrentLayoutSettings()
    {
        NormalizeNavCustomization();
        return new LayoutSettings
        {
            Theme = _state.Theme,
            AccentColorArgb = _state.AccentColorArgb,
            NavPanelPosition = _state.NavPanelPosition,
            NavIconSize = _state.NavIconSize,
            NavItemOrder = _state.NavItemOrder.ToList(),
            HiddenNavItems = _state.HiddenNavItems.ToList(),
            ShowNavTooltips = _state.ShowNavTooltips,
            ShowNavSeparators = _state.ShowNavSeparators,
            UsePanelGlass = _state.UsePanelGlass
        };
    }

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
    }

    private void LogFromProcess(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line)) BeginInvoke(new Action(() => Log(line)));
    }

    private void Log(string message)
    {
        _logs.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        if (_logs.Count > 600) _logs.RemoveRange(0, _logs.Count - 600);
    }

    private void DragWindow(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0xA1, 0x2, 0);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    private enum WindowButtonKind
    {
        Minimize,
        Maximize,
        Close
    }

    private enum AppView
    {
        Servers,
        Settings,
        Config,
        Logs,
        Info
    }

    private sealed record ReleaseInfo(string Tag, string ReleaseUrl, string? AssetName, string? AssetDownloadUrl);

    private sealed class UpdateDialog : Form
    {
        private readonly Label _statusLabel = new();
        private readonly Button _laterButton;
        private readonly Button _updateButton;
        private readonly System.Windows.Forms.Timer _fadeTimer = new() { Interval = 15 };

        public event EventHandler? UpdateRequested;

        public UpdateDialog(string version, bool canInstall, AppLanguage language)
        {
            var ru = language == AppLanguage.Russian;
            Text = "ior update";
            Width = 456;
            Height = 246;
            MinimumSize = new Size(456, 246);
            MaximumSize = new Size(456, 246);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(18, 20, 20);
            ForeColor = TextColor;
            Font = new Font("Segoe UI", 9.5f);
            Opacity = 0;

            var card = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                Radius = 12,
                BackColor = Color.FromArgb(30, 32, 38),
                BorderColor = Color.FromArgb(84, 86, 98),
                BorderWidth = 1,
                Padding = new Padding(24, 22, 24, 18)
            };

            var title = new Label
            {
                Text = ru ? "Доступно обновление" : "Update available",
                Dock = DockStyle.Top,
                Height = 34,
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 15f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };

            var text = new Label
            {
                Text = canInstall
                    ? ru
                        ? $"Новая версия {version} готова к установке. Приложение скачает нужную сборку и перезапустится."
                        : $"Version {version} is ready to install. The app will download the correct build and restart."
                    : ru
                        ? $"Новая версия {version} доступна, но подходящий ZIP-архив не найден в GitHub Release."
                        : $"Version {version} is available, but a matching ZIP archive was not found in the GitHub Release.",
                Dock = DockStyle.Top,
                Height = 58,
                ForeColor = Color.FromArgb(218, 218, 224),
                BackColor = Color.Transparent,
                Font = new Font("Segoe UI", 9.8f),
                TextAlign = ContentAlignment.MiddleLeft
            };

            _statusLabel.Text = canInstall
                ? ru ? "Можно обновиться сейчас или напомнить позже." : "You can update now or be reminded later."
                : ru ? "Откроется страница релиза." : "The release page will open.";
            _statusLabel.Dock = DockStyle.Top;
            _statusLabel.Height = 32;
            _statusLabel.ForeColor = Muted;
            _statusLabel.BackColor = Color.Transparent;
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft;

            var buttons = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 42,
                ColumnCount = 2,
                BackColor = Color.Transparent
            };
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            _laterButton = DialogButton(ru ? "Напомнить позже" : "Remind me later", primary: false);
            _updateButton = DialogButton(
                canInstall
                    ? ru ? "Обновить сейчас" : "Update now"
                    : ru ? "Открыть релиз" : "Open release",
                primary: true);
            _laterButton.Click += (_, _) =>
            {
                DialogResult = DialogResult.Cancel;
                Close();
            };
            _updateButton.Click += (_, _) =>
            {
                UpdateRequested?.Invoke(this, EventArgs.Empty);
            };

            buttons.Controls.Add(_laterButton, 0, 0);
            buttons.Controls.Add(_updateButton, 1, 0);
            card.Controls.Add(buttons);
            card.Controls.Add(_statusLabel);
            card.Controls.Add(text);
            card.Controls.Add(title);
            Controls.Add(card);

            _fadeTimer.Tick += (_, _) =>
            {
                Opacity = Math.Min(1, Opacity + 0.08);
                if (Opacity >= 1) _fadeTimer.Stop();
            };
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _fadeTimer.Start();
        }

        public void SetBusy(string text)
        {
            _statusLabel.Text = text;
            _laterButton.Enabled = false;
            _updateButton.Enabled = false;
            Cursor = Cursors.WaitCursor;
            Application.DoEvents();
        }

        public void SetError(string text)
        {
            _statusLabel.Text = text;
            _statusLabel.ForeColor = Color.FromArgb(255, 130, 130);
            _laterButton.Enabled = true;
            _updateButton.Enabled = true;
            Cursor = Cursors.Default;
        }

        private static Button DialogButton(string text, bool primary)
        {
            return new Button
            {
                Text = text,
                Dock = DockStyle.Fill,
                Height = 38,
                Margin = primary ? new Padding(8, 0, 0, 0) : new Padding(0, 0, 8, 0),
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Accent : Color.FromArgb(48, 50, 58),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
        }
    }

    private sealed class LayoutSettings
    {
        public AppTheme Theme { get; set; } = AppTheme.Dark;
        public int AccentColorArgb { get; set; } = unchecked((int)0xFF7C69FF);
        public NavPanelPosition NavPanelPosition { get; set; } = NavPanelPosition.Left;
        public NavIconSize NavIconSize { get; set; } = NavIconSize.Normal;
        public List<string> NavItemOrder { get; set; } = ["servers", "settings", "config", "logs", "info"];
        public List<string> HiddenNavItems { get; set; } = [];
        public bool ShowNavTooltips { get; set; } = true;
        public bool ShowNavSeparators { get; set; } = true;
        public bool UsePanelGlass { get; set; } = true;
    }

    private sealed class NavSeparator : Control
    {
        public NavPanelPosition PanelPosition { get; set; } = NavPanelPosition.Left;

        public NavSeparator()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint, true);
            BackColor = NavBg;
            Margin = Padding.Empty;
            Dock = DockStyle.Fill;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Color.FromArgb(80, Line), 1f);
            if (PanelPosition == NavPanelPosition.Top)
            {
                var x = Width / 2;
                e.Graphics.DrawLine(pen, x, 14, x, Math.Max(14, Height - 14));
            }
            else
            {
                var y = Height / 2;
                e.Graphics.DrawLine(pen, 18, y, Math.Max(18, Width - 18), y);
            }
        }
    }

    private sealed class TitleBarPanel : Panel
    {
        public WindowButtonKind? HoveredButton { get; set; }
        public WindowButtonKind? PressedButton { get; set; }

        public TitleBarPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            DrawCaptionButton(e.Graphics, CaptionButtonBounds(this, WindowButtonKind.Minimize), WindowButtonKind.Minimize, HoveredButton == WindowButtonKind.Minimize, PressedButton == WindowButtonKind.Minimize);
            DrawCaptionButton(e.Graphics, CaptionButtonBounds(this, WindowButtonKind.Maximize), WindowButtonKind.Maximize, HoveredButton == WindowButtonKind.Maximize, PressedButton == WindowButtonKind.Maximize);
            DrawCaptionButton(e.Graphics, CaptionButtonBounds(this, WindowButtonKind.Close), WindowButtonKind.Close, HoveredButton == WindowButtonKind.Close, PressedButton == WindowButtonKind.Close);
        }
    }

    private sealed class WindowCaptionButton : Control
    {
        private bool _hover;
        private bool _pressed;

        public WindowButtonKind Kind { get; set; }

        public WindowCaptionButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint, true);
            BackColor = NavBg;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            _pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _pressed = true;
                Invalidate();
            }

            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _pressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var bg = NavBg;
            if (_hover) bg = Kind == WindowButtonKind.Close ? Color.FromArgb(196, 43, 28) : Color.FromArgb(35, 37, 37);
            if (_pressed) bg = Kind == WindowButtonKind.Close ? Color.FromArgb(160, 32, 22) : Color.FromArgb(45, 47, 47);

            e.Graphics.Clear(bg);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var pen = new Pen(Color.White, 1.7f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };

            var cx = Width / 2;
            var cy = Height / 2;
            switch (Kind)
            {
                case WindowButtonKind.Minimize:
                    e.Graphics.DrawLine(pen, cx - 6, cy + 5, cx + 6, cy + 5);
                    break;
                case WindowButtonKind.Maximize:
                    e.Graphics.DrawRectangle(pen, cx - 6, cy - 5, 12, 10);
                    break;
                case WindowButtonKind.Close:
                    e.Graphics.DrawLine(pen, cx - 5, cy - 5, cx + 5, cy + 5);
                    e.Graphics.DrawLine(pen, cx + 5, cy - 5, cx - 5, cy + 5);
                    break;
            }
        }
    }

    private sealed class SideNavButton : Control
    {
        private const float HoverStep = 0.12f;
        private float _hoverProgress;
        private float _hoverTarget;
        private float _activeProgress;
        private float _activeTarget;
        private readonly System.Windows.Forms.Timer _hoverTimer;
        private bool _active;

        public string IconText { get; set; } = "";
        public bool Active
        {
            get => _active;
            set
            {
                if (_active == value) return;
                _active = value;
                _activeTarget = value ? 1f : 0f;
                _hoverTimer.Start();
            }
        }
        public NavPanelPosition PanelPosition { get; set; } = NavPanelPosition.Left;
        public float IconScale { get; set; } = 1f;

        public SideNavButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            _hoverTimer = new System.Windows.Forms.Timer { Interval = 16 };
            _hoverTimer.Tick += (_, _) => AnimateHover();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hoverTarget = 1f;
            _hoverTimer.Start();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hoverTarget = 0f;
            _hoverTimer.Start();
            base.OnMouseLeave(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hoverTimer.Dispose();
            }

            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var rect = new Rectangle(0, 0, Width, Height);
            var activeAmount = Math.Max(_activeProgress, Active ? 0.18f : 0f);
            var hoverAmount = Math.Max(_hoverProgress, activeAmount * 0.35f);

            if (Active || hoverAmount > 0.01f || activeAmount > 0.01f)
            {
                var alpha = (int)(20 + 20 * hoverAmount + 18 * activeAmount);
                using var brush = new SolidBrush(Color.FromArgb(alpha, Color.White));
                using var path = RoundedRect(Rectangle.Inflate(rect, -4, -4), 8);
                g.FillPath(brush, path);
            }

            if (hoverAmount > 0.01f)
            {
                using var glowBrush = new SolidBrush(Color.FromArgb((int)(34 * hoverAmount), Accent));
                g.FillEllipse(glowBrush, Width / 2 - 16, Height / 2 - 16, 32, 32);
            }

            if (activeAmount > 0.01f)
            {
                using var marker = new SolidBrush(Color.FromArgb((int)(255 * activeAmount), Accent));
                if (PanelPosition == NavPanelPosition.Top)
                {
                    var markerWidth = (int)(26 * activeAmount);
                    g.FillRectangle(marker, Width / 2 - markerWidth / 2, Height - 4, markerWidth, 3);
                }
                else
                {
                    var markerHeight = (int)(26 * activeAmount);
                    g.FillRectangle(marker, 0, Height / 2 - markerHeight / 2, 3, markerHeight);
                }
            }

            var iconColor = Active ? Color.White : Blend(Muted, Color.White, hoverAmount);
            var state = g.Save();
            var scale = IconScale * (1f + 0.055f * hoverAmount + 0.03f * activeAmount);
            g.TranslateTransform(Width / 2f, Height / 2f - 2.5f * hoverAmount);
            g.ScaleTransform(scale, scale);
            g.TranslateTransform(-Width / 2f, -Height / 2f);
            DrawNavIcon(g, IconText, rect, iconColor);
            g.Restore(state);
        }

        private void AnimateHover()
        {
            _hoverProgress += (_hoverTarget - _hoverProgress) * HoverStep;
            _activeProgress += (_activeTarget - _activeProgress) * HoverStep;
            if (Math.Abs(_hoverTarget - _hoverProgress) < 0.01f)
            {
                _hoverProgress = _hoverTarget;
            }
            if (Math.Abs(_activeTarget - _activeProgress) < 0.01f)
            {
                _activeProgress = _activeTarget;
            }
            if (_hoverProgress == _hoverTarget && _activeProgress == _activeTarget) _hoverTimer.Stop();

            Invalidate();
        }

        private static Color Blend(Color from, Color to, float amount)
        {
            amount = Math.Clamp(amount, 0f, 1f);
            return Color.FromArgb(
                (int)(from.A + (to.A - from.A) * amount),
                (int)(from.R + (to.R - from.R) * amount),
                (int)(from.G + (to.G - from.G) * amount),
                (int)(from.B + (to.B - from.B) * amount));
        }

        private static void DrawNavIcon(Graphics g, string icon, Rectangle bounds, Color color)
        {
            var cx = bounds.Left + bounds.Width / 2f;
            var cy = bounds.Top + bounds.Height / 2f;
            using var pen = new Pen(color, 2.1f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            using var brush = new SolidBrush(color);

            switch (icon)
            {
                case "+":
                    g.DrawLine(pen, cx - 8, cy, cx + 8, cy);
                    g.DrawLine(pen, cx, cy - 8, cx, cy + 8);
                    break;
                case "→":
                    g.DrawLine(pen, cx - 9, cy, cx + 8, cy);
                    g.DrawLine(pen, cx + 8, cy, cx + 2, cy - 6);
                    g.DrawLine(pen, cx + 8, cy, cx + 2, cy + 6);
                    break;
                case "◎":
                    g.DrawEllipse(pen, cx - 9, cy - 9, 18, 18);
                    g.FillEllipse(brush, cx - 3, cy - 3, 6, 6);
                    break;
                case "⚙":
                    g.DrawEllipse(pen, cx - 7, cy - 7, 14, 14);
                    g.DrawEllipse(pen, cx - 2, cy - 2, 4, 4);
                    for (var i = 0; i < 8; i++)
                    {
                        var angle = Math.PI * 2 * i / 8;
                        var x1 = cx + Math.Cos(angle) * 10;
                        var y1 = cy + Math.Sin(angle) * 10;
                        var x2 = cx + Math.Cos(angle) * 13;
                        var y2 = cy + Math.Sin(angle) * 13;
                        g.DrawLine(pen, (float)x1, (float)y1, (float)x2, (float)y2);
                    }
                    break;
                case "config-box":
                    var boxTop = cy - 4;
                    var boxLeft = cx - 11;
                    var boxRight = cx + 11;
                    var boxBottom = cy + 11;
                    g.DrawLine(pen, cx, cy - 13, boxRight, cy - 7);
                    g.DrawLine(pen, cx, cy - 13, boxLeft, cy - 7);
                    g.DrawLine(pen, boxLeft, cy - 7, cx, boxTop);
                    g.DrawLine(pen, boxRight, cy - 7, cx, boxTop);
                    g.DrawLine(pen, boxLeft, cy - 7, boxLeft, cy + 6);
                    g.DrawLine(pen, boxRight, cy - 7, boxRight, cy + 6);
                    g.DrawLine(pen, boxLeft, cy + 6, cx, boxBottom);
                    g.DrawLine(pen, boxRight, cy + 6, cx, boxBottom);
                    g.DrawLine(pen, cx, boxTop, cx, boxBottom);
                    break;
                case "logs-list":
                    using (var pagePath = RoundedRect(new Rectangle((int)cx - 10, (int)cy - 12, 20, 24), 3))
                    {
                        g.DrawPath(pen, pagePath);
                    }
                    g.DrawLine(pen, cx - 5, cy - 5, cx + 5, cy - 5);
                    g.DrawLine(pen, cx - 5, cy, cx + 5, cy);
                    g.DrawLine(pen, cx - 5, cy + 5, cx + 2, cy + 5);
                    break;
                case "i":
                    g.FillEllipse(brush, cx - 1.6f, cy - 10, 3.2f, 3.2f);
                    g.DrawLine(pen, cx, cy - 3, cx, cy + 9);
                    break;
                default:
                    using (var font = new Font("Segoe UI Symbol", 17f, FontStyle.Regular))
                    {
                        TextRenderer.DrawText(g, icon, font, bounds, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                    }
                    break;
            }
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            if (d <= 0) { path.AddRectangle(bounds); return path; }
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class NavCustomizationPreview : Panel
    {
        private const int ItemGap = 6;
        private readonly Dictionary<string, SideNavButton> _buttons = [];
        private readonly List<string> _order;
        private readonly System.Windows.Forms.Timer _layoutTimer;
        private SideNavButton? _dragButton;
        private string? _dragId;
        private Point _dragStart;
        private Point _dragOffset;
        private bool _dragging;
        private NavPanelPosition _position;
        private NavIconSize _iconSize;
        private int _itemSize = 56;

        public IReadOnlyList<string> Order => _order;

        public NavPanelPosition Position
        {
            get => _position;
            set
            {
                if (_position == value) return;
                _position = value;
                foreach (var button in _buttons.Values)
                {
                    button.PanelPosition = _position;
                }
                RebuildButtons();
            }
        }

        public NavIconSize IconSize
        {
            get => _iconSize;
            set
            {
                if (_iconSize == value) return;
                _iconSize = value;
                _itemSize = value switch
                {
                    NavIconSize.Compact => 46,
                    NavIconSize.Large => 66,
                    _ => 56
                };
                RebuildButtons();
            }
        }

        public NavCustomizationPreview(IEnumerable<string> order, NavPanelPosition position, NavIconSize iconSize)
        {
            _order = NormalizeNavItemOrder(order);
            _position = position;
            _iconSize = iconSize;
            _itemSize = iconSize switch
            {
                NavIconSize.Compact => 46,
                NavIconSize.Large => 66,
                _ => 56
            };
            BackColor = NavBg;
            Padding = new Padding(12);
            MinimumSize = new Size(320, 260);
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint, true);

            _layoutTimer = new System.Windows.Forms.Timer { Interval = 16 };
            _layoutTimer.Tick += (_, _) => AnimateLayout();
            RebuildButtons();
        }

        public void Reset(IEnumerable<string> order, NavPanelPosition position)
        {
            _order.Clear();
            _order.AddRange(NormalizeNavItemOrder(order));
            _position = position;
            RebuildButtons();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _layoutTimer.Dispose();
            }

            base.Dispose(disposing);
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            LayoutButtons(immediate: true);
        }

        private void RebuildButtons()
        {
            Controls.Clear();
            _buttons.Clear();
            AddPreviewButton("back", "→", false);
            AddPreviewButton("import", "+", false);
            foreach (var id in _order)
            {
                var (icon, _, view) = NavItemInfo(id);
                var button = AddPreviewButton(id, icon, true);
                button.Active = view == AppView.Servers;
            }
            LayoutButtons(immediate: true);
        }

        private SideNavButton AddPreviewButton(string id, string icon, bool draggable)
        {
            var button = new SideNavButton
            {
                IconText = icon,
                PanelPosition = _position,
                IconScale = _iconSize switch
                {
                    NavIconSize.Compact => 0.9f,
                    NavIconSize.Large => 1.15f,
                    _ => 1f
                },
                Width = _itemSize,
                Height = _itemSize,
                Cursor = draggable ? Cursors.SizeAll : Cursors.Default,
                Tag = id
            };
            if (draggable)
            {
                button.MouseDown += DragButtonMouseDown;
                button.MouseMove += DragButtonMouseMove;
                button.MouseUp += DragButtonMouseUp;
            }

            _buttons[id] = button;
            Controls.Add(button);
            return button;
        }

        private void DragButtonMouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || sender is not SideNavButton button || button.Tag is not string id) return;
            _dragButton = button;
            _dragId = id;
            _dragStart = PointToClient(button.PointToScreen(e.Location));
            _dragOffset = e.Location;
            _dragging = false;
            button.Capture = true;
        }

        private void DragButtonMouseMove(object? sender, MouseEventArgs e)
        {
            if (_dragButton is null || _dragId is null) return;
            var cursor = PointToClient(_dragButton.PointToScreen(e.Location));
            if (!_dragging && Math.Abs(cursor.X - _dragStart.X) + Math.Abs(cursor.Y - _dragStart.Y) < 6) return;

            _dragging = true;
            _dragButton.BringToFront();
            _dragButton.Location = new Point(cursor.X - _dragOffset.X, cursor.Y - _dragOffset.Y);
            ReorderForPoint(cursor);
            LayoutButtons(immediate: false);
        }

        private void DragButtonMouseUp(object? sender, MouseEventArgs e)
        {
            if (_dragButton is not null)
            {
                _dragButton.Capture = false;
            }

            _dragButton = null;
            _dragId = null;
            _dragging = false;
            LayoutButtons(immediate: false);
        }

        private void ReorderForPoint(Point point)
        {
            if (_dragId is null) return;
            var current = _order.IndexOf(_dragId);
            if (current < 0) return;

            var target = 0;
            for (var i = 0; i < _order.Count; i++)
            {
                var slot = SlotBounds(i + 2);
                var midpoint = _position == NavPanelPosition.Top ? slot.Left + slot.Width / 2 : slot.Top + slot.Height / 2;
                var cursorAxis = _position == NavPanelPosition.Top ? point.X : point.Y;
                if (cursorAxis > midpoint) target = i + 1;
            }

            target = Math.Clamp(target, 0, _order.Count - 1);
            if (target == current) return;

            _order.RemoveAt(current);
            _order.Insert(target, _dragId);
        }

        private void LayoutButtons(bool immediate)
        {
            if (_buttons.TryGetValue("back", out var back)) MoveButton(back, SlotBounds(0), immediate);
            if (_buttons.TryGetValue("import", out var import)) MoveButton(import, SlotBounds(1), immediate);
            for (var i = 0; i < _order.Count; i++)
            {
                if (_buttons.TryGetValue(_order[i], out var button) && button != _dragButton)
                {
                    MoveButton(button, SlotBounds(i + 2), immediate);
                }
            }

            if (!immediate) _layoutTimer.Start();
        }

        private Rectangle SlotBounds(int index)
        {
            var x = Padding.Left;
            var y = Padding.Top;
            if (_position == NavPanelPosition.Top)
            {
                var count = _order.Count + 2;
                var available = Math.Max(1, Width - Padding.Left - Padding.Right);
                var size = Math.Clamp((available - Math.Max(0, count - 1) * ItemGap) / Math.Max(1, count), 34, _itemSize);
                var totalWidth = count * size + Math.Max(0, count - 1) * ItemGap;
                x += Math.Max(0, (available - totalWidth) / 2) + index * (size + ItemGap);
                y = Math.Max(Padding.Top, Height / 2 - size / 2);
                return new Rectangle(x, y, size, size);
            }
            else
            {
                x = Math.Max(Padding.Left, Width / 2 - _itemSize / 2);
                y += index * (_itemSize + ItemGap);
            }

            return new Rectangle(x, y, _itemSize, _itemSize);
        }

        private static void MoveButton(Control button, Rectangle target, bool immediate)
        {
            button.Size = target.Size;
            if (immediate)
            {
                button.Location = target.Location;
                return;
            }

            var next = new Point(
                button.Left + (int)((target.Left - button.Left) * 0.28f),
                button.Top + (int)((target.Top - button.Top) * 0.28f));
            if (Math.Abs(target.Left - next.X) < 2 && Math.Abs(target.Top - next.Y) < 2)
            {
                next = target.Location;
            }
            button.Location = next;
        }

        private void AnimateLayout()
        {
            var moving = false;
            foreach (Control button in Controls)
            {
                if (button == _dragButton || button.Tag is not string id) continue;
                var index = id switch
                {
                    "back" => 0,
                    "import" => 1,
                    _ => _order.IndexOf(id) + 2
                };
                if (index < 2 && id is not ("back" or "import")) continue;
                var target = SlotBounds(index);
                MoveButton(button, target, immediate: false);
                moving |= button.Location != target.Location;
            }

            if (!moving) _layoutTimer.Stop();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var border = new Pen(Color.FromArgb(58, 60, 68), 1f);
            using var path = RoundedRectPath(Rectangle.Inflate(ClientRectangle, -1, -1), 8);
            e.Graphics.DrawPath(border, path);
        }
    }

    private sealed class ChevronIcon : Control
    {
        private bool _expanded;
        private bool _hover;

        public bool Expanded
        {
            get => _expanded;
            set { _expanded = value; Invalidate(); }
        }

        public ChevronIcon()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = new Size(34, 34);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var pen = new Pen(_hover ? Color.White : Muted, 2f);
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;

            var center = new Point(Width / 2, Height / 2);
            var size = 5;

            if (Expanded)
            {
                // Down arrow
                e.Graphics.DrawLine(pen, center.X - size, center.Y - size / 2, center.X, center.Y + size / 2);
                e.Graphics.DrawLine(pen, center.X, center.Y + size / 2, center.X + size, center.Y - size / 2);
            }
            else
            {
                // Right arrow
                e.Graphics.DrawLine(pen, center.X - size / 2, center.Y - size, center.X + size / 2, center.Y);
                e.Graphics.DrawLine(pen, center.X + size / 2, center.Y, center.X - size / 2, center.Y + size);
            }
        }
    }

    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var rect = new Rectangle(Point.Empty, e.Item.Size);
            using var brush = new SolidBrush(e.Item.Selected ? Color.FromArgb(72, 74, 84) : Color.FromArgb(42, 44, 50));
            e.Graphics.FillRectangle(brush, rect);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Color.FromArgb(74, 76, 86));
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }
    }

    private sealed class RoundedPanel : Panel
    {
        public int Radius { get; set; } = 10;
        public Color BorderColor { get; set; } = Line;
        public int BorderWidth { get; set; } = 1;

        public RoundedPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.Clear(Parent?.BackColor ?? Bg);

            var inset = Math.Max(2, BorderWidth);
            var rect = new Rectangle(inset, inset, Math.Max(1, Width - inset * 2 - 1), Math.Max(1, Height - inset * 2 - 1));
            using var path = RoundedRect(rect, Radius);
            using var fill = new SolidBrush(BackColor);
            using var border = new Pen(BorderColor, BorderWidth)
            {
                Alignment = PenAlignment.Inset
            };
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(border, path);
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class RoundedButton : Button
    {
        public int Radius { get; set; } = 8;
        public Color BorderColor { get; set; } = Color.FromArgb(70, Color.White);
        public float BorderWidth { get; set; } = 1f;

        public RoundedButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.Clear(Parent?.BackColor ?? Bg);

            var pressed = MouseButtons == MouseButtons.Left && ClientRectangle.Contains(PointToClient(Cursor.Position));
            var rect = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
            if (pressed) rect.Offset(0, 1);

            using var path = RoundedRect(rect, Radius);
            using var fill = new SolidBrush(BackColor);
            using var border = new Pen(BorderColor, BorderWidth) { Alignment = PenAlignment.Inset };
            e.Graphics.FillPath(fill, path);
            if (BorderWidth > 0f)
            {
                e.Graphics.DrawPath(border, path);
            }

            var textRect = rect;
            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                textRect,
                ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class SettingsSelect : Control
    {
        private readonly string[] _values;
        private bool _hover;
        private bool _pressed;

        public string SelectedValue
        {
            get => Text;
            private set
            {
                Text = value;
                Invalidate();
            }
        }

        public SettingsSelect(string[] values, string selected)
        {
            _values = values;
            Text = selected;
            Height = 34;
            BackColor = CardBg2;
            ForeColor = TextColor;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint, true);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            _pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _pressed = true;
            Invalidate();
            ShowMenu();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _pressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.Clear(Parent?.BackColor ?? Bg);

            var rect = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
            using var path = RoundedRect(rect, 6);
            using var fill = new SolidBrush(_pressed ? Color.FromArgb(34, 36, 42) : BackColor);
            using var border = new Pen(_hover ? Accent : Color.FromArgb(82, 84, 92), 1.2f) { Alignment = PenAlignment.Inset };
            e.Graphics.FillPath(fill, path);
            e.Graphics.DrawPath(border, path);

            var textRect = new Rectangle(rect.Left + 10, rect.Top, rect.Width - 38, rect.Height);
            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                textRect,
                ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

            var cx = rect.Right - 17;
            var cy = rect.Top + rect.Height / 2 + 1;
            using var pen = new Pen(_hover ? Color.White : Color.FromArgb(196, 200, 212), 1.5f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            e.Graphics.DrawLine(pen, cx - 4, cy - 2, cx, cy + 2);
            e.Graphics.DrawLine(pen, cx, cy + 2, cx + 4, cy - 2);
        }

        private void ShowMenu()
        {
            var menu = new ContextMenuStrip
            {
                BackColor = Color.FromArgb(42, 44, 50),
                ForeColor = TextColor,
                Renderer = new DarkMenuRenderer(),
                ShowImageMargin = false
            };

            foreach (var value in _values)
            {
                var item = new ToolStripMenuItem(value)
                {
                    BackColor = Color.FromArgb(42, 44, 50),
                    ForeColor = TextColor,
                    Checked = value == SelectedValue
                };
                item.Click += (_, _) => SelectedValue = value;
                menu.Items.Add(item);
            }

            menu.Closed += (_, _) =>
            {
                _pressed = false;
                Invalidate();
            };
            menu.Show(this, new Point(0, Height + 2));
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class SettingsScrollPanel : Panel
    {
        private Control? _content;
        private bool _dragging;
        private int _dragOffset;
        private int _scrollY;

        public Control? Content
        {
            get => _content;
            set
            {
                if (_content is not null)
                {
                    Controls.Remove(_content);
                }

                _content = value;
                _scrollY = 0;
                if (_content is not null)
                {
                    _content.Dock = DockStyle.None;
                    _content.Location = Point.Empty;
                    Controls.Add(_content);
                    HookMouseWheel(_content);
                }
                UpdateContentLayout();
            }
        }

        public SettingsScrollPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint, true);
            TabStop = true;
            BackColor = Bg;
        }

        protected override void OnResize(EventArgs e)
        {
            UpdateContentLayout();
            base.OnResize(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ScrollBy(-Math.Sign(e.Delta) * 48);
            base.OnMouseWheel(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            var thumb = ThumbBounds();
            if (thumb.Contains(e.Location))
            {
                _dragging = true;
                _dragOffset = e.Y - thumb.Top;
                Capture = true;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_dragging || _content is null) return;
            var max = MaxScroll();
            if (max <= 0) return;

            var track = TrackBounds();
            var thumb = ThumbBounds();
            var usable = Math.Max(1, track.Height - thumb.Height);
            var y = Math.Clamp(e.Y - _dragOffset - track.Top, 0, usable);
            SetScroll((int)Math.Round(y / (double)usable * max));
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _dragging = false;
            Capture = false;
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.Clear(BackColor);
            if (MaxScroll() <= 0) return;

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            var track = TrackBounds();
            using var trackPath = RoundedRect(track, 3);
            using var trackBrush = new SolidBrush(Color.FromArgb(31, 33, 38));
            e.Graphics.FillPath(trackBrush, trackPath);

            var thumb = ThumbBounds();
            using var thumbPath = RoundedRect(thumb, 3);
            using var thumbBrush = new SolidBrush(_dragging ? Color.FromArgb(150, 154, 170) : Color.FromArgb(104, 108, 124));
            e.Graphics.FillPath(thumbBrush, thumbPath);
        }

        private void HookMouseWheel(Control control)
        {
            control.MouseWheel += (_, e) => ScrollBy(-Math.Sign(e.Delta) * 48);
            foreach (Control child in control.Controls)
            {
                HookMouseWheel(child);
            }
            control.ControlAdded += (_, e) =>
            {
                if (e.Control is not null) HookMouseWheel(e.Control);
            };
        }

        private void UpdateContentLayout()
        {
            if (_content is null) return;

            var scrollbarWidth = MaxScroll() > 0 ? 14 : 0;
            var contentWidth = Math.Max(1, ClientSize.Width - scrollbarWidth);
            _content.Width = contentWidth;
            var preferred = _content.GetPreferredSize(new Size(contentWidth, 0));
            _content.Height = Math.Max(ClientSize.Height, preferred.Height);
            SetScroll(_scrollY);
        }

        private void ScrollBy(int delta)
        {
            SetScroll(_scrollY + delta);
        }

        private void SetScroll(int value)
        {
            if (_content is null) return;
            _scrollY = Math.Clamp(value, 0, MaxScroll());
            _content.Location = new Point(0, -_scrollY);
            Invalidate();
        }

        private int MaxScroll()
        {
            return _content is null ? 0 : Math.Max(0, _content.Height - ClientSize.Height);
        }

        private Rectangle TrackBounds()
        {
            return new Rectangle(Width - 8, 3, 4, Math.Max(1, Height - 6));
        }

        private Rectangle ThumbBounds()
        {
            var track = TrackBounds();
            var max = MaxScroll();
            if (_content is null || max <= 0) return track;

            var thumbHeight = Math.Clamp((int)Math.Round(track.Height * (ClientSize.Height / (double)_content.Height)), 28, track.Height);
            var usable = Math.Max(1, track.Height - thumbHeight);
            var y = track.Top + (int)Math.Round(_scrollY / (double)max * usable);
            return new Rectangle(track.Left, y, track.Width, thumbHeight);
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class ImportScrollPanel : Panel
    {
        private Control? _content;
        private bool _dragging;
        private int _dragOffset;
        private int _scrollY;

        public Control? Content
        {
            get => _content;
            set
            {
                if (_content is not null) Controls.Remove(_content);
                _content = value;
                if (_content is not null)
                {
                    _content.Location = Point.Empty;
                    Controls.Add(_content);
                    _content.MouseWheel += (_, e) => ScrollBy(-Math.Sign(e.Delta) * 36);
                }
                UpdateContentLayout();
            }
        }

        public ImportScrollPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            TabStop = true;
        }

        protected override void OnResize(EventArgs e)
        {
            UpdateContentLayout();
            base.OnResize(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ScrollBy(-Math.Sign(e.Delta) * 36);
            base.OnMouseWheel(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            var thumb = ThumbBounds();
            if (thumb.Contains(e.Location))
            {
                _dragging = true;
                _dragOffset = e.Y - thumb.Top;
                Capture = true;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_dragging || _content is null) return;
            var max = MaxScroll();
            if (max <= 0) return;

            var track = TrackBounds();
            var thumb = ThumbBounds();
            var usable = Math.Max(1, track.Height - thumb.Height);
            var y = Math.Clamp(e.Y - _dragOffset - track.Top, 0, usable);
            SetScroll((int)Math.Round(y / (double)usable * max));
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _dragging = false;
            Capture = false;
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (MaxScroll() <= 0) return;

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            var track = TrackBounds();
            using var trackPath = RoundedRect(track, 3);
            using var trackBrush = new SolidBrush(Color.FromArgb(31, 33, 38));
            e.Graphics.FillPath(trackBrush, trackPath);

            var thumb = ThumbBounds();
            using var thumbPath = RoundedRect(thumb, 3);
            using var thumbBrush = new SolidBrush(_dragging ? Color.FromArgb(150, 154, 170) : Color.FromArgb(104, 108, 124));
            e.Graphics.FillPath(thumbBrush, thumbPath);
        }

        private void UpdateContentLayout()
        {
            if (_content is null) return;
            _content.Width = Math.Max(1, ClientSize.Width - (MaxScroll() > 0 ? 14 : 0));
            SetScroll(_scrollY);
        }

        private void ScrollBy(int delta)
        {
            SetScroll(_scrollY + delta);
        }

        private void SetScroll(int value)
        {
            if (_content is null) return;
            _scrollY = Math.Clamp(value, 0, MaxScroll());
            _content.Location = new Point(0, -_scrollY);
            Invalidate();
        }

        private int MaxScroll()
        {
            return _content is null ? 0 : Math.Max(0, _content.Height - ClientSize.Height);
        }

        private Rectangle TrackBounds()
        {
            return new Rectangle(Width - 8, 3, 4, Math.Max(1, Height - 6));
        }

        private Rectangle ThumbBounds()
        {
            var track = TrackBounds();
            var max = MaxScroll();
            if (_content is null || max <= 0) return track;

            var thumbHeight = Math.Clamp((int)Math.Round(track.Height * (ClientSize.Height / (double)_content.Height)), 28, track.Height);
            var usable = Math.Max(1, track.Height - thumbHeight);
            var y = track.Top + (int)Math.Round(_scrollY / (double)max * usable);
            return new Rectangle(track.Left, y, track.Width, thumbHeight);
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class DarkHorizontalScrollBar : Control
    {
        private bool _dragging;
        private int _dragOffset;
        private int _value;
        private int _maximum;

        public event EventHandler<int>? ScrollRequested;

        public int Maximum
        {
            get => _maximum;
            set
            {
                _maximum = Math.Max(0, value);
                Value = Math.Min(Value, _maximum);
                Invalidate();
            }
        }

        public int Value
        {
            get => _value;
            set
            {
                var next = Math.Clamp(value, 0, Maximum);
                if (_value == next) return;
                _value = next;
                Invalidate();
            }
        }

        public DarkHorizontalScrollBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint, true);
            Cursor = Cursors.Hand;
            Height = 7;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || Maximum <= 0) return;
            var thumb = ThumbBounds();
            if (thumb.Contains(e.Location))
            {
                _dragging = true;
                _dragOffset = e.X - thumb.Left;
                Capture = true;
            }
            else
            {
                Request(Value + (e.X < thumb.Left ? -8 : 8));
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_dragging || Maximum <= 0) return;
            var track = TrackBounds();
            var thumbWidth = ThumbBounds().Width;
            var usable = Math.Max(1, track.Width - thumbWidth);
            var x = Math.Clamp(e.X - _dragOffset - track.Left, 0, usable);
            Request((int)Math.Round(x / (double)usable * Maximum));
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _dragging = false;
            Capture = false;
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.Clear(CardBg2);

            var track = TrackBounds();
            using var trackPath = RoundedRect(track, 3);
            using var trackBrush = new SolidBrush(Color.FromArgb(31, 33, 38));
            e.Graphics.FillPath(trackBrush, trackPath);

            var thumb = ThumbBounds();
            using var thumbPath = RoundedRect(thumb, 3);
            using var thumbBrush = new SolidBrush(Maximum > 0
                ? (_dragging ? Color.FromArgb(150, 154, 170) : Color.FromArgb(104, 108, 124))
                : Color.FromArgb(58, 60, 68));
            e.Graphics.FillPath(thumbBrush, thumbPath);
        }

        private void Request(int value)
        {
            ScrollRequested?.Invoke(this, Math.Clamp(value, 0, Maximum));
        }

        private Rectangle TrackBounds()
        {
            return new Rectangle(0, 2, Math.Max(1, Width), 4);
        }

        private Rectangle ThumbBounds()
        {
            var track = TrackBounds();
            if (Maximum <= 0) return new Rectangle(track.Left, track.Top, track.Width, track.Height);
            var thumbWidth = Math.Clamp(track.Width / 3, 34, track.Width);
            var usable = Math.Max(1, track.Width - thumbWidth);
            var x = track.Left + (int)Math.Round(Value / (double)Maximum * usable);
            return new Rectangle(x, track.Top, thumbWidth, track.Height);
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class DarkScrollBar : Control
    {
        private bool _dragging;
        private int _dragOffset;
        private int _value;
        private int _maximum;

        public event EventHandler<int>? ScrollRequested;

        public int LargeChange { get; set; } = 1;

        public int Maximum
        {
            get => _maximum;
            set
            {
                _maximum = Math.Max(0, value);
                Value = Math.Min(Value, _maximum);
                Invalidate();
            }
        }

        public int Value
        {
            get => _value;
            set
            {
                var next = Math.Clamp(value, 0, Maximum);
                if (_value == next) return;
                _value = next;
                Invalidate();
            }
        }

        public DarkScrollBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint, true);
            Cursor = Cursors.Hand;
            Width = 10;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (Maximum <= 0) return;
            Request(Value - Math.Sign(e.Delta) * 3);
            base.OnMouseWheel(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Focus();
            var thumb = ThumbBounds();
            if (thumb.Contains(e.Location))
            {
                _dragging = true;
                _dragOffset = e.Y - thumb.Top;
            }
            else
            {
                if (Maximum <= 0) return;
                Request(Value + (e.Y < thumb.Top ? -LargeChange : LargeChange));
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_dragging || Maximum <= 0) return;
            var track = TrackBounds();
            var thumbHeight = ThumbBounds().Height;
            var usable = Math.Max(1, track.Height - thumbHeight);
            var y = Math.Clamp(e.Y - _dragOffset - track.Top, 0, usable);
            Request((int)Math.Round(y / (double)usable * Maximum));
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _dragging = false;
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.Clear(CardBg2);

            var track = TrackBounds();
            using var trackPath = RoundedRect(track, 3);
            using var trackBrush = new SolidBrush(Color.FromArgb(31, 33, 38));
            e.Graphics.FillPath(trackBrush, trackPath);

            var thumb = ThumbBounds();
            if (Maximum > 0)
            {
                using var thumbPath = RoundedRect(thumb, 3);
                using var thumbBrush = new SolidBrush(_dragging ? Color.FromArgb(150, 154, 170) : Color.FromArgb(104, 108, 124));
                e.Graphics.FillPath(thumbBrush, thumbPath);
            }

            DrawArrow(e.Graphics, true);
            DrawArrow(e.Graphics, false);
        }

        private void Request(int value)
        {
            ScrollRequested?.Invoke(this, Math.Clamp(value, 0, Maximum));
        }

        private Rectangle TrackBounds()
        {
            return new Rectangle(4, 17, Math.Max(3, Width - 8), Math.Max(1, Height - 34));
        }

        private Rectangle ThumbBounds()
        {
            var track = TrackBounds();
            if (Maximum <= 0) return new Rectangle(track.Left, track.Top, track.Width, track.Height);
            var visibleRatio = LargeChange / (double)(Maximum + LargeChange);
            var thumbHeight = Math.Clamp((int)Math.Round(track.Height * visibleRatio), 20, track.Height);
            var usable = Math.Max(1, track.Height - thumbHeight);
            var y = track.Top + (int)Math.Round(Value / (double)Maximum * usable);
            return new Rectangle(track.Left, y, track.Width, thumbHeight);
        }

        private void DrawArrow(Graphics g, bool up)
        {
            var cx = Width / 2f;
            var cy = up ? 8f : Height - 8f;
            using var pen = new Pen(Color.White, 1.6f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };

            if (up)
            {
                g.DrawLine(pen, cx - 3.5f, cy + 2f, cx, cy - 2f);
                g.DrawLine(pen, cx, cy - 2f, cx + 3.5f, cy + 2f);
            }
            else
            {
                g.DrawLine(pen, cx - 3.5f, cy - 2f, cx, cy + 2f);
                g.DrawLine(pen, cx, cy + 2f, cx + 3.5f, cy - 2f);
            }
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class SettingsNumberInput : UserControl
    {
        private readonly TextBox _box = new();
        private readonly StepperButton _up = new(true);
        private readonly StepperButton _down = new(false);
        private int _value;

        public int Minimum { get; set; } = 0;
        public int Maximum { get; set; } = 100;

        public int Value
        {
            get => Coerce(ParseValue());
            set
            {
                _value = Coerce(value);
                _box.Text = _value.ToString();
            }
        }

        public SettingsNumberInput()
        {
            Height = 34;
            BackColor = Bg;
            Margin = new Padding(4, 8, 10, 8);

            var wrap = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                BackColor = CardBg2,
                BorderColor = Color.FromArgb(82, 84, 92),
                Radius = 6,
                Padding = new Padding(10, 7, 3, 4)
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = CardBg2,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18));

            _box.BorderStyle = BorderStyle.None;
            _box.BackColor = CardBg2;
            _box.ForeColor = TextColor;
            _box.Dock = DockStyle.Fill;
            _box.Margin = new Padding(0, 1, 4, 0);
            _box.TextAlign = HorizontalAlignment.Left;
            _box.KeyPress += (_, e) =>
            {
                if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true;
            };
            _box.Leave += (_, _) => Value = ParseValue();

            var stepper = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = CardBg2,
                RowCount = 2,
                ColumnCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            stepper.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            stepper.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

            _up.Click += (_, _) => Value++;
            _down.Click += (_, _) => Value--;
            stepper.Controls.Add(_up, 0, 0);
            stepper.Controls.Add(_down, 0, 1);

            layout.Controls.Add(_box, 0, 0);
            layout.Controls.Add(stepper, 1, 0);
            wrap.Controls.Add(layout);
            Controls.Add(wrap);
            Value = 0;
        }

        private int ParseValue()
        {
            return int.TryParse(_box.Text, out var parsed) ? parsed : _value;
        }

        private int Coerce(int value)
        {
            return Math.Clamp(value, Minimum, Maximum);
        }
    }

    private sealed class StepperButton : Control
    {
        private readonly bool _up;
        private bool _hover;
        private bool _pressed;

        public StepperButton(bool up)
        {
            _up = up;
            Dock = DockStyle.Fill;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint, true);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            _pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            _pressed = true;
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _pressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.Clear(CardBg2);

            if (_hover)
            {
                using var hover = new SolidBrush(_pressed ? Color.FromArgb(62, 64, 72) : Color.FromArgb(48, 50, 58));
                e.Graphics.FillRectangle(hover, ClientRectangle);
            }

            var cx = Width / 2f;
            var cy = Height / 2f + (_up ? 1f : -1f);
            var color = _hover ? Color.White : Color.FromArgb(182, 186, 198);
            using var pen = new Pen(color, 1.6f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };

            if (_up)
            {
                e.Graphics.DrawLine(pen, cx - 4, cy + 2, cx, cy - 2);
                e.Graphics.DrawLine(pen, cx, cy - 2, cx + 4, cy + 2);
            }
            else
            {
                e.Graphics.DrawLine(pen, cx - 4, cy - 2, cx, cy + 2);
                e.Graphics.DrawLine(pen, cx, cy + 2, cx + 4, cy - 2);
            }
        }
    }

    private sealed class ModeSwitchControl : Control
    {
        private readonly System.Windows.Forms.Timer _animationTimer = new() { Interval = 15 };
        private float _thumbPosition;
        private float _targetPosition;
        private bool _hoverProxy;
        private bool _hoverTun;
        private bool _isTun;

        public event EventHandler<bool>? ModeChanged;
        public string ProxyText { get; set; } = "Proxy";
        public string TunText { get; set; } = "TUN";

        public bool IsTun
        {
            get => _isTun;
            set
            {
                _isTun = value;
                _targetPosition = value ? 1f : 0f;
                if (Math.Abs(_thumbPosition - _targetPosition) > 0.001f)
                {
                    _animationTimer.Start();
                }
                Invalidate();
            }
        }

        public ModeSwitchControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            _animationTimer.Tick += (_, _) => Animate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var left = e.X < Width / 2;
            var changed = _hoverProxy != left || _hoverTun == left;
            _hoverProxy = left;
            _hoverTun = !left;
            if (changed) Invalidate();
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hoverProxy = false;
            _hoverTun = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                var nextTun = e.X >= Width / 2;
                if (nextTun != _isTun)
                {
                    IsTun = nextTun;
                    ModeChanged?.Invoke(this, nextTun);
                }
            }
            base.OnMouseDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Parent?.BackColor ?? Bg);

            var outer = new Rectangle(0, 0, Width - 1, Height - 1);
            using var outerPath = RoundedRect(outer, 8);
            using var outerFill = new SolidBrush(Color.FromArgb(18, 18, 18));
            using var outerBorder = new Pen(Color.FromArgb(38, 40, 46), 1f);
            g.FillPath(outerFill, outerPath);
            g.DrawPath(outerBorder, outerPath);

            var padding = 4;
            var segmentWidth = (Width - padding * 2) / 2;
            var thumbX = padding + (Width - padding * 2 - segmentWidth) * _thumbPosition;
            var thumb = new RectangleF(thumbX, padding, segmentWidth, Height - padding * 2);
            using var thumbPath = RoundedRect(Rectangle.Round(thumb), 7);
            using var thumbBrush = new LinearGradientBrush(
                thumb,
                Color.FromArgb(126, 106, 255),
                Color.FromArgb(75, 165, 255),
                LinearGradientMode.Horizontal);
            using var glow = new SolidBrush(Color.FromArgb(38, Accent));
            g.FillPath(glow, RoundedRect(new Rectangle((int)thumb.X, (int)thumb.Y + 2, (int)thumb.Width, (int)thumb.Height), 7));
            g.FillPath(thumbBrush, thumbPath);

            var proxyRect = new Rectangle(0, 0, Width / 2, Height);
            var tunRect = new Rectangle(Width / 2, 0, Width - Width / 2, Height);
            DrawLabel(g, ProxyText, proxyRect, !_isTun, _hoverProxy);
            DrawLabel(g, TunText, tunRect, _isTun, _hoverTun);
        }

        private void Animate()
        {
            _thumbPosition += (_targetPosition - _thumbPosition) * 0.24f;
            if (Math.Abs(_targetPosition - _thumbPosition) < 0.01f)
            {
                _thumbPosition = _targetPosition;
                _animationTimer.Stop();
            }
            Invalidate();
        }

        private static void DrawLabel(Graphics g, string text, Rectangle bounds, bool active, bool hover)
        {
            var color = active ? Color.White : hover ? Color.FromArgb(220, 224, 235) : Color.FromArgb(178, 182, 194);
            using var font = new Font("Segoe UI", 10f, FontStyle.Bold);
            TextRenderer.DrawText(
                g,
                text,
                font,
                bounds,
                color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class ToggleButton : Control
    {
        private readonly System.Windows.Forms.Timer _animationTimer = new() { Interval = 15 };
        private bool _hover;
        private bool _pressed;
        private bool _isOn;
        private float _thumbPosition;
        private float _targetPosition;

        public bool IsOn
        {
            get => _isOn;
            set
            {
                if (_isOn == value && Math.Abs(_targetPosition - (value ? 1f : 0f)) < 0.001f) return;
                _isOn = value;
                _targetPosition = value ? 1f : 0f;
                if (Math.Abs(_thumbPosition - _targetPosition) > 0.001f)
                {
                    _animationTimer.Start();
                }
                Invalidate();
            }
        }

        public ToggleButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            _animationTimer.Tick += (_, _) => Animate();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            _pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            _pressed = true;
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _pressed = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var rect = new Rectangle(3, 5, Math.Max(1, Width - 6), Math.Max(1, Height - 10));
            var active = _isOn;
            using var path = RoundedRect(rect, rect.Height / 2);
            using var shadow = new SolidBrush(Color.FromArgb(55, Color.Black));
            e.Graphics.FillPath(shadow, RoundedRect(new Rectangle(rect.X, rect.Y + 3, rect.Width, rect.Height), rect.Height / 2));

            using var bg = new LinearGradientBrush(
                rect,
                active ? Color.FromArgb(126, 106, 255) : Color.FromArgb(43, 45, 55),
                active ? Color.FromArgb(96, 125, 255) : Color.FromArgb(28, 30, 38),
                LinearGradientMode.Horizontal);
            e.Graphics.FillPath(bg, path);

            using var border = new Pen(active ? Color.FromArgb(172, 160, 255) : Color.FromArgb(82, 84, 98), _hover ? 1.6f : 1.2f)
            {
                Alignment = PenAlignment.Inset
            };
            e.Graphics.DrawPath(border, path);

            var knobSize = rect.Height - 10;
            var leftX = rect.Left + 6;
            var rightX = rect.Right - knobSize - 6;
            var knobX = (int)Math.Round(leftX + (rightX - leftX) * _thumbPosition);
            if (_pressed) knobX += active ? -2 : 2;
            var knob = new Rectangle(knobX, rect.Top + 5, knobSize, knobSize);
            using var knobBrush = new SolidBrush(Color.White);
            using var knobShadow = new SolidBrush(Color.FromArgb(70, Color.Black));
            e.Graphics.FillEllipse(knobShadow, knob.X, knob.Y + 2, knob.Width, knob.Height);
            e.Graphics.FillEllipse(knobBrush, knob);

            var text = active ? "ON" : "OFF";
            using var font = new Font("Segoe UI", 12f, FontStyle.Bold);
            var textRect = active
                ? new Rectangle(rect.Left + 18, rect.Top, rect.Width - knobSize - 26, rect.Height)
                : new Rectangle(rect.Left + knobSize + 16, rect.Top, rect.Width - knobSize - 24, rect.Height);
            TextRenderer.DrawText(e.Graphics, text, font, textRect, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        private void Animate()
        {
            _thumbPosition += (_targetPosition - _thumbPosition) * 0.26f;
            if (Math.Abs(_targetPosition - _thumbPosition) < 0.01f)
            {
                _thumbPosition = _targetPosition;
                _animationTimer.Stop();
            }
            Invalidate();
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class ConnectionPanel : Panel
    {
        public ConnectionPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint, true);
            BackColor = Color.Black;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.Clear(Color.FromArgb(6, 7, 10));
        }
    }
}
