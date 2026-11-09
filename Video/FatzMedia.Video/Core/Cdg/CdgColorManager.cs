using System.Drawing;

namespace FatzMedia.Video.Core.Cdg;

/// <summary>
/// Manages CDG color table, lookup table, and background color operations.
/// Handles all color-related optimizations and calculations.
/// </summary>
sealed class CdgColorManager
{
    #region Private Fields

    readonly Color[] _colorTable = new Color[CdgConstants.ColorCount];
    readonly int[] _colorArgbLut = new int[CdgConstants.ColorCount];

    int _borderColorIndex = -1;
    byte _backgroundOpacity = 255; // 0..255
    bool _lutNeedsUpdate = true;
    bool _backgroundWashNeedsUpdate = true;
    uint _backgroundWashArgb;

    #endregion

    #region Public Properties

    /// <summary>
    /// Gets the color lookup table for fast pixel rendering.
    /// </summary>
    public ReadOnlySpan<int> ColorLut => _colorArgbLut.AsSpan();

    public int BackgroundColorIndex { get; private set; } = -1;

    /// <summary>
    /// Cached full-viewport background wash ARGB.  Recomputed only when background color,
    /// background override, color table, or presentation opacity changes.
    /// </summary>
    public uint BackgroundWashArgb
    {
        get
        {
            _ = UpdateBackgroundWashIfNeeded();
            return _backgroundWashArgb;
        }
    }

    /// <summary>
    /// Refreshes the cached wash color if invalidated. Returns true only when the
    /// ARGB value actually changed.
    /// </summary>
    public bool UpdateBackgroundWashIfNeeded()
    {
        if (!_backgroundWashNeedsUpdate)
        {
            return false;
        }

        var old = _backgroundWashArgb;
        _backgroundWashArgb = CalculateBackgroundWashArgb();
        _backgroundWashNeedsUpdate = false;
        return old != _backgroundWashArgb;
    }

    /// <summary>
    /// Gets or sets the background opacity (0-100 scale).
    /// </summary>
    public byte BackgroundOpacity
    {
        // Rounds like the setter, so every 0-100 value reads back as set.
        get => (byte)Math.Round(_backgroundOpacity * 100.0 / 255.0);
        set
        {
            var v = (byte)Math.Clamp((int)value, 0, 100);
            // Map 0..100 exactly to 0..255. The previous v * 2.55 conversion truncated
            // 100% to 254, leaving the CDG background subtly translucent over video.
            var scaled = v >= 100 ? (byte)255 : (byte)Math.Round(v * 255.0 / 100.0);
            if (scaled == _backgroundOpacity) return;

            _backgroundOpacity = scaled;
            InvalidateColorState(invalidateWash: true);
        }
    }

    /// <summary>
    /// Gets or sets the background color override (ARGB format).
    /// </summary>
    public uint? BackgroundOverrideArgb
    {
        get; set
        {
            if (field == value) return;

            field = value;
            InvalidateColorState(invalidateWash: true);
        }
    }

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new CDG color manager with default colors.
    /// </summary>
    public CdgColorManager() => Reset();

    #endregion

    #region Public Methods

    /// <summary>
    /// Resets color table to default CDG palette.
    /// </summary>
    public void Reset()
    {
        // Copy default palette
        for (var i = 0; i < CdgConstants.ColorCount; i++)
            _colorTable[i] = CdgConstants.DefaultColorPalette[i];

        _borderColorIndex = -1;
        BackgroundColorIndex = -1;
        InvalidateColorState(invalidateWash: true);

        _ = UpdateLutIfNeeded();
    }

    /// <summary>
    /// Updates the lookup table if changes have been made.
    /// This is called automatically but can be forced for immediate updates.
    /// </summary>
    public bool UpdateLutIfNeeded()
    {
        if (!_lutNeedsUpdate) return false;

        for (var i = 0; i < CdgConstants.ColorCount; i++)
        {
            var isBg = i == 0 || i == _borderColorIndex || i == BackgroundColorIndex;
            var baseColor = _colorTable[i];

            // Apply background override if applicable
            if (BackgroundOverrideArgb.HasValue && isBg)
                baseColor = FromArgb(BackgroundOverrideArgb.Value);

            // Calculate final alpha
            var finalA = baseColor.A;
            if (isBg)
            {
                var sourceA = BackgroundOverrideArgb.HasValue
                    ? FromArgb(BackgroundOverrideArgb.Value).A
                    : (byte)255;
                finalA = (byte)Math.Clamp(sourceA * _backgroundOpacity / 255, 0, 255);
            }

            // Pack into ARGB integer
            _colorArgbLut[i] = (finalA << 24) | (baseColor.R << 16) | (baseColor.G << 8) | baseColor.B;
        }

        _lutNeedsUpdate = false;
        return true;
    }

    /// <summary>
    /// Sets the border color index.
    /// </summary>
    /// <param name="colorIndex">Color index (0-15)</param>
    public void SetBorderColor(int colorIndex)
    {
        if (_borderColorIndex == colorIndex) return;

        _borderColorIndex = colorIndex;
        InvalidateColorState(invalidateWash: false);
    }

