using System;
using System.Diagnostics;
using System.Drawing;

namespace FatzBombMedia.MediaControl.Core.Cdg;

/// <summary>
/// Manages CDG color table, lookup table, and background color operations.
/// Handles all color-related optimizations and calculations.
/// </summary>
internal sealed class CdgColorManager
{
    #region Private Fields
    
    private readonly Color[] _colorTable = new Color[CdgConstants.ColorCount];
    private readonly int[] _colorArgbLut = new int[CdgConstants.ColorCount];
    
    private int _borderColorIndex = -1;
    private int _backgroundColorIndex = -1;
    private byte _backgroundOpacity = 255; // 0..255
    private uint? _backgroundOverrideArgb;
    private bool _lutNeedsUpdate = true;
    
    #endregion

    #region Public Properties
    
    /// <summary>
    /// Gets the color lookup table for fast pixel rendering.
    /// </summary>
    public ReadOnlySpan<int> ColorLut => _colorArgbLut.AsSpan();

    public int CurrentBackgroundArgb
    {
        get
        {
            UpdateLutIfNeeded();
            var backgroundIndex = _backgroundColorIndex >= 0 ? _backgroundColorIndex : 0;
            return _colorArgbLut[backgroundIndex];
        }
    }

    public int BackgroundColorIndex => _backgroundColorIndex;

    /// <summary>
    /// The full-opacity background tint ARGB (before transparency is baked into the bitmap).
    /// When CDG is active this color is drawn as a full-rectangle tint pass behind the
    /// CDG graphics so the native-aspect centered CDG frame blends over a matching background.
    /// </summary>
    public uint TintArgb
    {
        get
        {
            UpdateLutIfNeeded();
            var bgIndex = _backgroundColorIndex >= 0 ? _backgroundColorIndex : 0;
            var baseColor = _colorTable[bgIndex];
            if (_backgroundOverrideArgb.HasValue && (bgIndex == 0 || bgIndex == _backgroundColorIndex))
                baseColor = FromArgb(_backgroundOverrideArgb.Value);

            var sourceA = _backgroundOverrideArgb.HasValue
                ? FromArgb(_backgroundOverrideArgb.Value).A
                : (byte)255;
            var finalA = (byte)Math.Clamp((sourceA * _backgroundOpacity) / 255, 0, 255);

            var result = (uint)((finalA << 24) | (baseColor.R << 16) | (baseColor.G << 8) | baseColor.B);
            Debug.WriteLine($"[CDG-TINT] TintArgb: bgIdx={bgIndex} override={_backgroundOverrideArgb.HasValue} opacity={_backgroundOpacity} sourceA={sourceA} finalA={finalA} R={baseColor.R} G={baseColor.G} B={baseColor.B} → 0x{result:X8}");
            return result;
        }
    }
    
    /// <summary>
    /// Gets or sets the background opacity (0-100 scale).
    /// </summary>
    public byte BackgroundOpacity
    {
        get => (byte)(_backgroundOpacity / 2.55);
        set
        {
            var v = (byte)Math.Clamp((int)value, 0, 100);
            // Map 0..100 exactly to 0..255. The previous v * 2.55 conversion truncated
            // 100% to 254, leaving the CDG background subtly translucent over video.
            var scaled = v >= 100 ? (byte)255 : (byte)Math.Round(v * 255.0 / 100.0);
            if (scaled == _backgroundOpacity) return;
            
            _backgroundOpacity = scaled;
            _lutNeedsUpdate = true;
        }
    }
    
    /// <summary>
    /// Gets or sets the background color override (ARGB format).
    /// </summary>
    public uint? BackgroundOverrideArgb
    {
        get => _backgroundOverrideArgb;
        set
        {
            if (_backgroundOverrideArgb == value) return;
            
            _backgroundOverrideArgb = value;
            _lutNeedsUpdate = true;
        }
    }

    #endregion

    #region Constructor
    
    /// <summary>
    /// Initializes a new CDG color manager with default colors.
    /// </summary>
    public CdgColorManager()
    {
        Reset();
    }
    
    #endregion

    #region Public Methods
    
    /// <summary>
    /// Resets color table to default CDG palette.
    /// </summary>
    public void Reset()
    {
        // Copy default palette
        for (int i = 0; i < CdgConstants.ColorCount; i++)
        {
            _colorTable[i] = CdgConstants.DefaultColorPalette[i];
        }
        
        _borderColorIndex = -1;
        _backgroundColorIndex = -1;
        _lutNeedsUpdate = true;
        
        UpdateLutIfNeeded();
    }
    
