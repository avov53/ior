using System.Drawing.Drawing2D;

namespace HappBox;

internal sealed record TrayServerOption(string Id, string Name, string Protocol, bool Selected);

internal sealed class TrayPopupForm : Form
{
    internal Point AnchorPoint => _anchor;
    private readonly TrayPalette _palette;
    private readonly Point _anchor;
    private readonly bool _connected;
    private readonly List<TrayActionButton> _serverButtons = [];
    private readonly System.Windows.Forms.Timer _entranceTimer = new() { Interval = 15 };
    private readonly System.Windows.Forms.Timer _expandTimer = new() { Interval = 15 };
    private readonly Panel _serverCard = new();
    private readonly Panel _serverList = new();
    private readonly Label _brand = new();
    private readonly Label _status = new();
    private readonly Label _serverCaption = new();
    private readonly Label _serverName = new();
    private readonly Label _serverProtocol = new();
    private readonly TrayActionButton _switchButton;
    private readonly TrayActionButton _connectionButton;
    private readonly TrayActionButton _openButton;
    private readonly TrayActionButton _exitButton;
    private readonly int _baseHeight = 250;
    private int _serverListHeight;
    private int _targetServerListHeight;
    private int _serverScrollOffset;
    private DateTime _entranceStarted;

    public TrayPopupForm(
        AppTheme theme, Point anchor, bool russian, string? serverName, string? protocol,
        bool connected, bool transitioning, IReadOnlyList<TrayServerOption> servers,
        Action open, Action toggleConnection, Action exit, Action<string> selectServer)
    {
        _palette = TrayPalette.For(theme);
        _anchor = anchor;
        _connected = connected;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        BackColor = _palette.Panel;
        ForeColor = _palette.Text;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(P(284), P(_baseHeight));
        Opacity = 0;

        _brand.Text = "ior";
        _brand.ForeColor = _palette.Text;
        _brand.Font = new Font("Segoe UI Semibold", 13f, FontStyle.Bold);
        _brand.AutoSize = false;
        Controls.Add(_brand);

        _status.Text = transitioning
            ? (russian ? "Переключение…" : "Switching…")
            : connected ? (russian ? "Подключено" : "Connected")
                        : (russian ? "Не подключено" : "Disconnected");
        _status.ForeColor = transitioning ? _palette.Muted : connected ? _palette.Accent : _palette.Muted;
        _status.TextAlign = ContentAlignment.MiddleRight;
        Controls.Add(_status);

        _serverCard.BackColor = _palette.Panel;
        Controls.Add(_serverCard);
        _serverCaption.Text = (russian ? "ВЫБРАННЫЙ СЕРВЕР" : "SELECTED SERVER") +
            (string.IsNullOrWhiteSpace(protocol) ? "" : $" · {protocol}");
        _serverCaption.Font = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold);
        _serverCaption.ForeColor = _palette.Muted;
        _serverCaption.BackColor = _palette.Panel;
        _serverCaption.AutoEllipsis = true;
        _serverCard.Controls.Add(_serverCaption);
        _serverName.Text = string.IsNullOrWhiteSpace(serverName)
            ? (russian ? "Сервер не выбран" : "No server selected") : serverName;
        _serverName.Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
        _serverName.ForeColor = _palette.Text;
        _serverName.BackColor = _palette.Panel;
        _serverName.AutoEllipsis = true;
        _serverCard.Controls.Add(_serverName);
        _serverProtocol.Text = protocol ?? "";
        _serverProtocol.ForeColor = _palette.Muted;
        _serverProtocol.BackColor = _palette.Panel;
        _serverProtocol.AutoEllipsis = true;
        _serverProtocol.Visible = false;
        _serverCard.Controls.Add(_serverProtocol);

        _switchButton = AddAction(russian ? "Сменить сервер  ›" : "Switch server  ›", false, () =>
        {
            if (servers.Count == 0) return;
            var availablePixels = Screen.FromPoint(_anchor).WorkingArea.Height - P(_baseHeight + 16);
            var availableLogical = (int)Math.Floor(Math.Max(0, availablePixels) * 96d / DeviceDpi);
            _targetServerListHeight = _targetServerListHeight == 0
                ? Math.Min(Math.Min(servers.Count, 5) * 40 + 8, availableLogical) : 0;
            _expandTimer.Start();
        });
        _switchButton.Enabled = !transitioning && servers.Count > 0;

