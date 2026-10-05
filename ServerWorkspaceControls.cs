using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace HappBox;

internal enum ConnectionVisualState
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,
    Failed
}

internal sealed class GlassPanel : Panel
{
    public int Radius { get; set; } = 14;
    public Color SurfaceColor { get; set; } = Color.FromArgb(30, 32, 37);
    public Color BorderColor { get; set; } = Color.FromArgb(62, 65, 74);

    public GlassPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Color.FromArgb(18, 20, 20));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = Rectangle.Inflate(ClientRectangle, -1, -1);
        if (rect.Width < 2 || rect.Height < 2) return;
        using var path = Rounded(rect, Radius);
        using var fill = new LinearGradientBrush(rect,
            Blend(SurfaceColor, Color.White, 0.035f),
            Blend(SurfaceColor, Color.Black, 0.08f),
            LinearGradientMode.Vertical);
        using var border = new Pen(BorderColor, 1f) { Alignment = PenAlignment.Inset };
        g.FillPath(fill, path);
        g.DrawPath(border, path);
        using var highlight = new Pen(Color.FromArgb(22, Color.White));
        g.DrawLine(highlight, rect.Left + Radius, rect.Top + 1, rect.Right - Radius, rect.Top + 1);
    }

    private static Color Blend(Color from, Color to, float amount) => Color.FromArgb(
        (int)(from.A + (to.A - from.A) * amount),
        (int)(from.R + (to.R - from.R) * amount),
        (int)(from.G + (to.G - from.G) * amount),
        (int)(from.B + (to.B - from.B) * amount));

    internal static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(Math.Min(bounds.Width, bounds.Height), radius * 2);
        if (diameter <= 1) { path.AddRectangle(bounds); return path; }
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class FlatInputPanel : Panel
{
    private readonly Func<bool> _animationFrame;
    private float _focusProgress;
    private float _hoverProgress;
    private bool _hovered;
    private long _lastAnimationTick;
    private int _radius = 8;
    public int Radius
    {
        get => _radius;
        set
        {
            _radius = Math.Max(1, value);
            UpdateRegion();
            Invalidate();
        }
    }
    public Color FillColor { get; set; } = Color.FromArgb(28, 30, 34);
    public Color BorderColor { get; set; } = Color.FromArgb(57, 60, 68);

    public FlatInputPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.FromArgb(35, 37, 41);
        _animationFrame = Animate;
        ControlAdded += (_, e) =>
        {
            if (e.Control is null) return;
            e.Control.BackColor = FillColor;
            e.Control.Enter += ChildFocusChanged;
            e.Control.Leave += ChildFocusChanged;
            e.Control.MouseEnter += ChildMouseEnter;
            e.Control.MouseLeave += ChildMouseLeave;
        };
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
    }

    protected override void OnResize(EventArgs eventargs)
    {
        UpdateRegion();
        base.OnResize(eventargs);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = Rectangle.Inflate(ClientRectangle, -1, -1);
        if (rect.Width < 2 || rect.Height < 2) return;
        using var path = GlassPanel.Rounded(rect, Radius);
        using var fill = new SolidBrush(FillColor);
        var idleBorder = Blend(BorderColor, Color.FromArgb(82, 85, 96), _hoverProgress);
        using var border = new Pen(Blend(idleBorder, Color.FromArgb(124, 105, 255), _focusProgress), 1f + _focusProgress * 0.45f)
        {
            Alignment = PenAlignment.Inset
        };
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        StartAnimation();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = ClientRectangle.Contains(PointToClient(Cursor.Position));
        StartAnimation();
        base.OnMouseLeave(e);
    }

    private void ChildFocusChanged(object? sender, EventArgs e) => StartAnimation();
    private void ChildMouseEnter(object? sender, EventArgs e) { _hovered = true; StartAnimation(); }
    private void ChildMouseLeave(object? sender, EventArgs e)
    {
        _hovered = ClientRectangle.Contains(PointToClient(Cursor.Position));
        StartAnimation();
    }

    private void StartAnimation()
    {
        _lastAnimationTick = Environment.TickCount64;
        UiAnimationClock.Start(_animationFrame, this);
    }

    private bool Animate()
    {
        if (IsDisposed) return false;
        var now = Environment.TickCount64;
        var elapsed = Math.Clamp(now - _lastAnimationTick, 1, 50);
        _lastAnimationTick = now;
        var target = ContainsFocus ? 1f : 0f;
        var hoverTarget = _hovered ? 1f : 0f;
        var step = (float)(elapsed / 150d);
        _focusProgress = target > _focusProgress
            ? Math.Min(target, _focusProgress + step)
            : Math.Max(target, _focusProgress - step);
        _hoverProgress = hoverTarget > _hoverProgress
            ? Math.Min(hoverTarget, _hoverProgress + step)
            : Math.Max(hoverTarget, _hoverProgress - step);
        Invalidate();
        return Math.Abs(_focusProgress - target) > 0.001f ||
               Math.Abs(_hoverProgress - hoverTarget) > 0.001f;
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

    private void UpdateRegion()
    {
        if (ClientSize.Width < 2 || ClientSize.Height < 2) return;
        using var path = GlassPanel.Rounded(ClientRectangle, Radius);
        var next = new Region(path);
        var previous = Region;
        Region = next;
        previous?.Dispose();
    }
}

