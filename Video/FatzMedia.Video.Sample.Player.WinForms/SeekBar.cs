using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace FatzMedia.Video.Sample.Player.WinForms;

/// <summary>
/// The player's seek bar, drawn by the player (dark, like the WPF and Avalonia seek bars). The
/// native track bar it replaces paged toward a press off the thumb and flashed a light rectangle at
/// its left end when it repainted, while playing as well as while dragging (owner's 046A smoke).
/// </summary>
/// <remarks>
/// A press jumps to the pointer and a drag follows it, wherever the press lands; the arrow keys step
/// by <see cref="SmallChange"/>, Page Up / Page Down by <see cref="LargeChange"/>, Home / End go to
/// the ends. The value runs from 0 to <see cref="Maximum"/>.
/// </remarks>
sealed class SeekBar : Control
{
    // The WPF player's accent.
    static readonly Color Accent = Color.FromArgb(0x3E, 0xA6, 0xFF);
    int _maximum = 1;
    int _value;
    bool _pressed;
    // Focus shows only after the keyboard is used (Tab or a key), never for a mouse press or drag.
    bool _keyboardFocus;

    public SeekBar()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable,
            true);
        TabStop = true;
    }

    /// <summary>Raised when <see cref="Value"/> changes: a press, a drag, a key, or the code.</summary>
    public event EventHandler? ValueChanged;

    /// <summary>Raised on the left button press, before the value jumps to the pointer.</summary>
    public event EventHandler? PressStarting;

    /// <summary>Raised when the press ends: the button is released or the mouse capture is lost.</summary>
    public event EventHandler? PressEnded;

    /// <summary>Gets or sets the largest value (at least 1); a smaller maximum clamps <see cref="Value"/>.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Maximum
    {
        get => _maximum;
        set
        {
            _maximum = Math.Max(1, value);
            if (_value > _maximum)
                SetValue(_maximum);
            Invalidate();
        }
    }

    /// <summary>Gets or sets the thumb's position, 0 to <see cref="Maximum"/>.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set => SetValue(value);
    }

    /// <summary>Gets or sets the step of the arrow keys.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SmallChange { get; set; } = 1;

    /// <summary>Gets or sets the step of Page Up and Page Down.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int LargeChange { get; set; } = 10;

    float ThumbRadius => 7f * DeviceDpi / 96f;

    void SetValue(int value)
    {
        value = Math.Clamp(value, 0, _maximum);
        if (value == _value)
            return;

        _value = value;
        Invalidate();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.Clear(BackColor);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var radius = ThumbRadius;
        var trackHeight = Math.Max(2f, 4f * DeviceDpi / 96f);
        var left = radius;
        var width = Math.Max(1f, ClientSize.Width - (2 * radius));
        var centreY = ClientSize.Height / 2f;
        var thumbX = left + (width * _value / _maximum);
        var fill = Enabled ? Accent : SystemColors.GrayText;

        using (var track = new SolidBrush(SystemColors.ControlDark))
            graphics.FillRectangle(track, left, centreY - (trackHeight / 2), width, trackHeight);
        using (var played = new SolidBrush(fill))
            graphics.FillRectangle(played, left, centreY - (trackHeight / 2), thumbX - left, trackHeight);
        using (var thumb = new SolidBrush(Enabled ? SystemColors.ControlText : SystemColors.GrayText))
            graphics.FillEllipse(thumb, thumbX - radius, centreY - radius, 2 * radius, 2 * radius);

        if (Focused && _keyboardFocus)
        {
            using var ring = new Pen(Accent, Math.Max(1f, 2f * DeviceDpi / 96f));
            var ringRadius = radius + (3f * DeviceDpi / 96f);
            graphics.DrawEllipse(ring, thumbX - ringRadius, centreY - ringRadius, 2 * ringRadius, 2 * ringRadius);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !Enabled)
            return;

        _keyboardFocus = false;
        _ = Focus();
        PressStarting?.Invoke(this, EventArgs.Empty);
        _pressed = true;
        Capture = true;
        MoveTo(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pressed)
            MoveTo(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
            EndPress();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        // Alt+Tab or a dialog took the mouse: the press ends where it is.
        if (!Capture)
            EndPress();
        base.OnMouseCaptureChanged(e);
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || !Enabled)
            return;

        int? target = e.KeyCode switch
        {
            Keys.Left or Keys.Down => _value - SmallChange,
            Keys.Right or Keys.Up => _value + SmallChange,
            Keys.PageDown => _value - LargeChange,
            Keys.PageUp => _value + LargeChange,
            Keys.Home => 0,
            Keys.End => _maximum,
            _ => null,
        };
        if (target is { } value)
        {
            _keyboardFocus = true;
            SetValue(value);
            Invalidate();
            e.Handled = true;
        }
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        // Focus without a button down came from the keyboard (Tab).
        if (MouseButtons == MouseButtons.None)
            _keyboardFocus = true;
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        if (!Enabled)
            EndPress();
        Invalidate();
    }

    void EndPress()
    {
        if (!_pressed)
            return;

        _pressed = false;
        Capture = false;
        PressEnded?.Invoke(this, EventArgs.Empty);
    }

    void MoveTo(int x)
    {
        // The thumb's centre travels the bar minus a thumb radius at each end.
        var radius = ThumbRadius;
        var fraction = Math.Clamp((x - radius) / Math.Max(1f, ClientSize.Width - (2 * radius)), 0f, 1f);
        SetValue((int)Math.Round(fraction * _maximum));
    }
}