        _serverList.BackColor = _palette.Panel;
        // Native AutoScroll paints temporary white scrollbars while the list animates.
        _serverList.AutoScroll = false;
        _serverList.MouseWheel += (_, e) => ScrollServers(e.Delta);
        _serverList.Visible = false;
        Controls.Add(_serverList);
        foreach (var server in servers)
        {
            var option = server;
            var button = new TrayActionButton(
                (server.Selected ? "●  " : "    ") + server.Name,
                _palette, false, () => { Close(); selectServer(option.Id); });
            button.AccessibleName = $"{server.Name}, {server.Protocol}";
            button.Height = P(36);
            button.Tag = server.Protocol;
            _serverList.Controls.Add(button);
            button.MouseWheel += (_, e) => ScrollServers(e.Delta);
            _serverButtons.Add(button);
        }

        _connectionButton = AddAction(
            transitioning ? (russian ? "Подождите…" : "Please wait…")
                          : connected ? (russian ? "Отключить" : "Disconnect")
                                      : (russian ? "Подключить" : "Connect"),
            true, () => { Close(); toggleConnection(); });
        _connectionButton.Enabled = !transitioning && (connected || !string.IsNullOrWhiteSpace(serverName));

        _openButton = AddAction(russian ? "Открыть ior" : "Open ior", false, () => { Close(); open(); });
        _exitButton = AddAction(russian ? "Выйти" : "Exit", false, () => { Close(); exit(); });

