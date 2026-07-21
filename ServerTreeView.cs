using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace HappBox;

public enum SubscriptionAction
{
    Refresh,
    Rename,
    Delete
}

public sealed record LatencySnapshot(long? Current, long? Minimum, long? Average, long? Maximum, IReadOnlyList<long?> Samples);

internal static class UiAnimationClock
{
    private static readonly System.Windows.Forms.Timer Timer = new() { Interval = 16 };
    private static readonly HashSet<Func<bool>> Animations = [];
    private static bool _highResolutionActive;

    static UiAnimationClock()
    {
        Timer.Tick += (_, _) =>
        {
            foreach (var animation in Animations.ToArray())
            {
                if (!animation()) Animations.Remove(animation);
            }
            if (Animations.Count == 0) StopTimer();
        };
    }

    public static void Start(Func<bool> animation, Control? owner = null)
    {
        if (owner is not null) Timer.Interval = DisplayRefreshRate.FrameInterval(owner);
        Animations.Add(animation);
        if (!_highResolutionActive)
        {
            timeBeginPeriod(1);
            _highResolutionActive = true;
        }
        Timer.Start();
    }

    public static void Stop(Func<bool> animation)
    {
        Animations.Remove(animation);
        if (Animations.Count == 0) StopTimer();
    }

    private static void StopTimer()
    {
        Timer.Stop();
        if (!_highResolutionActive) return;
        timeEndPeriod(1);
        _highResolutionActive = false;
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);
}

internal static class DisplayRefreshRate
{
    public static int FrameInterval(Control control)
    {
        try
        {
            var mode = new DevMode { dmSize = (short)System.Runtime.InteropServices.Marshal.SizeOf<DevMode>() };
            var device = Screen.FromControl(control).DeviceName;
            if (EnumDisplaySettings(device, -1, ref mode) && mode.dmDisplayFrequency is >= 30 and <= 500)
                return Math.Clamp((int)Math.Round(1000d / mode.dmDisplayFrequency), 2, 16);
        }
        catch
        {
        }
        return 16;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct DevMode
    {
        private const int CchDeviceName = 32;
        private const int CchFormName = 32;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = CchDeviceName)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = CchFormName)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DevMode devMode);
}

public sealed class ServerTreeView : Control
{
    private const int GroupHeight = 60;
    private const int EndpointHeight = 42;
    private const int ProfileHeight = 56;
    private readonly Func<bool> _animationFrame;
    private readonly Dictionary<string, float> _expansion = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly List<TreeRow> _rows = [];
    private IReadOnlyList<ProxyProfile> _profiles = [];
    private IReadOnlyList<ProxySubscription> _subscriptions = [];
    private IReadOnlyDictionary<string, LatencySnapshot> _latencies = new Dictionary<string, LatencySnapshot>();
    private string? _selectedProfileId;
    private int _scrollOffset;
    private float _scrollPosition;
    private int _hoveredIndex = -1;
    private int _pressedIndex = -1;
    private string _filterText = "";
    private IReadOnlySet<string> _refreshingSubscriptions = new HashSet<string>();

    public event EventHandler<ProxyProfile>? ProfileSelected;
    public event EventHandler<(ProxySubscription Subscription, SubscriptionAction Action)>? SubscriptionActionRequested;
    public event EventHandler<ProxyProfile>? ProfileMenuRequested;