    /// <summary>
    /// Updates the lookup table if changes have been made.
    /// This is called automatically but can be forced for immediate updates.
    /// </summary>
    public void UpdateLutIfNeeded()
    {
        if (!_lutNeedsUpdate) return;
        
        for (int i = 0; i < CdgConstants.ColorCount; i++)
        {
            bool isBg = i == 0 || i == _borderColorIndex || i == _backgroundColorIndex;
            var baseColor = _colorTable[i];
            
            // Apply background override if applicable
            if (_backgroundOverrideArgb.HasValue && isBg)
                baseColor = FromArgb(_backgroundOverrideArgb.Value);
            
            // Calculate final alpha
            byte finalA = baseColor.A;
            if (isBg)
            {
                var sourceA = _backgroundOverrideArgb.HasValue 
                    ? FromArgb(_backgroundOverrideArgb.Value).A 
                    : (byte)255;
                finalA = (byte)Math.Clamp((sourceA * _backgroundOpacity) / 255, 0, 255);
            }
            
            // Pack into ARGB integer
            _colorArgbLut[i] = (finalA << 24) | (baseColor.R << 16) | (baseColor.G << 8) | baseColor.B;
        }
        
        _lutNeedsUpdate = false;
    }
    
    /// <summary>
    /// Sets the border color index.
    /// </summary>
    /// <param name="colorIndex">Color index (0-15)</param>
    public void SetBorderColor(int colorIndex)
    {
        if (_borderColorIndex == colorIndex) return;
        
        _borderColorIndex = colorIndex;
        _lutNeedsUpdate = true;
    }
    
    /// <summary>
    /// Sets the background color index.
    /// </summary>
    /// <param name="colorIndex">Color index (0-15)</param>
    public void SetBackgroundColor(int colorIndex)
    {
        if (_backgroundColorIndex == colorIndex) return;
        
        _backgroundColorIndex = colorIndex;
        _lutNeedsUpdate = true;
    }
    
    /// <summary>
    /// Loads colors into the lower half of the color table (colors 0-7).
    /// </summary>
    /// <param name="colorData">8 colors worth of data (16 bytes)</param>
    public void LoadColorTableLower(ReadOnlySpan<byte> colorData)
    {
        LoadColorTableRange(colorData, 0);
    }
    
    /// <summary>
    /// Loads colors into the upper half of the color table (colors 8-15).
    /// </summary>
    /// <param name="colorData">8 colors worth of data (16 bytes)</param>
    public void LoadColorTableUpper(ReadOnlySpan<byte> colorData)
    {
        LoadColorTableRange(colorData, 8);
    }
    
    /// <summary>
    /// Defines a color as transparent.
    /// </summary>
    /// <param name="colorIndex">Color index to make transparent</param>
    public void DefineTransparentColor(int colorIndex)
    {
        if (colorIndex < 0 || colorIndex >= CdgConstants.ColorCount) return;
        // Host-controlled background opacity handles visual transparency.
        // CDG transparent-color packets are intentionally not mapped to WPF alpha here.
        _lutNeedsUpdate = true;
    }
    
    #endregion

    #region Private Methods
    
    private void LoadColorTableRange(ReadOnlySpan<byte> colorData, int offset)
    {
        if (colorData.Length < 16) return;
        
        for (int i = 0; i < 8; i++)
        {
            var high = colorData[2 * i];
            var low = colorData[2 * i + 1];
            
            // Extract RGB components (CDG color format)
            int r = (high & CdgConstants.RedColorMask) >> 2;
            int g = (high & CdgConstants.GreenColorMask) << 2 | 
                    (low & CdgConstants.GreenLowbyteColorMask) >> 4;
            int b = low & CdgConstants.BlueColorMask;
            
            // Scale to full 8-bit range
            r *= CdgConstants.ColorValueMultiplier;
            g *= CdgConstants.ColorValueMultiplier;
            b *= CdgConstants.ColorValueMultiplier;
            
            _colorTable[i + offset] = Color.FromArgb(255, (byte)r, (byte)g, (byte)b);
        }
        
        _lutNeedsUpdate = true;
    }
    
    private static Color FromArgb(uint argb) =>
        Color.FromArgb(
            (byte)((argb >> 24) & 0xFF), 
            (byte)((argb >> 16) & 0xFF), 
            (byte)((argb >> 8) & 0xFF), 
            (byte)(argb & 0xFF));
    
    #endregion
}