internal sealed class ServiceEditorInputRow : TableLayoutPanel
{
    private readonly Func<bool> _animationFrame;
    private float _hoverProgress;
    private bool _hovered;
    private bool _pressed;
    private long _lastAnimationTick;

    public event EventHandler? ApplyRequested;

    public ServiceEditorInputRow()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        _animationFrame = Animate;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var rect = ApplyButtonRectangle();
        if (rect.Width < 4 || rect.Height < 4) return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = GlassPanel.Rounded(rect, 7);
        var top = Blend(Color.FromArgb(69, 70, 82), Color.FromArgb(114, 98, 198), _hoverProgress * 0.72f);
        var bottom = Blend(Color.FromArgb(43, 45, 54), Color.FromArgb(75, 65, 133), _hoverProgress * 0.65f);
        using var fill = new LinearGradientBrush(rect, top, bottom, LinearGradientMode.Vertical);
        using var border = new Pen(Blend(Color.FromArgb(88, 91, 105), Color.FromArgb(162, 146, 255), _hoverProgress), 1f);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);
        using var shine = new Pen(Color.FromArgb((int)(28 + 30 * _hoverProgress), Color.White), 1f);
        e.Graphics.DrawLine(shine, rect.Left + 8, rect.Top + 2, rect.Right - 8, rect.Top + 2);

        var cx = rect.Left + rect.Width / 2f;
        var cy = rect.Top + rect.Height / 2f + (_pressed ? 1f : 0f);
        using var circle = new SolidBrush(Color.FromArgb(236, 235, 244));
        e.Graphics.FillEllipse(circle, cx - 9, cy - 9, 18, 18);
        using var check = new Pen(Color.FromArgb(65, 60, 82), 2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        e.Graphics.DrawLines(check, [new PointF(cx - 5, cy), new PointF(cx - 1.5f, cy + 3), new PointF(cx + 5, cy - 4)]);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hovered = ApplyButtonRectangle().Contains(e.Location);
        if (hovered != _hovered)
        {
            _hovered = hovered;
            Cursor = hovered ? Cursors.Hand : Cursors.Default;
            StartAnimation();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        _pressed = false;
        Cursor = Cursors.Default;
        StartAnimation();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && ApplyButtonRectangle().Contains(e.Location))
        {
            _pressed = true;
            Invalidate(ApplyButtonRectangle());
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        var activate = _pressed && e.Button == MouseButtons.Left && ApplyButtonRectangle().Contains(e.Location);
        _pressed = false;
        Invalidate(ApplyButtonRectangle());
        if (activate) ApplyRequested?.Invoke(this, EventArgs.Empty);
        base.OnMouseUp(e);
    }

    private Rectangle ApplyButtonRectangle()
    {
        var widths = GetColumnWidths();
        if (widths.Length < 3) return Rectangle.Empty;
        var x = Padding.Left + widths[0] + widths[1] + 1;
        return new Rectangle(x, Padding.Top, Math.Max(0, widths[2] - 2), Math.Max(0, ClientSize.Height - Padding.Vertical));
    }

    private void StartAnimation()
    {
        _lastAnimationTick = Environment.TickCount64;
        UiAnimationClock.Start(_animationFrame, this);
    }

    private bool Animate()
    {
        if (IsDisposed) return false;
        var now = Environment.TickCount64;
        var elapsed = Math.Clamp(now - _lastAnimationTick, 1, 50);
        _lastAnimationTick = now;
        var target = _hovered ? 1f : 0f;
        var step = (float)(elapsed / 160d);
        _hoverProgress = target > _hoverProgress
            ? Math.Min(target, _hoverProgress + step)
            : Math.Max(target, _hoverProgress - step);
        Invalidate(ApplyButtonRectangle());
        return Math.Abs(_hoverProgress - target) > 0.001f;
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
}

internal sealed class GlassApplyButton : Button
{
    private readonly Func<bool> _animationFrame;
    private float _hoverProgress;
    private bool _hovered;
    private bool _pressed;
    private long _lastAnimationTick;

    public GlassApplyButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Color.FromArgb(31, 33, 39);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        FlatAppearance.MouseDownBackColor = BackColor;
        FlatAppearance.MouseOverBackColor = BackColor;
        UseVisualStyleBackColor = false;
        BackgroundImageLayout = ImageLayout.Stretch;
        Text = "✓";
        ForeColor = Color.FromArgb(242, 241, 248);
        Font = new Font("Segoe UI Symbol", 12f, FontStyle.Bold);
        TextAlign = ContentAlignment.MiddleCenter;
        UseCompatibleTextRendering = true;
        Cursor = Cursors.Hand;
        TabStop = true;
        _animationFrame = Animate;
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; StartAnimation(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; _pressed = false; StartAnimation(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _pressed = true; UpdateVisual(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        UpdateVisual();
        base.OnMouseUp(e);
    }
    protected override void OnGotFocus(EventArgs e) { StartAnimation(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { StartAnimation(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        base.OnPaint(pevent);
        var g = pevent.Graphics;
        var previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var cx = ClientSize.Width / 2f;
        var cy = ClientSize.Height / 2f + (_pressed ? 1f : 0f);
        using var circle = new SolidBrush(Color.FromArgb(236, 235, 244));
        g.FillEllipse(circle, cx - 9, cy - 9, 18, 18);
        using var check = new Pen(Color.FromArgb(65, 60, 82), 2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        g.DrawLines(check, [new PointF(cx - 5, cy), new PointF(cx - 1.5f, cy + 3), new PointF(cx + 5, cy - 4)]);
        g.SmoothingMode = previous;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateRegion();
        UpdateVisual();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var previous = BackgroundImage;
            BackgroundImage = null;
            previous?.Dispose();
        }
        base.Dispose(disposing);
    }

    private Bitmap RenderButton()
    {
        var bitmap = new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(1, 1 + (_pressed ? 1 : 0), Math.Max(1, Width - 3), Math.Max(1, Height - 3));
        using var path = GlassPanel.Rounded(rect, 7);
        var top = Blend(Color.FromArgb(69, 70, 82), Color.FromArgb(114, 98, 198), _hoverProgress * 0.72f);
        var bottom = Blend(Color.FromArgb(43, 45, 54), Color.FromArgb(75, 65, 133), _hoverProgress * 0.65f);
        using var fill = new LinearGradientBrush(rect, top, bottom, LinearGradientMode.Vertical);
        using var border = new Pen(Blend(Color.FromArgb(88, 91, 105), Color.FromArgb(162, 146, 255), _hoverProgress), 1f);
        g.FillPath(fill, path);
        g.DrawPath(border, path);
        using var shine = new Pen(Color.FromArgb((int)(28 + 30 * _hoverProgress), Color.White), 1f);
        g.DrawLine(shine, rect.Left + 8, rect.Top + 2, rect.Right - 8, rect.Top + 2);
        return bitmap;
    }

    private void UpdateVisual()
    {
        if (IsDisposed || Width < 2 || Height < 2) return;
        var next = RenderButton();
        var previous = BackgroundImage;
        BackgroundImage = next;
        previous?.Dispose();
        Invalidate();
    }

    private void UpdateRegion()
    {
        if (ClientSize.Width < 2 || ClientSize.Height < 2) return;
        using var path = GlassPanel.Rounded(ClientRectangle, 7);
        var next = new Region(path);
        var previous = Region;
        Region = next;
        previous?.Dispose();
    }

    private void StartAnimation()
    {
        _lastAnimationTick = Environment.TickCount64;
        UiAnimationClock.Start(_animationFrame, this);
    }

    private bool Animate()
    {
        if (IsDisposed) return false;
        var now = Environment.TickCount64;
        var elapsed = Math.Clamp(now - _lastAnimationTick, 1, 50);
        _lastAnimationTick = now;
        var target = _hovered || Focused ? 1f : 0f;
        var step = (float)(elapsed / 160d);
        _hoverProgress = target > _hoverProgress
            ? Math.Min(target, _hoverProgress + step)
            : Math.Max(target, _hoverProgress - step);
        UpdateVisual();
        return Math.Abs(_hoverProgress - target) > 0.001f;
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

}

internal static class UiGlyphs
{
    public static void DrawRefresh(Graphics g, Rectangle bounds, Color color, float width = 2f)
    {
        var size = Math.Min(bounds.Width, bounds.Height);
        if (size < 8) return;

        var inner = RectangleF.Inflate(bounds, -1f, -1f);
        PointF P(float x, float y) => new(
            inner.Left + x / 24f * inner.Width,
            inner.Top + y / 24f * inner.Height);

        var previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, width)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        using var clockwise = new GraphicsPath();
        clockwise.StartFigure();
        clockwise.AddBezier(P(21, 12), P(21, 16.97f), P(16.97f, 21), P(12, 21));
        clockwise.AddBezier(P(12, 21), P(9.52f, 21), P(7.27f, 20), P(5.64f, 18.36f));
        clockwise.AddLine(P(5.64f, 18.36f), P(3, 16));
        clockwise.StartFigure();
        clockwise.AddLine(P(3, 21), P(3, 16));
        clockwise.AddLine(P(3, 16), P(8, 16));
        g.DrawPath(pen, clockwise);

        using var counterClockwise = new GraphicsPath();
        counterClockwise.StartFigure();
        counterClockwise.AddBezier(P(3, 12), P(3, 7.03f), P(7.03f, 3), P(12, 3));
        counterClockwise.AddBezier(P(12, 3), P(14.48f, 3), P(16.73f, 4), P(18.36f, 5.64f));
        counterClockwise.AddLine(P(18.36f, 5.64f), P(21, 8));
        counterClockwise.StartFigure();
        counterClockwise.AddLine(P(21, 3), P(21, 8));
        counterClockwise.AddLine(P(21, 8), P(16, 8));
        g.DrawPath(pen, counterClockwise);
        g.SmoothingMode = previous;
    }

    public static void DrawSpinner(Graphics g, Rectangle bounds, Color color)
    {
        var rect = Rectangle.Inflate(bounds, -2, -2);
        var previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(color, 1.9f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var angle = (Environment.TickCount / 4) % 360;
        g.DrawArc(pen, rect, angle, 245);
        g.SmoothingMode = previous;
    }
}

internal enum ToolbarIconKind { Add, Refresh, Menu }

internal enum ServiceHealth { Unknown, Available, Warning, Failed }

internal sealed class ServiceTargetClickedEventArgs(int index, Point location) : EventArgs
{
    public int Index { get; } = index;
    public Point Location { get; } = location;
}

internal sealed class ServiceStatusStrip : Control
{
    private readonly Dictionary<string, ServiceHealth> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Image> _icons = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<ServiceMonitorTarget> _targets = [];
    private int _hoveredIndex = -1;

    public event EventHandler<ServiceTargetClickedEventArgs>? TargetClicked;

    public ServiceStatusStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        MinimumSize = new Size(210, 42);
        Cursor = Cursors.Hand;
    }

    public void SetTargets(IReadOnlyList<ServiceMonitorTarget> targets)
    {
        _targets = targets.Take(3).ToArray();
        Invalidate();
    }

    public void SetStatus(string targetId, ServiceHealth status)
    {
        _statuses[targetId] = status;
        Invalidate();
    }

    public IReadOnlyDictionary<string, string> StatusSnapshot()
    {
        return _statuses.ToDictionary(item => item.Key, item => item.Value.ToString().ToLowerInvariant(), StringComparer.OrdinalIgnoreCase);
    }

    public void ResetStatus(string targetId)
    {
        _statuses.Remove(targetId);
        Invalidate();
    }

    public void ResetIcon(string targetId)
    {
        if (_icons.Remove(targetId, out var icon)) icon.Dispose();
        Invalidate();
    }

    public void SetIcon(string targetId, Image image)
    {
        if (_icons.Remove(targetId, out var previous)) previous.Dispose();
        _icons[targetId] = image;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var index = HitTest(e.Location);
        if (index != _hoveredIndex)
        {
            _hoveredIndex = index;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hoveredIndex = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && HitTest(e.Location) is var index && index >= 0)
            TargetClicked?.Invoke(this, new ServiceTargetClickedEventArgs(index, e.Location));
        base.OnMouseUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var gap = 6;
        var width = Math.Max(1, (ClientSize.Width - gap * 2) / 3);
        using var nameFont = new Font("Segoe UI", ClientSize.Width < 390 ? 7.2f : 8.2f, FontStyle.Bold);
        using var statusFont = new Font("Segoe UI", 7.4f, FontStyle.Bold);

        for (var i = 0; i < _targets.Count; i++)
        {
            var target = _targets[i];
            var left = i * (width + gap);
            var rect = new Rectangle(left, 2, i == _targets.Count - 1 ? ClientSize.Width - left : width, Math.Max(1, ClientSize.Height - 4));
            using var path = GlassPanel.Rounded(Rectangle.Inflate(rect, -1, -1), 7);
            using var fill = new SolidBrush(i == _hoveredIndex ? Color.FromArgb(38, 40, 47) : Color.FromArgb(29, 31, 37));
            using var border = new Pen(i == _hoveredIndex ? Color.FromArgb(100, 88, 152) : Color.FromArgb(61, 64, 73));
            g.FillPath(fill, path);
            g.DrawPath(border, path);

            var health = _statuses.GetValueOrDefault(target.Id);
            var color = health switch
            {
                ServiceHealth.Available => Color.FromArgb(92, 220, 148),
                ServiceHealth.Warning => Color.FromArgb(255, 190, 92),
                ServiceHealth.Failed => Color.FromArgb(255, 116, 116),
                _ => Color.FromArgb(137, 141, 152)
            };
            var status = health switch
            {
                ServiceHealth.Available => "OK",
                ServiceHealth.Warning => "!",
                ServiceHealth.Failed => "!",
                _ => "—"
            };
            var iconRect = new Rectangle(rect.Left + 9, rect.Top + (rect.Height - 18) / 2, 18, 18);
            if (_icons.TryGetValue(target.Id, out var icon))
                g.DrawImage(icon, iconRect);
            else
                DrawGlobe(g, iconRect, Color.FromArgb(155, 159, 170));
            TextRenderer.DrawText(g, target.Name, nameFont,
                new Rectangle(rect.Left + 33, rect.Top, Math.Max(1, rect.Width - 67), rect.Height),
                Color.FromArgb(218, 220, 228), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, status, statusFont,
                new Rectangle(rect.Right - 34, rect.Top, 24, rect.Height), color,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var icon in _icons.Values) icon.Dispose();
            _icons.Clear();
        }
        base.Dispose(disposing);
    }

    private int HitTest(Point point)
    {
        if (_targets.Count == 0 || !ClientRectangle.Contains(point)) return -1;
        var gap = 6;
        var width = Math.Max(1, (ClientSize.Width - gap * 2) / 3);
        for (var i = 0; i < _targets.Count; i++)
        {
            var left = i * (width + gap);
            var rect = new Rectangle(left, 0, i == _targets.Count - 1 ? ClientSize.Width - left : width, ClientSize.Height);
            if (rect.Contains(point)) return i;
        }
        return -1;
    }

    private static void DrawGlobe(Graphics g, Rectangle rect, Color color)
    {
        using var pen = new Pen(color, 1.35f);
        g.DrawEllipse(pen, Rectangle.Inflate(rect, -2, -2));
        g.DrawEllipse(pen, new Rectangle(rect.Left + 6, rect.Top + 2, 6, rect.Height - 4));
        g.DrawLine(pen, rect.Left + 2, rect.Top + rect.Height / 2, rect.Right - 2, rect.Top + rect.Height / 2);
    }
}

internal sealed class ServicePresetList : Control
{
    private const int RowHeight = 44;
    private const int RowGap = 6;
    private readonly Func<bool> _animationFrame;
    private readonly Dictionary<string, float> _hoverProgress = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Image> _icons = new(StringComparer.Ordinal);
    private IReadOnlyList<ServicePreset> _items = [];
    private float _scrollPosition;
    private float _scrollTarget;
    private int _hoveredIndex = -1;
    private int _pressedIndex = -1;
    private bool _draggingThumb;
    private int _thumbGrabOffset;
    private long _lastAnimationTick;

    public event EventHandler<ServicePreset>? PresetSelected;

    public ServicePresetList()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.Selectable, true);
        BackColor = Color.FromArgb(31, 33, 39);
        Cursor = Cursors.Hand;
        TabStop = true;
        _animationFrame = Animate;
    }

    public void SetItems(IEnumerable<ServicePreset> items)
    {
        _items = items.ToArray();
        _scrollTarget = Math.Clamp(_scrollTarget, 0, MaximumScroll());
        _scrollPosition = Math.Clamp(_scrollPosition, 0, MaximumScroll());
        _hoveredIndex = -1;
        Invalidate();
    }

    public void SetIcon(string presetId, Image icon)
    {
        if (_icons.Remove(presetId, out var previous)) previous.Dispose();
        _icons[presetId] = icon;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _scrollTarget = Math.Clamp(_scrollTarget - e.Delta / 120f * 94f, 0, MaximumScroll());
        StartAnimation();
        base.OnMouseWheel(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_draggingThumb)
        {
            var track = ScrollTrack();
            var thumb = ScrollThumb();
            var travel = Math.Max(1, track.Height - thumb.Height);
            var top = Math.Clamp(e.Y - _thumbGrabOffset, track.Top, track.Bottom - thumb.Height);
            _scrollTarget = MaximumScroll() * (top - track.Top) / travel;
            StartAnimation();
            return;
        }

        var index = HitTestRow(e.Location);
        if (index != _hoveredIndex)
        {
            _hoveredIndex = index;
            StartAnimation();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        if (!_draggingThumb)
        {
            _hoveredIndex = -1;
            StartAnimation();
        }
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        var thumb = ScrollThumb();
        if (MaximumScroll() > 0 && thumb.Contains(e.Location))
        {
            _draggingThumb = true;
            _thumbGrabOffset = e.Y - thumb.Top;
            Capture = true;
        }
        else
        {
            _pressedIndex = HitTestRow(e.Location);
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_draggingThumb)
        {
            _draggingThumb = false;
            Capture = false;
        }
        else
        {
            var index = HitTestRow(e.Location);
            if (index >= 0 && index == _pressedIndex) PresetSelected?.Invoke(this, _items[index]);
        }
        _pressedIndex = -1;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_items.Count == 0) return;
        if (e.KeyCode is Keys.Down or Keys.PageDown)
        {
            _scrollTarget = Math.Clamp(_scrollTarget + (e.KeyCode == Keys.Down ? RowHeight : ClientSize.Height), 0, MaximumScroll());
            StartAnimation();
            e.Handled = true;
        }
        else if (e.KeyCode is Keys.Up or Keys.PageUp)
        {
            _scrollTarget = Math.Clamp(_scrollTarget - (e.KeyCode == Keys.Up ? RowHeight : ClientSize.Height), 0, MaximumScroll());
            StartAnimation();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnResize(EventArgs e)
    {
        _scrollTarget = Math.Clamp(_scrollTarget, 0, MaximumScroll());
        _scrollPosition = Math.Clamp(_scrollPosition, 0, MaximumScroll());
        base.OnResize(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using var nameFont = new Font("Segoe UI", 9f, FontStyle.Bold);
        using var domainFont = new Font("Segoe UI", 8.2f);
        var cardWidth = Math.Max(1, ClientSize.Width - 22);

        for (var i = 0; i < _items.Count; i++)
        {
            var top = 4 + i * (RowHeight + RowGap) - (int)Math.Round(_scrollPosition);
            if (top + RowHeight < 0 || top > ClientSize.Height) continue;
            var item = _items[i];
            var hover = _hoverProgress.GetValueOrDefault(item.Id);
            var rect = new Rectangle(4, top, cardWidth, RowHeight);
            using var path = GlassPanel.Rounded(rect, 8);
            var fillColor = Blend(Color.FromArgb(39, 41, 48), Color.FromArgb(55, 53, 68), hover);
            var borderColor = Blend(Color.FromArgb(61, 64, 74), Color.FromArgb(115, 99, 190), hover);
            using var fill = new SolidBrush(fillColor);
            using var border = new Pen(borderColor, 1f);
            g.FillPath(fill, path);
            g.DrawPath(border, path);

            var iconRect = new Rectangle(rect.Left + 11, rect.Top + 13, 18, 18);
            if (_icons.TryGetValue(item.Id, out var icon))
                g.DrawImage(icon, iconRect);
            else
                DrawGlobe(g, iconRect, Blend(Color.FromArgb(151, 155, 168), Color.FromArgb(178, 166, 255), hover));
            TextRenderer.DrawText(g, item.Name, nameFont,
                new Rectangle(rect.Left + 38, rect.Top, 118, rect.Height),
                Color.FromArgb(235, 236, 241), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            var host = new Uri(item.Url).Host;
            TextRenderer.DrawText(g, host, domainFont,
                new Rectangle(rect.Left + 157, rect.Top, Math.Max(1, rect.Width - 169), rect.Height),
                Color.FromArgb(172, 175, 186), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        if (MaximumScroll() > 0)
        {
            var track = ScrollTrack();
            var thumb = ScrollThumb();
            using var trackPath = GlassPanel.Rounded(track, 2);
            using var thumbPath = GlassPanel.Rounded(thumb, 3);
            using var trackBrush = new SolidBrush(Color.FromArgb(43, 45, 52));
            using var thumbBrush = new SolidBrush(_draggingThumb || _hoveredIndex >= 0
                ? Color.FromArgb(132, 116, 235)
                : Color.FromArgb(91, 85, 126));
            g.FillPath(trackBrush, trackPath);
            g.FillPath(thumbBrush, thumbPath);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var icon in _icons.Values) icon.Dispose();
            _icons.Clear();
        }
        base.Dispose(disposing);
    }

    private void StartAnimation()
    {
        _lastAnimationTick = Environment.TickCount64;
        UiAnimationClock.Start(_animationFrame, this);
    }

    private bool Animate()
    {
        if (IsDisposed) return false;
        var now = Environment.TickCount64;
        var elapsed = Math.Clamp(now - _lastAnimationTick, 1, 50);
        _lastAnimationTick = now;
        var scrollBlend = 1f - MathF.Exp(-(float)elapsed / 65f);
        _scrollPosition += (_scrollTarget - _scrollPosition) * scrollBlend;
        if (Math.Abs(_scrollTarget - _scrollPosition) < 0.15f) _scrollPosition = _scrollTarget;

        var active = Math.Abs(_scrollTarget - _scrollPosition) > 0.01f;
        foreach (var item in _items)
        {
            var current = _hoverProgress.GetValueOrDefault(item.Id);
            var target = _hoveredIndex >= 0 && ReferenceEquals(_items[_hoveredIndex], item) ? 1f : 0f;
            var step = (float)(elapsed / 150d);
            var next = target > current ? Math.Min(target, current + step) : Math.Max(target, current - step);
            _hoverProgress[item.Id] = next;
            active |= Math.Abs(next - target) > 0.001f;
        }
        Invalidate();
        return active;
    }

    private int HitTestRow(Point point)
    {
        if (point.X < 4 || point.X > ClientSize.Width - 18) return -1;
        var contentY = point.Y + _scrollPosition - 4;
        if (contentY < 0) return -1;
        var index = (int)(contentY / (RowHeight + RowGap));
        if (index < 0 || index >= _items.Count) return -1;
        return contentY - index * (RowHeight + RowGap) <= RowHeight ? index : -1;
    }

    private float MaximumScroll() => Math.Max(0, 4 + _items.Count * (RowHeight + RowGap) - RowGap - ClientSize.Height);

    private Rectangle ScrollTrack() => new(Math.Max(0, ClientSize.Width - 9), 7, 4, Math.Max(1, ClientSize.Height - 14));

    private Rectangle ScrollThumb()
    {
        var track = ScrollTrack();
        var content = Math.Max(ClientSize.Height, 4 + _items.Count * (RowHeight + RowGap) - RowGap);
        var height = Math.Max(28, (int)Math.Round(track.Height * ClientSize.Height / (double)content));
        var travel = Math.Max(0, track.Height - height);
        var top = track.Top + (MaximumScroll() <= 0 ? 0 : (int)Math.Round(travel * _scrollPosition / MaximumScroll()));
        return new Rectangle(track.Left - 1, top, 6, height);
    }

    private static void DrawGlobe(Graphics g, Rectangle rect, Color color)
    {
        using var pen = new Pen(color, 1.25f);
        g.DrawEllipse(pen, Rectangle.Inflate(rect, -2, -2));
        g.DrawEllipse(pen, new Rectangle(rect.Left + 6, rect.Top + 2, 6, rect.Height - 4));
        g.DrawLine(pen, rect.Left + 2, rect.Top + rect.Height / 2, rect.Right - 2, rect.Top + rect.Height / 2);
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
}

internal sealed class ToolbarIconButton : Control
{
    private readonly Func<bool> _animationFrame;
    private bool _hovered;
    private bool _pressed;
    private float _hoverProgress;

    public ToolbarIconKind Kind { get; set; }

    public ToolbarIconButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = true;
        _animationFrame = Animate;
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; UiAnimationClock.Start(_animationFrame); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; _pressed = false; UiAnimationClock.Start(_animationFrame); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void Dispose(bool disposing) { if (disposing) UiAnimationClock.Stop(_animationFrame); base.Dispose(disposing); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var visibleBounds = Rectangle.Intersect(ClientRectangle, e.ClipRectangle);
        if (visibleBounds.Width < 4 || visibleBounds.Height < 4) visibleBounds = ClientRectangle;
        var rect = Rectangle.Inflate(visibleBounds, -1, -1);
        var alpha = (int)(42 * _hoverProgress) + (_pressed ? 24 : 0);
        using var path = GlassPanel.Rounded(rect, 8);
        using var fill = new SolidBrush(Color.FromArgb(alpha, 150, 137, 255));
        using var border = new Pen(Color.FromArgb((int)(48 * _hoverProgress), 196, 188, 255));
        g.FillPath(fill, path);
        g.DrawPath(border, path);
        using var pen = new Pen(_hovered ? Color.White : Color.FromArgb(183, 178, 210), 1.8f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        var cx = visibleBounds.Left + visibleBounds.Width / 2f;
        var cy = visibleBounds.Top + visibleBounds.Height / 2f + (_pressed ? 1 : 0);
        switch (Kind)
        {
            case ToolbarIconKind.Add:
                g.DrawLine(pen, cx - 6, cy, cx + 6, cy);
                g.DrawLine(pen, cx, cy - 6, cx, cy + 6);
                break;
            case ToolbarIconKind.Refresh:
                UiGlyphs.DrawRefresh(g, Rectangle.Round(new RectangleF(cx - 10, cy - 10, 20, 20)), pen.Color, 2f);
                break;
            case ToolbarIconKind.Menu:
                using (var dots = new SolidBrush(pen.Color))
                {
                    g.FillEllipse(dots, cx - 7, cy - 1.5f, 3, 3);
                    g.FillEllipse(dots, cx - 1.5f, cy - 1.5f, 3, 3);
                    g.FillEllipse(dots, cx + 4, cy - 1.5f, 3, 3);
                }
                break;
        }
    }

    private bool Animate()
    {
        var target = _hovered ? 1f : 0f;
        _hoverProgress += (target - _hoverProgress) * 0.24f;
        if (Math.Abs(target - _hoverProgress) < 0.01f) _hoverProgress = target;
        Invalidate();
        return Math.Abs(target - _hoverProgress) >= 0.01f;
    }
}

internal sealed class ConnectionStateIndicator : Control
{
    private readonly Func<bool> _animationFrame;
    private ConnectionVisualState _state;
    private float _progress;
    private float _failureProgress;

    public ConnectionVisualState State
    {
        get => _state;
        set { if (_state == value) return; _state = value; UiAnimationClock.Start(_animationFrame); }
    }

    public ConnectionStateIndicator()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        _animationFrame = Animate;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var idle = Color.FromArgb(105, 109, 120);
        var active = _state is ConnectionVisualState.Connecting or ConnectionVisualState.Disconnecting
            ? Color.FromArgb(158, 143, 255)
            : Color.FromArgb(92, 220, 148);
        var color = Blend(Blend(idle, active, _progress), Color.FromArgb(255, 94, 108), _failureProgress);
        var size = 9;
        var x = (Width - size) / 2;
        var y = (Height - size) / 2;
        using var glow = new SolidBrush(Color.FromArgb((int)(35 + 45 * _progress), color));
        using var dot = new SolidBrush(color);
        e.Graphics.FillEllipse(glow, x - 4, y - 4, size + 8, size + 8);
        e.Graphics.FillEllipse(dot, x, y, size, size);
    }

    protected override void Dispose(bool disposing) { if (disposing) UiAnimationClock.Stop(_animationFrame); base.Dispose(disposing); }
    private bool Animate()
    {
        var target = _state == ConnectionVisualState.Disconnected ? 0f : 1f;
        var failureTarget = _state == ConnectionVisualState.Failed ? 1f : 0f;
        _progress += (target - _progress) * 0.2f;
        _failureProgress += (failureTarget - _failureProgress) * 0.25f;
        if (Math.Abs(target - _progress) < 0.01f) _progress = target;
        if (Math.Abs(failureTarget - _failureProgress) < 0.01f) _failureProgress = failureTarget;
        Invalidate();
        return Math.Abs(target - _progress) >= 0.01f || Math.Abs(failureTarget - _failureProgress) >= 0.01f;
    }
    private static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
}

internal sealed class ServerIdentityControl : Control
{
    private ProxyProfile? _profile;

    public ServerIdentityControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    public void SetProfile(ProxyProfile? profile) { _profile = profile; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var profile = _profile;
        DrawFlag(g, profile?.CountryCode, new Rectangle(2, 25, 36, 25));
        var name = profile?.Name ?? "Сервер не выбран";
        var detail = profile is null ? "Добавьте подписку или сервер" : $"{profile.Server}:{profile.Port}";
        var protocol = profile is null ? "" : ProtocolText(profile);
        TextRenderer.DrawText(g, name, new Font("Segoe UI", 15f, FontStyle.Bold),
            new Rectangle(52, 8, Math.Max(1, Width - 56), 32), Color.White,
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, detail, new Font("Segoe UI", 9.2f),
            new Rectangle(52, 39, Math.Max(1, Width - 56), 21), Color.FromArgb(174, 178, 190),
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, protocol, new Font("Segoe UI", 8.2f, FontStyle.Bold),
            new Rectangle(52, 61, Math.Max(1, Width - 56), 18), Color.FromArgb(139, 126, 230),
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
    }

    private static string ProtocolText(ProxyProfile profile)
    {
        var transport = string.IsNullOrWhiteSpace(profile.Network) ? "" : $" / {profile.Network.ToUpperInvariant()}";
        var security = profile.RealityEnabled ? " / REALITY" : profile.TlsEnabled ? " / TLS" : "";
        return $"{profile.Protocol.ToString().ToUpperInvariant()}{transport}{security}";
    }

    private static void DrawFlag(Graphics g, string? code, Rectangle bounds)
    {
        using var clip = GlassPanel.Rounded(bounds, 5);
        var state = g.Save();
        g.SetClip(clip);
        code = code?.ToUpperInvariant();
        if (code is "SE" or "FI")
        {
            g.FillRectangle(code == "SE" ? Brushes.RoyalBlue : Brushes.White, bounds);
            using var cross = new SolidBrush(code == "SE" ? Color.Gold : Color.FromArgb(0, 53, 128));
            g.FillRectangle(cross, bounds.Left + 10, bounds.Top, 5, bounds.Height);
            g.FillRectangle(cross, bounds.Left, bounds.Top + 10, bounds.Width, 5);
        }
        else if (code is "NL" or "RU" or "DE")
        {
            var colors = code switch
            {
                "NL" => new[] { Color.Firebrick, Color.White, Color.RoyalBlue },
                "RU" => new[] { Color.White, Color.RoyalBlue, Color.Firebrick },
                "DE" => new[] { Color.Black, Color.Firebrick, Color.Gold },
                _ => new[] { Color.Black, Color.Firebrick, Color.Gold }
            };
            for (var i = 0; i < 3; i++) using (var brush = new SolidBrush(colors[i])) g.FillRectangle(brush, bounds.Left, bounds.Top + bounds.Height * i / 3, bounds.Width, bounds.Height / 3 + 1);
        }
        else if (code is "PL" or "UA")
        {
            using var top = new SolidBrush(code == "PL" ? Color.White : Color.RoyalBlue);
            using var bottom = new SolidBrush(code == "PL" ? Color.Crimson : Color.Gold);
            g.FillRectangle(top, bounds.Left, bounds.Top, bounds.Width, bounds.Height / 2);
            g.FillRectangle(bottom, bounds.Left, bounds.Top + bounds.Height / 2, bounds.Width, bounds.Height - bounds.Height / 2);
        }
        else if (code is "FR" or "MD")
        {
            var colors = code == "FR"
                ? new[] { Color.RoyalBlue, Color.White, Color.Firebrick }
                : new[] { Color.RoyalBlue, Color.Gold, Color.Firebrick };
            for (var i = 0; i < 3; i++) using (var brush = new SolidBrush(colors[i])) g.FillRectangle(brush, bounds.Left + bounds.Width * i / 3, bounds.Top, bounds.Width / 3 + 1, bounds.Height);
        }
        else if (code == "US")
        {
            for (var i = 0; i < 7; i++)
            {
                using var stripe = new SolidBrush(i % 2 == 0 ? Color.Firebrick : Color.White);
                g.FillRectangle(stripe, bounds.Left, bounds.Top + bounds.Height * i / 7, bounds.Width, bounds.Height / 7 + 1);
            }
            using var field = new SolidBrush(Color.Navy);
            g.FillRectangle(field, bounds.Left, bounds.Top, bounds.Width / 2, bounds.Height / 2);
        }
        else
        {
            using var fallback = new SolidBrush(Color.FromArgb(48, 51, 59));
            g.FillRectangle(fallback, bounds);
            TextRenderer.DrawText(g, string.IsNullOrWhiteSpace(code) ? "•" : code, new Font("Segoe UI", 7.5f, FontStyle.Bold), bounds,
                Color.FromArgb(220, 222, 230), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        g.Restore(state);
        using var border = new Pen(Color.FromArgb(84, 87, 98));
        g.DrawPath(border, clip);
    }
}
