using System.Drawing;

namespace FatzBombMedia.MediaControl.Core.DxCdg;

/// <summary>
/// CDG specification constants and default values.
/// Based on the CD+G specification for karaoke graphics.
/// </summary>
internal static class DxCdgConstants
{
    #region Frame Geometry

    /// <summary>
    /// CDG frame width in pixels (CD+G specification).
    /// </summary>
    public const int FrameWidth = 300;

    /// <summary>
    /// CDG frame height in pixels (CD+G specification).
    /// </summary>
    public const int FrameHeight = 216;

    /// <summary>
    /// Total pixels in a CDG frame.
    /// </summary>
    public const int TotalPixels = FrameWidth * FrameHeight;

    #endregion

    #region Color System

    /// <summary>
    /// Number of colors in the CDG color table.
    /// </summary>
    public const int ColorCount = 16;

    /// <summary>
    /// Color value multiplier for RGB components.
    /// </summary>
    public const int ColorValueMultiplier = 17;

    #endregion

    #region Timing

    /// <summary>
    /// CDG packets per second (CD+G specification: 75 sectors/second * 4 packets/sector).
    /// </summary>
    public const int PacketsPerSecond = 300;

    #endregion

    #region Tile System

    /// <summary>
    /// Width of a CDG tile in pixels.
    /// </summary>
    public const int TileWidth = 6;

    /// <summary>
    /// Height of a CDG tile in pixels.
    /// </summary>
    public const int TileHeight = 12;

    /// <summary>
    /// Bit shift value for tile pixel processing.
    /// </summary>
    public const int TilePixelBitShift = 5;

    #endregion

    #region Border System

    /// <summary>
    /// Left and right border width in pixels.
    /// </summary>
    public const int BorderLeftRight = 6;

    /// <summary>
    /// Top and bottom border height in pixels.
    /// </summary>
    public const int BorderTopBottom = 12;

    #endregion

    #region Bit Masks

    /// <summary>
    /// Mask for extracting 4-bit color values.
    /// </summary>
    public const int ColorMask = 0x0F;

    /// <summary>
    /// Mask for extracting red color component (6-bit).
    /// </summary>
    public const int RedColorMask = 0x3F;

    /// <summary>
    /// Mask for extracting green color component (2-bit high).
    /// </summary>
    public const int GreenColorMask = 0x03;

    /// <summary>
    /// Mask for extracting green color component (4-bit low).
    /// </summary>
    public const int GreenLowbyteColorMask = 0x3C;

    /// <summary>
    /// Mask for extracting blue color component (4-bit).
    /// </summary>
    public const int BlueColorMask = 0x0F;

    /// <summary>
    /// Mask for tile block X coordinate.
    /// </summary>
    public const int TileBlockXMask = 0x3F;

    /// <summary>
    /// Mask for tile block Y coordinate.
    /// </summary>
    public const int TileBlockYMask = 0x1F;

    /// <summary>
    /// Mask for tile pixel data.
    /// </summary>
    public const int TilePixelDataMask = 0x3F;

    /// <summary>
    /// Mask for individual tile pixel bit.
    /// </summary>
    public const int TilePixelBitMask = 0x01;

    #endregion

    #region Default Color Palette

    /// <summary>
    /// Default CDG color palette (CD+G specification colors).
    /// These are the standard 16 colors used when no custom palette is loaded.
    /// </summary>
    public static readonly Color[] DefaultColorPalette =
    [
        Color.FromArgb(255, 0, 0, 0),       // 0: Black
        Color.FromArgb(255, 0, 0, 170),     // 1: Dark Blue  
        Color.FromArgb(255, 0, 170, 0),     // 2: Dark Green
        Color.FromArgb(255, 0, 170, 170),   // 3: Dark Cyan
        Color.FromArgb(255, 170, 0, 0),     // 4: Dark Red
        Color.FromArgb(255, 170, 0, 170),   // 5: Dark Magenta
        Color.FromArgb(255, 170, 85, 0),    // 6: Brown
        Color.FromArgb(255, 170, 170, 170), // 7: Light Gray
        Color.FromArgb(255, 85, 85, 85),    // 8: Dark Gray
        Color.FromArgb(255, 85, 85, 255),   // 9: Light Blue
        Color.FromArgb(255, 85, 255, 85),   // 10: Light Green
        Color.FromArgb(255, 85, 255, 255),  // 11: Light Cyan
        Color.FromArgb(255, 255, 85, 85),   // 12: Light Red
        Color.FromArgb(255, 255, 85, 255),  // 13: Light Magenta
        Color.FromArgb(255, 255, 255, 85),  // 14: Yellow
        Color.FromArgb(255, 255, 255, 255)  // 15: White
    ];

    #endregion

    #region Command and Instruction Constants

    /// <summary>
    /// CDG command types.
    /// </summary>
    public enum Command : byte
    {
        Graphic = 0x09
    }

    /// <summary>
    /// CDG instruction types for graphic commands.
    /// </summary>
    public enum Instruction : byte
    {
        MemoryPreset = 0x01,
        BorderPreset = 0x02,
        TileBlockNormal = 0x06,
        ScrollPreset = 0x14,
        ScrollCopy = 0x18,
        DefineTransparentColor = 0x1C,
        LoadColorTableLower = 0x1E,
        LoadColorTableUpper = 0x1F,
        TileBlockXor = 0x26
    }

    #endregion

    #region Packet Structure

    /// <summary>
    /// Size of a CDG packet in bytes (CD+G specification).
    /// </summary>
    public const int PacketSize = 24;

    #endregion
}
