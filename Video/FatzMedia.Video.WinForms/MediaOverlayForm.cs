namespace FatzMedia.Video.WinForms;

/// <summary>
/// The layered window that shows a <see cref="MediaSurface.OverlayContent"/> above the video
/// (Stage 046A, like the WPF adapter's <c>MediaOverlayWindow</c>): a borderless tool window owned
/// by the surface's form, so it stays above that form, minimizes with it and never shows in the
/// taskbar. Its background is a colour key, so only the overlay's own pixels show; it is
/// click-through (<c>WS_EX_TRANSPARENT</c>) unless <see cref="MediaSurface.OverlayHitTestVisible"/>
/// is set. It never takes the focus when shown.
/// </summary>
sealed class MediaOverlayForm : Form
{
    /// <summary>
    /// The colour key: a near-black that overlay content rarely uses, so anti-aliased edges blend
    /// towards a dark outline over video rather than a bright fringe.
    /// </summary>
    internal static readonly Color TransparencyKeyColor = Color.FromArgb(1, 0, 1);

    const int WsExTransparent = 0x00000020;
    const int WsExToolWindow = 0x00000080;
    const int WsExNoActivate = 0x08000000;
    const int WmMouseActivate = 0x0021;
    const int MaNoActivate = 3;

    public MediaOverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        ControlBox = false;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.None;
        MinimumSize = Size.Empty;
        Text = string.Empty;
        BackColor = TransparencyKeyColor;
        TransparencyKey = TransparencyKeyColor;
    }

    /// <summary>Gets whether the overlay takes mouse input (tests).</summary>
    internal bool IsHitTestVisible { get; private set; }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolWindow;
            if (!IsHitTestVisible)
                parameters.ExStyle |= WsExTransparent | WsExNoActivate;
            return parameters;
        }
    }

    /// <summary>Hosts <paramref name="content"/> (or nothing) without disposing what it replaces.</summary>
    public void SetContent(Control? content)
    {
        if (content is not null && Controls.Contains(content) && Controls.Count == 1)
            return;

        Controls.Clear();
        if (content is not null)
            Controls.Add(content);
    }

    public void SetHitTestVisible(bool value)
    {
        if (IsHitTestVisible == value)
            return;

        IsHitTestVisible = value;
        if (IsHandleCreated)
            UpdateStyles();
    }

    protected override void WndProc(ref Message m)
    {
        // A click-through overlay never activates (it has no input); one that takes input does
        // not steal activation from its owner either when clicked.
        if (m.Msg == WmMouseActivate)
        {
            m.Result = MaNoActivate;
            return;
        }

        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        // The content belongs to the host: an owner that closes disposes its owned forms, and a
        // form disposes its controls, so the content is taken out first.
        if (disposing)
            Controls.Clear();

        base.Dispose(disposing);
    }
}