        _entranceTimer.Tick += (_, _) => AnimateEntrance();
        _expandTimer.Tick += (_, _) => AnimateServerList();
        Deactivate += (_, _) => Close();
        FormClosed += (_, _) => { _entranceTimer.Dispose(); _expandTimer.Dispose(); };
        LayoutElements();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return parameters;
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        if (keyData is Keys.Down or Keys.Up)
        {
            var focusable = new[] { _switchButton }
                .Concat(_serverList.Visible ? _serverButtons : [])
                .Concat(new[] { _connectionButton, _openButton, _exitButton })
                .Where(button => button.Visible && button.Enabled).ToArray();
            if (focusable.Length == 0) return true;
            var index = Array.FindIndex(focusable, button => button.Focused);
            var next = focusable[(index + (keyData == Keys.Down ? 1 : -1) + focusable.Length) % focusable.Length];
            if (_serverButtons.Contains(next)) EnsureServerVisible(_serverButtons.IndexOf(next));
            next.Focus();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), P(14));
        using var brush = new SolidBrush(_palette.Panel);
        using var border = new Pen(_palette.Border);
        e.Graphics.FillPath(brush, path);
        e.Graphics.DrawPath(border, path);
        using var accent = new SolidBrush(_connected ? _palette.Accent : _palette.Muted);
        e.Graphics.FillEllipse(accent, P(16), P(20), P(6), P(6));
        using var separator = new Pen(_palette.Border);
        foreach (var y in new[] { 123, 165, 207 })
            e.Graphics.DrawLine(separator, P(12), P(y + _serverListHeight), Width - P(12), P(y + _serverListHeight));
        base.OnPaint(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (Width < 20 || Height < 20) return;
        using var path = RoundedRect(new Rectangle(0, 0, Width, Height), P(14));
        Region?.Dispose();
        Region = new Region(path);
        LayoutElements();
    }

    public void ShowAtAnchor()
    {
        PositionNearAnchor();
        Show();
        Activate();
        _entranceStarted = DateTime.UtcNow;
        _entranceTimer.Start();
        _openButton.Focus();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ClientSize = new Size(P(284), P(_baseHeight + _serverListHeight));
        LayoutElements();
        PositionNearAnchor();
    }

    private TrayActionButton AddAction(string text, bool primary, Action click)
    {
        var button = new TrayActionButton(text, _palette, primary, click);
        Controls.Add(button);
        return button;
    }

    private void LayoutElements()
    {
        if (_brand is null || _serverCard is null || _switchButton is null || _connectionButton is null) return;
        var width = ClientSize.Width;
        _brand.SetBounds(P(28), P(8), P(84), P(30));
        _status.SetBounds(width - P(138), P(9), P(126), P(27));
        _serverCard.SetBounds(P(12), P(42), width - P(24), P(37));
        _serverCaption.SetBounds(P(5), P(1), _serverCard.Width - P(10), P(13));
        _serverName.SetBounds(P(5), P(15), _serverCard.Width - P(10), P(21));
        _switchButton.SetBounds(P(9), P(86), width - P(18), P(31));
        _serverList.SetBounds(P(12), P(121), width - P(24), P(_serverListHeight));
        for (var i = 0; i < _serverButtons.Count; i++)
            _serverButtons[i].SetBounds(0, P(i * 40) - _serverScrollOffset, _serverList.Width, P(36));
        _connectionButton.SetBounds(P(9), P(128 + _serverListHeight), width - P(18), P(31));
        if (_openButton is not null) _openButton.SetBounds(P(9), P(170 + _serverListHeight), width - P(18), P(31));
        if (_exitButton is not null) _exitButton.SetBounds(P(9), P(212 + _serverListHeight), width - P(18), P(31));
        Invalidate();
    }

    private void AnimateEntrance()
    {
        var progress = Math.Clamp((DateTime.UtcNow - _entranceStarted).TotalMilliseconds / 170, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        Opacity = eased;
        PositionNearAnchor(P((int)Math.Round((1 - eased) * 9)));
        if (progress >= 1) _entranceTimer.Stop();
    }

    private void ScrollServers(int delta)
    {
        var maxOffset = Math.Max(0, P(_serverButtons.Count * 40 + 8) - _serverList.Height);
        _serverScrollOffset = Math.Clamp(_serverScrollOffset - Math.Sign(delta) * P(80), 0, maxOffset);
        LayoutElements();
    }

    private void EnsureServerVisible(int index)
    {
        var top = P(index * 40);
        var bottom = top + P(36);
        if (top < _serverScrollOffset) _serverScrollOffset = top;
        else if (bottom > _serverScrollOffset + _serverList.Height)
            _serverScrollOffset = bottom - _serverList.Height;
        LayoutElements();
    }

    private void AnimateServerList()
    {
        var distance = _targetServerListHeight - _serverListHeight;
        if (Math.Abs(distance) <= 2)
        {
            _serverListHeight = _targetServerListHeight;
            _expandTimer.Stop();
        }
        else _serverListHeight += Math.Sign(distance) * Math.Max(2, (int)Math.Ceiling(Math.Abs(distance) * .28));
        _serverList.Visible = _serverListHeight > 0;
        ClientSize = new Size(ClientSize.Width, P(_baseHeight + _serverListHeight));
        LayoutElements();
        PositionNearAnchor();
    }

    private void PositionNearAnchor(int entranceOffset = 0)
    {
        var area = Screen.FromPoint(_anchor).WorkingArea;
        var above = _anchor.Y > area.Top + area.Height / 2;
        var x = Math.Clamp(_anchor.X - Width + P(24), area.Left + P(8), Math.Max(area.Left + P(8), area.Right - Width - P(8)));
        var preferredY = above ? _anchor.Y - Height - P(12) : _anchor.Y + P(12);
        var y = Math.Clamp(preferredY + (above ? entranceOffset : -entranceOffset),
            area.Top + P(8), Math.Max(area.Top + P(8), area.Bottom - Height - P(8)));
        Location = new Point(x, y);
    }

    private int P(int logicalPixels) => (int)Math.Round(logicalPixels * DeviceDpi / 96d);

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class TrayActionButton : Control
    {
        private readonly TrayPalette _palette;
        private readonly bool _primary;
        private readonly Action _click;
        private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 15 };
        private float _hover;
        private float _targetHover;

        public TrayActionButton(string text, TrayPalette palette, bool primary, Action click)
        {
            Text = text;
            _palette = palette;
            _primary = primary;
            _click = click;
            BackColor = palette.Panel;
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.PushButton;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
            _hoverTimer.Tick += (_, _) =>
            {
                _hover += (_targetHover - _hover) * .28f;
                if (Math.Abs(_targetHover - _hover) < .02f) { _hover = _targetHover; _hoverTimer.Stop(); }
                Invalidate();
            };
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _targetHover = 1; _hoverTimer.Start(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _targetHover = 0; _hoverTimer.Start(); }
        protected override void OnMouseDown(MouseEventArgs e) { Focus(); base.OnMouseDown(e); }
        protected override void OnClick(EventArgs e) { base.OnClick(e); if (Enabled) _click(); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Enter or Keys.Space) { e.Handled = true; PerformAction(); }
            base.OnKeyDown(e);
        }

        private void PerformAction() { if (Enabled) _click(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var baseColor = _palette.Panel;
            var hoverColor = Blend(_palette.Panel2, _palette.Accent, _primary ? .15f : .08f);
            using var path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 7);
            using var brush = new SolidBrush(Blend(baseColor, hoverColor, _hover));
            e.Graphics.FillPath(brush, path);
            if (Focused)
            {
                using var pen = new Pen(_palette.Accent);
                e.Graphics.DrawPath(pen, path);
            }
            var textColor = !Enabled ? _palette.Muted : _primary ? _palette.Accent : _palette.Text;
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(12, 0, Width - 24, Height), textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _hoverTimer.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed record TrayPalette(Color Panel, Color Panel2, Color Text, Color Muted, Color Accent)
    {
        public Color Border => Blend(Panel, Text, .13f);

        public static TrayPalette For(AppTheme theme)
        {
            var colors = theme switch
            {
                AppTheme.Darker => ("#0d0d0d", "#171717", "#fafafa", "#929292", "#ffffff"),
                AppTheme.Midnight => ("#0d1322", "#111a2e", "#f4f7ff", "#8592ad", "#7da2ff"),
                AppTheme.RedNeon => ("#180d11", "#211116", "#fff5f6", "#a4888d", "#ff667d"),
                AppTheme.Emerald => ("#0b1714", "#10211d", "#f0fff9", "#7f9c92", "#5ee5a2"),
                AppTheme.Violet => ("#151020", "#1d152b", "#fbf7ff", "#9687a7", "#b68cff"),
                AppTheme.Amber => ("#18130c", "#211a10", "#fffaf0", "#a2957d", "#ffc260"),
                AppTheme.PureLight => ("#ffffff", "#f4f4f5", "#202020", "#717171", "#303030"),
                AppTheme.IceBlue => ("#ffffff", "#eaf1ff", "#142038", "#65728b", "#4678e8"),
                AppTheme.WhiteRed => ("#ffffff", "#ffecef", "#2b1418", "#84666c", "#e8415b"),
                AppTheme.MintGlass => ("#ffffff", "#e4f8ef", "#12271f", "#628074", "#229f70"),
                AppTheme.LightLavender => ("#ffffff", "#f0e8fb", "#281b37", "#786989", "#8558c9"),
                AppTheme.PeachCream => ("#ffffff", "#ffecdc", "#302016", "#887263", "#db713e"),
                AppTheme.SkyGradient => ("#ffffff", "#e5f5ff", "#122633", "#627b89", "#258ec1"),
                _ => ("#242424", "#2c302e", "#ececec", "#a0a0a0", "#ececec")
            };
            return new TrayPalette(ColorTranslator.FromHtml(colors.Item1), ColorTranslator.FromHtml(colors.Item2),
                ColorTranslator.FromHtml(colors.Item3), ColorTranslator.FromHtml(colors.Item4), ColorTranslator.FromHtml(colors.Item5));
        }

    }

    private static Color Blend(Color from, Color to, float amount) => Color.FromArgb(
        (int)(from.R + (to.R - from.R) * amount),
        (int)(from.G + (to.G - from.G) * amount),
        (int)(from.B + (to.B - from.B) * amount));
}