    public ServerTreeView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.FromArgb(37, 39, 39);
        TabStop = true;
        _animationFrame = Animate;
        MouseWheel += (_, e) =>
        {
            _scrollOffset = Math.Clamp(_scrollOffset - Math.Sign(e.Delta) * 116, 0, MaximumScroll());
            UiAnimationClock.Start(_animationFrame);
        };
    }

    public string FilterText
    {
        get => _filterText;
        set
        {
            value ??= "";
            if (_filterText == value) return;
            _filterText = value.Trim();
            RebuildRows();
        }
    }

    public void SetData(
        IReadOnlyList<ProxyProfile> profiles,
        IReadOnlyList<ProxySubscription> subscriptions,
        string? selectedProfileId,
        IReadOnlyDictionary<string, LatencySnapshot> latencies,
        IReadOnlySet<string>? refreshingSubscriptions = null)
    {
        _profiles = profiles;
        _subscriptions = subscriptions;
        _selectedProfileId = selectedProfileId;
        _latencies = latencies;
        _refreshingSubscriptions = refreshingSubscriptions ?? new HashSet<string>();
        if (_refreshingSubscriptions.Count > 0) UiAnimationClock.Start(_animationFrame);

        foreach (var subscription in subscriptions)
        {
            EnsureExpanded(GroupKey(subscription.Id));
        }
        RebuildRows();
    }

    public void UpdateLatencies(IReadOnlyDictionary<string, LatencySnapshot> latencies)
    {
        _latencies = latencies;
        Invalidate();
    }

    public void SelectProfile(string? profileId)
    {
        _selectedProfileId = profileId;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(BackColor);

        var y = -(int)_scrollPosition;
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var height = RowHeight(row);
            if (height <= 1) continue;
            var bounds = new Rectangle(0, y, ClientSize.Width, height);
            if (bounds.Bottom >= 0 && bounds.Top <= ClientSize.Height)
            {
                DrawRow(g, row, bounds, i == _hoveredIndex, i == _pressedIndex);
            }
            y += height;
        }

        DrawScrollBar(g);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = HitRow(e.Location);
        if (index == _hoveredIndex) return;
        _hoveredIndex = index;
        Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoveredIndex = -1;
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var index = HitRow(e.Location);
        _pressedIndex = index;
        Invalidate();
        if (index < 0 || index >= _rows.Count) return;
        var row = _rows[index];
        var bounds = RowBounds(index);

        if (row.Kind == TreeRowKind.Group)
        {
            if (row.Subscription is not null && e.X >= bounds.Right - 74)
            {
                var action = e.X >= bounds.Right - 36 ? SubscriptionAction.Rename : SubscriptionAction.Refresh;
                SubscriptionActionRequested?.Invoke(this, (row.Subscription, action));
                return;
            }
            Toggle(row.ExpansionKey);
            return;
        }

        if (row.Kind == TreeRowKind.Endpoint)
        {
            Toggle(row.ExpansionKey);
            return;
        }

        if (row.Profile is null) return;
        if (e.Button == MouseButtons.Right)
        {
            ProfileMenuRequested?.Invoke(this, row.Profile);
            return;
        }

        _selectedProfileId = row.Profile.Id;
        ProfileSelected?.Invoke(this, row.Profile);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressedIndex = -1;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override bool IsInputKey(Keys keyData)
    {
        return keyData is Keys.Up or Keys.Down or Keys.Enter || base.IsInputKey(keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var selectable = _rows
            .Select((row, index) => (row, index))
            .Where(x => x.row.Kind == TreeRowKind.Profile && RowHeight(x.row) > 1)
            .ToList();
        if (selectable.Count == 0) return;

        var current = selectable.FindIndex(x => x.row.Profile?.Id == _selectedProfileId);
        if (e.KeyCode == Keys.Enter && current >= 0)
        {
            ProfileSelected?.Invoke(this, selectable[current].row.Profile!);
            e.Handled = true;
            return;
        }
        if (e.KeyCode is not (Keys.Up or Keys.Down)) return;

        current = e.KeyCode == Keys.Down
            ? Math.Min(selectable.Count - 1, current + 1)
            : Math.Max(0, current < 0 ? 0 : current - 1);
        var selected = selectable[current];
        _selectedProfileId = selected.row.Profile!.Id;
        ProfileSelected?.Invoke(this, selected.row.Profile);
        EnsureRowVisible(selected.index);
        e.Handled = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) UiAnimationClock.Stop(_animationFrame);
        base.Dispose(disposing);
    }

    private void RebuildRows()
    {
        _rows.Clear();
        foreach (var subscription in _subscriptions)
        {
            var children = _profiles.Where(x => x.SubscriptionId == subscription.Id).ToList();
            if (!Matches(subscription, children)) continue;
            AddGroup(subscription, children, SubscriptionEndpoint(subscription));
        }

        var local = _profiles.Where(x => string.IsNullOrWhiteSpace(x.SubscriptionId)).ToList();
        foreach (var profile in local.Where(ProfileMatches))
        {
            _rows.Add(new TreeRow(TreeRowKind.Profile, profile.Name, "", null, profile, null, 1f));
        }

        _scrollOffset = Math.Clamp(_scrollOffset, 0, MaximumScroll());
        Invalidate();
    }

    private void AddGroup(ProxySubscription? subscription, List<ProxyProfile> profiles, string title)
    {
        var groupId = subscription?.Id ?? "local";
        var groupKey = GroupKey(groupId);
        _rows.Add(new TreeRow(TreeRowKind.Group, title, groupKey, subscription, null, null, 1f));
        var groupProgress = Progress(groupKey);
        if (groupProgress <= 0.01f) return;

        foreach (var profile in profiles.Where(ProfileMatches))
        {
            _rows.Add(new TreeRow(TreeRowKind.Profile, profile.Name, "", subscription, profile, null, groupProgress));
        }
    }

    private bool Matches(ProxySubscription? subscription, List<ProxyProfile> profiles)
    {
        if (string.IsNullOrWhiteSpace(_filterText)) return true;
        if (subscription?.Name.Contains(_filterText, StringComparison.OrdinalIgnoreCase) == true) return true;
        return profiles.Any(ProfileMatches);
    }

    private bool ProfileMatches(ProxyProfile profile)
    {
        if (string.IsNullOrWhiteSpace(_filterText)) return true;
        return profile.Name.Contains(_filterText, StringComparison.OrdinalIgnoreCase) ||
               profile.Server.Contains(_filterText, StringComparison.OrdinalIgnoreCase) ||
               profile.Protocol.ToString().Contains(_filterText, StringComparison.OrdinalIgnoreCase) ||
               $"{profile.Server}:{profile.Port}".Contains(_filterText, StringComparison.OrdinalIgnoreCase);
    }

    private void DrawRow(Graphics g, TreeRow row, Rectangle bounds, bool hovered, bool pressed)
    {
        switch (row.Kind)
        {
            case TreeRowKind.Group:
                DrawGroup(g, row, bounds, hovered, pressed);
                break;
            case TreeRowKind.Endpoint:
                DrawEndpoint(g, row, bounds, hovered);
                break;
            case TreeRowKind.Profile:
                DrawProfile(g, row, bounds, hovered, pressed);
                break;
        }
    }

    private void DrawGroup(Graphics g, TreeRow row, Rectangle bounds, bool hovered, bool pressed)
    {
        var rect = Rectangle.Inflate(bounds, -8, -4);
        var groupColor = pressed ? Color.FromArgb(63, 65, 72) : hovered ? Color.FromArgb(54, 57, 63) : Color.FromArgb(43, 45, 50);
        using var background = new SolidBrush(groupColor);
        using var path = Rounded(rect, 7);
        g.FillPath(background, path);

        using var titleFont = new Font("Segoe UI", 10.2f, FontStyle.Bold);
        using var smallFont = new Font("Segoe UI", 8.2f);
        var expansionProgress = Progress(row.ExpansionKey);
        DrawExpansionRail(g, new Rectangle(rect.Left + 11, rect.Top + 7, 4, rect.Height - 14), expansionProgress);
        TextRenderer.DrawText(g, row.Title, titleFont, new Rectangle(rect.Left + 27, rect.Top + 5, rect.Width - 119, 25),
            Color.White, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);

        var count = _profiles.Count(x => row.Subscription is null
            ? string.IsNullOrWhiteSpace(x.SubscriptionId)
            : x.SubscriptionId == row.Subscription.Id);
        var detail = row.Subscription is null ? $"{count} узл." : SubscriptionDetail(row.Subscription, count);
        TextRenderer.DrawText(g, detail, smallFont, new Rectangle(rect.Left + 27, rect.Top + 30, rect.Width - 109, 18),
            Color.FromArgb(166, 168, 178), TextFormatFlags.EndEllipsis);

        if (row.Subscription is not null)
        {
            if (_refreshingSubscriptions.Contains(row.Subscription.Id))
                UiGlyphs.DrawSpinner(g, new Rectangle(rect.Right - 64, rect.Top + 18, 18, 18), Color.FromArgb(166, 151, 255));
            else
                UiGlyphs.DrawRefresh(g, new Rectangle(rect.Right - 64, rect.Top + 18, 18, 18), Color.FromArgb(174, 164, 255), 1.8f);
            TextRenderer.DrawText(g, "⋯", new Font("Segoe UI", 14f, FontStyle.Bold), new Rectangle(rect.Right - 36, rect.Top, 30, rect.Height),
                Color.FromArgb(195, 197, 205), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    private void DrawEndpoint(Graphics g, TreeRow row, Rectangle bounds, bool hovered)
    {
        var rect = new Rectangle(bounds.Left + 20, bounds.Top, Math.Max(1, bounds.Width - 28), bounds.Height);
        if (hovered)
        {
            using var hover = new SolidBrush(Color.FromArgb(43, 45, 49));
            g.FillRectangle(hover, rect);
        }
        using var line = new Pen(Color.FromArgb(54, 56, 61));
        g.DrawLine(line, rect.Left + 20, rect.Bottom - 1, rect.Right - 8, rect.Bottom - 1);
        var profile = row.EndpointProfiles?.FirstOrDefault();
        DrawCountryBadge(g, profile?.CountryCode, new Rectangle(rect.Left + 10, rect.Top + 12, 24, 18));
        TextRenderer.DrawText(g, row.Title, new Font("Segoe UI", 9.4f, FontStyle.Bold),
            new Rectangle(rect.Left + 44, rect.Top, rect.Width - 148, rect.Height), Color.FromArgb(230, 232, 238),
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        var latency = profile is null ? null : LatencyFor(profile)?.Current;
        var latencyText = latency is null ? "—" : $"{latency} ms";
        TextRenderer.DrawText(g, latencyText, new Font("Segoe UI", 8.8f, FontStyle.Bold),
            new Rectangle(rect.Right - 92, rect.Top, 70, rect.Height), LatencyColor(latency),
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, _expanded.Contains(row.ExpansionKey) ? "⌄" : "›", new Font("Segoe UI Symbol", 9f),
            new Rectangle(rect.Right - 24, rect.Top, 20, rect.Height), Color.FromArgb(155, 157, 168),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private void DrawProfile(Graphics g, TreeRow row, Rectangle bounds, bool hovered, bool pressed)
    {
        var profile = row.Profile!;
        var selected = profile.Id == _selectedProfileId;
        var reveal = Math.Clamp(row.ParentProgress, 0f, 1f);
        var shift = row.Subscription is null ? 0 : (int)Math.Round((1f - reveal) * 10f);
        var fullHeight = ProfileHeight - 6;
        var rect = new Rectangle(bounds.Left + 12 + shift, bounds.Top + (bounds.Height - fullHeight) / 2,
            Math.Max(1, bounds.Width - 22 - shift), fullHeight);
        var clipState = g.Save();
        g.SetClip(bounds);
        if (selected || hovered)
        {
            var background = pressed ? Color.FromArgb(68, 69, 76) : selected ? Color.FromArgb(61, 62, 70) : Color.FromArgb(46, 48, 53);
            using var bg = new SolidBrush(BlendColor(BackColor, background, reveal));
            using var path = Rounded(rect, 6);
            g.FillPath(bg, path);
        }
        if (selected)
        {
            using var accent = new SolidBrush(BlendColor(BackColor, Color.FromArgb(124, 105, 255), reveal));
            using var path = Rounded(new Rectangle(rect.Left, rect.Top + 5, 3, rect.Height - 10), 2);
            g.FillPath(accent, path);
        }

        var flagRect = new Rectangle(rect.Left + 14, rect.Top + 16, 24, 18);
        DrawCountryFlag(g, profile.CountryCode, flagRect);
        if (reveal < 0.999f)
        {
            using var fade = new SolidBrush(Color.FromArgb((int)Math.Round((1f - reveal) * 255), BackColor));
            g.FillRectangle(fade, flagRect);
        }
        TextRenderer.DrawText(g, profile.Name, new Font("Segoe UI", 9.4f, FontStyle.Bold),
            new Rectangle(rect.Left + 50, rect.Top + 5, rect.Width - 142, 22), BlendColor(BackColor, Color.White, reveal),
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        var details = ProtocolDescription(profile);
        TextRenderer.DrawText(g, details, new Font("Segoe UI", 8.2f),
            new Rectangle(rect.Left + 50, rect.Top + 28, rect.Width - 142, 18), BlendColor(BackColor, Color.FromArgb(166, 168, 180), reveal),
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        var latency = LatencyFor(profile)?.Current;
        TextRenderer.DrawText(g, latency is null ? "—" : $"{latency} ms", new Font("Segoe UI", 8.8f, FontStyle.Bold),
            new Rectangle(rect.Right - 92, rect.Top, 78, rect.Height), BlendColor(BackColor, LatencyColor(latency), reveal),
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        g.Restore(clipState);
    }

    private static void DrawExpansionRail(Graphics g, Rectangle bounds, float progress)
    {
        var eased = 1f - MathF.Pow(1f - Math.Clamp(progress, 0f, 1f), 3f);
        var height = (int)Math.Round(10 + (bounds.Height - 10) * eased);
        var rail = new Rectangle(bounds.Left, bounds.Top + (bounds.Height - height) / 2, bounds.Width, height);
        using var brush = new SolidBrush(BlendColor(Color.FromArgb(103, 106, 116), Color.FromArgb(124, 105, 255), eased));
        using var path = Rounded(rail, 2);
        g.FillPath(brush, path);
    }

    private static Color BlendColor(Color from, Color to, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            (int)Math.Round(from.R + (to.R - from.R) * amount),
            (int)Math.Round(from.G + (to.G - from.G) * amount),
            (int)Math.Round(from.B + (to.B - from.B) * amount));
    }

    private void DrawScrollBar(Graphics g)
    {
        var content = ContentHeight();
        if (content <= ClientSize.Height || ClientSize.Height <= 0) return;
        var track = new Rectangle(ClientSize.Width - 5, 6, 3, ClientSize.Height - 12);
        var thumbHeight = Math.Max(28, track.Height * ClientSize.Height / content);
        var maxScroll = Math.Max(1, content - ClientSize.Height);
        var thumbTop = track.Top + (int)((track.Height - thumbHeight) * (_scrollPosition / maxScroll));
        using var brush = new SolidBrush(Color.FromArgb(102, 104, 114));
        using var path = Rounded(new Rectangle(track.Left, thumbTop, track.Width, thumbHeight), 2);
        g.FillPath(brush, path);
    }

    private void Toggle(string key)
    {
        if (!_expanded.Add(key)) _expanded.Remove(key);
        UiAnimationClock.Start(_animationFrame);
    }

    private bool Animate()
    {
        var moving = _refreshingSubscriptions.Count > 0;
        foreach (var key in _expansion.Keys.ToList())
        {
            var target = _expanded.Contains(key) ? 1f : 0f;
            var next = _expansion[key] + (target - _expansion[key]) * 0.24f;
            if (Math.Abs(next - target) < 0.015f) next = target;
            else moving = true;
            _expansion[key] = next;
        }
        var nextScroll = _scrollPosition + (_scrollOffset - _scrollPosition) * 0.25f;
        if (Math.Abs(nextScroll - _scrollOffset) < 0.5f) nextScroll = _scrollOffset;
        else moving = true;
        _scrollPosition = nextScroll;
        RebuildRows();
        return moving;
    }

    private void EnsureRowVisible(int index)
    {
        var bounds = RowBounds(index);
        if (bounds.Top < 0) _scrollOffset = Math.Max(0, _scrollOffset + bounds.Top - 8);
        else if (bounds.Bottom > ClientSize.Height) _scrollOffset = Math.Min(MaximumScroll(), _scrollOffset + bounds.Bottom - ClientSize.Height + 8);
        UiAnimationClock.Start(_animationFrame);
    }

    private void EnsureExpanded(string key)
    {
        if (_expansion.ContainsKey(key)) return;
        _expanded.Add(key);
        _expansion[key] = 1f;
    }

    private float Progress(string key) => _expansion.TryGetValue(key, out var value) ? value : 0f;
    private int RowHeight(TreeRow row) => Math.Max(0, (int)Math.Round((row.Kind switch
    {
        TreeRowKind.Group => GroupHeight,
        TreeRowKind.Endpoint => EndpointHeight,
        _ => ProfileHeight
    }) * row.ParentProgress));

    private int ContentHeight() => _rows.Sum(RowHeight);
    private int MaximumScroll() => Math.Max(0, ContentHeight() - ClientSize.Height);

    private int HitRow(Point point)
    {
        var y = -(int)_scrollPosition;
        for (var i = 0; i < _rows.Count; i++)
        {
            var height = RowHeight(_rows[i]);
            if (point.Y >= y && point.Y < y + height) return i;
            y += height;
        }
        return -1;
    }

    private Rectangle RowBounds(int index)
    {
        var y = -(int)_scrollPosition;
        for (var i = 0; i < index; i++) y += RowHeight(_rows[i]);
        return new Rectangle(0, y, ClientSize.Width, RowHeight(_rows[index]));
    }

    private LatencySnapshot? LatencyFor(ProxyProfile profile)
    {
        return _latencies.TryGetValue(EndpointKey(profile), out var value) ? value : null;
    }

    public static string EndpointKey(ProxyProfile profile) => $"{profile.Server.ToLowerInvariant()}:{profile.Port}";
    private static string GroupKey(string id) => $"group:{id}";
    private static string EndpointExpansionKey(string id) => $"endpoint:{id}";

    private static string ProtocolDescription(ProxyProfile profile)
    {
        var security = profile.RealityEnabled ? "Reality" : profile.TlsEnabled ? "TLS" : "без TLS";
        var transport = string.IsNullOrWhiteSpace(profile.Network) ? "" : $" · {profile.Network.ToUpperInvariant()}";
        return $"{profile.Protocol}{transport} · {security}";
    }

    private static string SubscriptionDetail(ProxySubscription subscription, int count)
    {
        var parts = new List<string> { subscription.Name, $"{count} прот." };
        if (subscription.TotalBytes is > 0)
        {
            var used = subscription.UploadBytes.GetValueOrDefault() + subscription.DownloadBytes.GetValueOrDefault();
            parts.Add($"{FormatBytes(used)} / {FormatBytes(subscription.TotalBytes.Value)}");
        }
        if (!string.IsNullOrWhiteSpace(subscription.LastError)) parts.Add("ошибка обновления");
        else if (subscription.LastUpdatedUtc is not null) parts.Add($"обновлено {subscription.LastUpdatedUtc.Value.ToLocalTime():dd.MM HH:mm}");
        return string.Join(" · ", parts);
    }

    private static string SubscriptionEndpoint(ProxySubscription subscription)
    {
        if (!Uri.TryCreate(subscription.Url, UriKind.Absolute, out var uri)) return subscription.Name;
        var port = uri.IsDefaultPort ? (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80) : uri.Port;
        return $"{uri.Host}:{port}";
    }

    private static string FormatBytes(long value)
    {
        const double gb = 1024d * 1024d * 1024d;
        return value >= gb ? $"{value / gb:0.#} GB" : $"{value / 1024d / 1024d:0} MB";
    }

    private static Color LatencyColor(long? latency) => latency switch
    {
        null => Color.FromArgb(132, 134, 144),
        <= 120 => Color.FromArgb(92, 220, 148),
        <= 260 => Color.FromArgb(255, 190, 92),
        _ => Color.FromArgb(255, 116, 116)
    };

    private static void DrawCountryBadge(Graphics g, string? code, Rectangle bounds)
    {
        using var bg = new SolidBrush(Color.FromArgb(55, 58, 66));
        using var border = new Pen(Color.FromArgb(78, 81, 90));
        using var path = Rounded(bounds, 4);
        g.FillPath(bg, path);
        g.DrawPath(border, path);
        TextRenderer.DrawText(g, string.IsNullOrWhiteSpace(code) ? "--" : code.ToUpperInvariant(),
            new Font("Segoe UI", 6.8f, FontStyle.Bold), bounds, Color.FromArgb(218, 220, 228),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private static void DrawCountryFlag(Graphics g, string? code, Rectangle bounds)
    {
        code = code?.ToUpperInvariant();
        using var clip = Rounded(bounds, 4);
        g.SetClip(clip);
        switch (code)
        {
            case "SE":
                using (var blue = new SolidBrush(Color.FromArgb(0, 106, 167))) g.FillRectangle(blue, bounds);
                using (var yellow = new SolidBrush(Color.FromArgb(254, 204, 0)))
                {
                    g.FillRectangle(yellow, bounds.Left + 7, bounds.Top, 4, bounds.Height);
                    g.FillRectangle(yellow, bounds.Left, bounds.Top + 7, bounds.Width, 4);
                }
                break;
            case "FI":
                g.FillRectangle(Brushes.White, bounds);
                using (var blue = new SolidBrush(Color.FromArgb(0, 53, 128)))
                {
                    g.FillRectangle(blue, bounds.Left + 7, bounds.Top, 4, bounds.Height);
                    g.FillRectangle(blue, bounds.Left, bounds.Top + 7, bounds.Width, 4);
                }
                break;
            case "NL":
                g.FillRectangle(Brushes.Firebrick, bounds.Left, bounds.Top, bounds.Width, 6);
                g.FillRectangle(Brushes.White, bounds.Left, bounds.Top + 6, bounds.Width, 6);
                g.FillRectangle(Brushes.RoyalBlue, bounds.Left, bounds.Top + 12, bounds.Width, 6);
                break;
            case "DE":
                g.FillRectangle(Brushes.Black, bounds.Left, bounds.Top, bounds.Width, 6);
                g.FillRectangle(Brushes.Firebrick, bounds.Left, bounds.Top + 6, bounds.Width, 6);
                g.FillRectangle(Brushes.Gold, bounds.Left, bounds.Top + 12, bounds.Width, 6);
                break;
            case "RU":
                g.FillRectangle(Brushes.White, bounds.Left, bounds.Top, bounds.Width, 6);
                g.FillRectangle(Brushes.RoyalBlue, bounds.Left, bounds.Top + 6, bounds.Width, 6);
                g.FillRectangle(Brushes.Firebrick, bounds.Left, bounds.Top + 12, bounds.Width, 6);
                break;
            case "FR":
                g.FillRectangle(Brushes.RoyalBlue, bounds.Left, bounds.Top, 8, bounds.Height);
                g.FillRectangle(Brushes.White, bounds.Left + 8, bounds.Top, 8, bounds.Height);
                g.FillRectangle(Brushes.Firebrick, bounds.Left + 16, bounds.Top, 8, bounds.Height);
                break;
            case "MD":
                g.FillRectangle(Brushes.RoyalBlue, bounds.Left, bounds.Top, 8, bounds.Height);
                g.FillRectangle(Brushes.Gold, bounds.Left + 8, bounds.Top, 8, bounds.Height);
                g.FillRectangle(Brushes.Firebrick, bounds.Left + 16, bounds.Top, 8, bounds.Height);
                break;
            case "PL":
                g.FillRectangle(Brushes.White, bounds.Left, bounds.Top, bounds.Width, 9);
                g.FillRectangle(Brushes.Crimson, bounds.Left, bounds.Top + 9, bounds.Width, 9);
                break;
            case "UA":
                g.FillRectangle(Brushes.RoyalBlue, bounds.Left, bounds.Top, bounds.Width, 9);
                g.FillRectangle(Brushes.Gold, bounds.Left, bounds.Top + 9, bounds.Width, 9);
                break;
            case "US":
                for (var i = 0; i < 6; i++)
                {
                    using var stripe = new SolidBrush(i % 2 == 0 ? Color.Firebrick : Color.White);
                    g.FillRectangle(stripe, bounds.Left, bounds.Top + i * 3, bounds.Width, 3);
                }
                g.FillRectangle(Brushes.Navy, bounds.Left, bounds.Top, 11, 9);
                break;
            default:
                using (var fallback = new SolidBrush(Color.FromArgb(49, 52, 60))) g.FillRectangle(fallback, bounds);
                TextRenderer.DrawText(g, "◎", new Font("Segoe UI Symbol", 10f), bounds, Color.FromArgb(190, 194, 205),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                break;
        }
        g.ResetClip();
        using var border = new Pen(Color.FromArgb(82, 84, 94));
        using var outline = Rounded(bounds, 4);
        g.DrawPath(border, outline);
    }

    private static void DrawChevron(Graphics g, Rectangle bounds, float progress, Color color)
    {
        var angle = progress * 90f;
        var center = new PointF(bounds.Left + bounds.Width / 2f, bounds.Top + bounds.Height / 2f);
        var state = g.Save();
        g.TranslateTransform(center.X, center.Y);
        g.RotateTransform(angle);
        using var pen = new Pen(color, 1.7f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        var size = Math.Min(bounds.Width, bounds.Height) * 0.25f;
        g.DrawLines(pen, [new PointF(-size, -size), new PointF(size, 0), new PointF(-size, size)]);
        g.Restore(state);
    }

    private static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private enum TreeRowKind { Group, Endpoint, Profile }
    private sealed record TreeRow(
        TreeRowKind Kind,
        string Title,
        string ExpansionKey,
        ProxySubscription? Subscription,
        ProxyProfile? Profile,
        IReadOnlyList<ProxyProfile>? EndpointProfiles,
        float ParentProgress);
}

public sealed class LatencyChart : Control
{
    private LatencySnapshot? _snapshot;
    private IReadOnlyList<long?> _previousSamples = [];
    private float _transition = 1f;
    private readonly Func<bool> _animationFrame;

    public LatencyChart()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        MinimumSize = new Size(220, 82);
        _animationFrame = Animate;
    }

    public void SetSnapshot(LatencySnapshot? snapshot)
    {
        _previousSamples = DisplaySamples().ToArray();
        _snapshot = snapshot;
        _transition = 0f;
        UiAnimationClock.Start(_animationFrame);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) UiAnimationClock.Stop(_animationFrame);
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = Rectangle.Inflate(ClientRectangle, -1, -1);
        using var bg = new SolidBrush(Color.FromArgb(28, 30, 36));
        using var border = new Pen(Color.FromArgb(60, 63, 73));
        using var path = Rounded(bounds, 10);
        g.FillPath(bg, path);
        g.DrawPath(border, path);

        var compact = bounds.Width < 400;
        TextRenderer.DrawText(g, compact ? "Пинг" : "Пинг сервера", new Font("Segoe UI", compact ? 8.2f : 9.2f, FontStyle.Bold),
            new Rectangle(bounds.Left + 16, bounds.Top + 9, compact ? 54 : Math.Max(1, bounds.Width / 3), 22), Color.FromArgb(230, 232, 239),
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        var chart = new Rectangle(bounds.Left + 16, bounds.Top + 43, bounds.Width - 32, Math.Max(20, bounds.Height - 58));
        using var grid = new Pen(Color.FromArgb(48, 51, 60));
        for (var i = 1; i <= 3; i++)
        {
            var y = chart.Top + chart.Height * i / 4;
            g.DrawLine(grid, chart.Left, y, chart.Right, y);
        }

        var samples = DisplaySamples();
        var valid = samples.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        if (valid.Count > 0)
        {
            var max = Math.Max(50, valid.Max());
            PointF? previous = null;
            using var line = new Pen(Color.FromArgb(139, 123, 255), 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            for (var i = 0; i < samples.Count; i++)
            {
                if (samples[i] is not { } value)
                {
                    previous = null;
                    continue;
                }
                var x = chart.Left + (samples.Count <= 1 ? chart.Width : chart.Width * i / (float)(samples.Count - 1));
                var y = chart.Bottom - Math.Min(1f, value / (float)max) * chart.Height;
                var point = new PointF(x, y);
                if (previous is not null) g.DrawLine(line, previous.Value, point);
                previous = point;
            }
        }

        var summary = _snapshot is null
            ? "— ms"
            : compact
                ? $"{_snapshot.Current?.ToString() ?? "—"} ms  ·  мин {_snapshot.Minimum?.ToString() ?? "—"}  ·  ср {_snapshot.Average?.ToString() ?? "—"}  ·  макс {_snapshot.Maximum?.ToString() ?? "—"}"
                : $"Сейчас {_snapshot.Current?.ToString() ?? "—"} ms   Мин {_snapshot.Minimum?.ToString() ?? "—"}   Ср {_snapshot.Average?.ToString() ?? "—"}   Макс {_snapshot.Maximum?.ToString() ?? "—"}";
        var summaryLeft = compact ? bounds.Left + 66 : bounds.Left + bounds.Width / 3;
        TextRenderer.DrawText(g, summary, new Font("Segoe UI", compact ? 7.1f : 8.2f, FontStyle.Bold),
            new Rectangle(summaryLeft, bounds.Top + 9, bounds.Right - summaryLeft - 16, 22), Color.FromArgb(176, 180, 192),
            TextFormatFlags.Right | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
    }

    private IReadOnlyList<long?> DisplaySamples()
    {
        var target = _snapshot?.Samples ?? [];
        if (_transition >= 0.999f || _previousSamples.Count == 0) return target;
        var count = Math.Max(_previousSamples.Count, target.Count);
        var values = new long?[count];
        for (var i = 0; i < count; i++)
        {
            var oldValue = i < _previousSamples.Count ? _previousSamples[i] : null;
            var newValue = i < target.Count ? target[i] : null;
            values[i] = oldValue is not null && newValue is not null
                ? (long)Math.Round(oldValue.Value + (newValue.Value - oldValue.Value) * _transition)
                : newValue;
        }
        return values;
    }

    private bool Animate()
    {
        _transition += (1f - _transition) * 0.22f;
        if (1f - _transition < 0.01f) _transition = 1f;
        Invalidate();
        return _transition < 1f;
    }

    private static GraphicsPath Rounded(Rectangle bounds, int radius)
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