    /// <summary>
    /// Sets the background color index.
    /// </summary>
    /// <param name="colorIndex">Color index (0-15)</param>
    public void SetBackgroundColor(int colorIndex)
    {
        if (BackgroundColorIndex == colorIndex) return;

        BackgroundColorIndex = colorIndex;
        InvalidateColorState(invalidateWash: true);
    }

    /// <summary>
    /// Loads colors into the lower half of the color table (colors 0-7).
    /// </summary>
    /// <param name="colorData">8 colors worth of data (16 bytes)</param>
    public void LoadColorTableLower(ReadOnlySpan<byte> colorData) => LoadColorTableRange(colorData, 0);

    /// <summary>
    /// Loads colors into the upper half of the color table (colors 8-15).
    /// </summary>
    /// <param name="colorData">8 colors worth of data (16 bytes)</param>
    public void LoadColorTableUpper(ReadOnlySpan<byte> colorData) => LoadColorTableRange(colorData, 8);

    /// <summary>
    /// Defines a color as transparent.
    /// </summary>
    /// <param name="colorIndex">Color index to make transparent</param>
    public void DefineTransparentColor(int colorIndex)
    {
        if (colorIndex is < 0 or >= CdgConstants.ColorCount) return;
        // Host-controlled background opacity handles visual transparency.
        // CDG transparent-color packets are intentionally not mapped to WPF alpha here.
        InvalidateColorState(invalidateWash: false);
    }

    #endregion

    /// <summary>
    /// Captures the state CDG packets change (the color table and the border and background
    /// indices), not the host's presentation settings (opacity, background override).
    /// </summary>
    internal CdgColorReplayState CaptureReplayState() =>
        new([.. _colorTable], _borderColorIndex, BackgroundColorIndex);

    /// <summary>Restores state from <see cref="CaptureReplayState"/>, keeping the host's presentation settings.</summary>
    internal void RestoreReplayState(CdgColorReplayState state)
    {
        state.ColorTable.CopyTo(_colorTable, 0);
        _borderColorIndex = state.BorderColorIndex;
        BackgroundColorIndex = state.BackgroundColorIndex;
        InvalidateColorState(invalidateWash: true);
    }

    #region Private Methods

    void LoadColorTableRange(ReadOnlySpan<byte> colorData, int offset)
    {
        if (colorData.Length < 16) return;

        for (var i = 0; i < 8; i++)
        {
            var high = colorData[2 * i];
            var low = colorData[(2 * i) + 1];

            // Extract RGB components (CDG color format)
            var r = (high & CdgConstants.RedColorMask) >> 2;
            var g = ((high & CdgConstants.GreenColorMask) << 2) | ((low & CdgConstants.GreenLowbyteColorMask) >> 4);
            var b = low & CdgConstants.BlueColorMask;

            // Scale to full 8-bit range
            r *= CdgConstants.ColorValueMultiplier;
            g *= CdgConstants.ColorValueMultiplier;
            b *= CdgConstants.ColorValueMultiplier;

            _colorTable[i + offset] = Color.FromArgb(255, (byte)r, (byte)g, (byte)b);
        }

        InvalidateColorState(invalidateWash: true);
    }

    void InvalidateColorState(bool invalidateWash)
    {
        _lutNeedsUpdate = true;
        if (invalidateWash)
        {
            _backgroundWashNeedsUpdate = true;
        }
    }

    uint CalculateBackgroundWashArgb()
    {
        // Until a CDG memory-preset establishes the background color, the CDG plane is transparent so
        // any underlying background (sidecar image/video) shows through instead of the opaque default
        // palette fill. A host-supplied background override still forces a visible wash.
        if (BackgroundColorIndex < 0 && !BackgroundOverrideArgb.HasValue)
        {
            return 0;
        }

        var bgIndex = BackgroundColorIndex >= 0 ? BackgroundColorIndex : 0;
        var baseColor = _colorTable[bgIndex];

        if (BackgroundOverrideArgb.HasValue && (bgIndex == 0 || bgIndex == BackgroundColorIndex))
        {
            baseColor = FromArgb(BackgroundOverrideArgb.Value);
        }

        var sourceA = BackgroundOverrideArgb.HasValue
            ? FromArgb(BackgroundOverrideArgb.Value).A
            : (byte)255;
        var finalA = (byte)Math.Clamp(sourceA * _backgroundOpacity / 255, 0, 255);

        return (uint)((finalA << 24) | (baseColor.R << 16) | (baseColor.G << 8) | baseColor.B);
    }

    static Color FromArgb(uint argb) =>
        Color.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF),
            (byte)(argb & 0xFF));

    #endregion
}

/// <summary>The packet-driven part of <see cref="CdgColorManager"/> (Stage 022A slice 4 replay checkpoints).</summary>
sealed record CdgColorReplayState(Color[] ColorTable, int BorderColorIndex, int BackgroundColorIndex);
