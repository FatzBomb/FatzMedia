using System;

namespace FatzBombMedia.MediaControl.Core.Cdg;

/// <summary>
/// Handles CDG packet processing and instruction execution.
/// Contains all the CDG specification compliance logic for rendering graphics.
/// This is where the complex CDG format decoding happens.
/// </summary>
/// <remarks>
/// Initializes a new CDG packet processor.
/// </remarks>
/// <param name="pixelColors">Main pixel buffer</param>
/// <param name="tempPixelColors">Temporary pixel buffer for scroll operations</param>
/// <param name="colorManager">Color manager for border/background updates</param>
internal sealed class CdgPacketProcessor(byte[] pixelColors, byte[] tempPixelColors, CdgColorManager colorManager)
{
    private readonly byte[] _pixelColors = pixelColors ?? throw new ArgumentNullException(nameof(pixelColors));
    private readonly byte[] _tempPixelColors = tempPixelColors ?? throw new ArgumentNullException(nameof(tempPixelColors));
    private readonly CdgColorManager _colorManager = colorManager ?? throw new ArgumentNullException(nameof(colorManager));

    /// <summary>
    /// Processes a single CDG packet according to the CD+G specification.
    /// Returns true if the packet caused any visual changes that require UI update.
    /// </summary>
    /// <param name="packet">The packet to process</param>
    /// <returns>True if pixels were modified and UI update is needed</returns>
    public bool ProcessPacket(CdgPacket packet)
    {
        if (packet.Command != CdgConstants.Command.Graphic) return false;
        
        return packet.Instruction switch
        {
            CdgConstants.Instruction.MemoryPreset => ProcessMemoryPreset(packet),
            CdgConstants.Instruction.BorderPreset => ProcessBorderPreset(packet),
            CdgConstants.Instruction.TileBlockNormal => ProcessTileBlock(packet, false),
            CdgConstants.Instruction.TileBlockXor => ProcessTileBlock(packet, true),
            CdgConstants.Instruction.ScrollPreset => Scroll(packet, false),
            CdgConstants.Instruction.ScrollCopy => Scroll(packet, true),
            CdgConstants.Instruction.LoadColorTableLower => ProcessLoadColorTableLower(packet),
            CdgConstants.Instruction.LoadColorTableUpper => ProcessLoadColorTableUpper(packet),
            CdgConstants.Instruction.DefineTransparentColor => ProcessDefineTransparentColor(packet),
            _ => false
        };
    }

    #region CDG Instruction Processing (Your Hard Work!)
    
    /// <summary>
    /// Processes Memory Preset instruction - clears screen and sets background color.
    /// </summary>
    /// <returns>True if visual changes occurred</returns>
    private bool ProcessMemoryPreset(CdgPacket packet)
    {
        var dataSpan = packet.DataSpan;
        var color = (byte)(dataSpan[0] & CdgConstants.ColorMask);
        
        bool hasChanges;
        
        // Set background color (this may trigger LUT updates)
        _colorManager.SetBackgroundColor(color);
        hasChanges = true; // Background color changes always require updates
        
        // Check if we need to clear the screen
        if ((dataSpan[1] & CdgConstants.ColorMask) == 0)
        {
            var span = _pixelColors.AsSpan();
            
            // Check if screen actually needs clearing (optimization from old code)
            for (int i = 0; i < span.Length; i++)
                if (span[i] != color)
                {
                    hasChanges = true;
                    span.Fill(color);
                    break;
                }
        }
        
        return hasChanges;
    }
    
    /// <summary>
    /// Processes Border Preset instruction - fills border area with specified color.
    /// </summary>
    /// <returns>True if visual changes occurred</returns>
    private bool ProcessBorderPreset(CdgPacket packet)
    {
        var dataSpan = packet.DataSpan;
        var color = (byte)(dataSpan[0] & CdgConstants.ColorMask);
        
        bool hasChanges;
        
        // Set border color (this may trigger LUT updates)
        _colorManager.SetBorderColor(color);
        hasChanges = true; // Border color changes always require updates
        
        var span = _pixelColors.AsSpan();
        
        // Fill left and right borders for all rows - check for actual changes
        for (int row = 0; row < CdgConstants.FrameHeight; row++)
        {
            int rowOffset = row * CdgConstants.FrameWidth;
            
            // Check and update left border
            var leftBorder = span.Slice(rowOffset, CdgConstants.BorderLeftRight);
            for (int i = 0; i < leftBorder.Length; i++)
                if (leftBorder[i] != color)
                {
                    leftBorder[i] = color;
                    hasChanges = true;
                }
            
            // Check and update right border
            var rightBorder = span.Slice(rowOffset + CdgConstants.FrameWidth - CdgConstants.BorderLeftRight, CdgConstants.BorderLeftRight);
            for (int i = 0; i < rightBorder.Length; i++)
                if (rightBorder[i] != color)
                {
                    rightBorder[i] = color;
                    hasChanges = true;
                }
        }
        
        // Fill top and bottom borders - check for changes
        for (int row = 0; row < CdgConstants.BorderTopBottom; row++)
        {
            int topRowOffset = row * CdgConstants.FrameWidth + CdgConstants.BorderLeftRight;
            int bottomRowOffset = (CdgConstants.FrameHeight - CdgConstants.BorderTopBottom + row) * CdgConstants.FrameWidth + CdgConstants.BorderLeftRight;
            int fillWidth = CdgConstants.FrameWidth - 2 * CdgConstants.BorderLeftRight;
            
            var topBorder = span.Slice(topRowOffset, fillWidth);
            var bottomBorder = span.Slice(bottomRowOffset, fillWidth);
            
            for (int i = 0; i < fillWidth; i++)
            {
                if (topBorder[i] != color)
                {
                    topBorder[i] = color;
                    hasChanges = true;
                }
                if (bottomBorder[i] != color)
                {
                    bottomBorder[i] = color;
                    hasChanges = true;
                }
            }
        }
        
        return hasChanges;
    }
    
    /// <summary>
    /// Processes Tile Block instruction - renders 6x12 pixel tiles (the core of CDG text/graphics).
    /// This is the most complex and important CDG instruction.
    /// </summary>
    /// <returns>True if visual changes occurred</returns>
    private bool ProcessTileBlock(CdgPacket packet, bool bXor)
    {
        var dataSpan = packet.DataSpan;
        byte color0 = (byte)(dataSpan[0] & CdgConstants.ColorMask);
        byte color1 = (byte)(dataSpan[1] & CdgConstants.ColorMask);
        int columnIndex = (dataSpan[3] & CdgConstants.TileBlockXMask) * CdgConstants.TileWidth;
        int rowIndex = (dataSpan[2] & CdgConstants.TileBlockYMask) * CdgConstants.TileHeight;
        
        if (rowIndex >= CdgConstants.FrameHeight || columnIndex >= CdgConstants.FrameWidth) return false;
        
        byte[] lookup = [color0, color1];
        bool hasChanges = false;
        
        for (int i = 0; i < CdgConstants.TileHeight; i++)
        {
            int tileRow = rowIndex + i;
            if (tileRow >= CdgConstants.FrameHeight) break;
            
            int rowBits = dataSpan[4 + i] & CdgConstants.TilePixelDataMask;
            int maxColumn = Math.Min(CdgConstants.TileWidth, CdgConstants.FrameWidth - columnIndex);
            
            for (int j = 0; j < maxColumn; j++)
            {
                int tileCol = columnIndex + j;
                int bit = (rowBits >> (CdgConstants.TilePixelBitShift - j)) & CdgConstants.TilePixelBitMask;
                byte newColor = lookup[bit];
                int index = tileRow * CdgConstants.FrameWidth + tileCol;
                
                if (bXor)
                    newColor ^= _pixelColors[index];
                
                // Only update if pixel actually changed (critical optimization from old code)
                if (_pixelColors[index] != newColor)
                {
                    _pixelColors[index] = newColor;
                    hasChanges = true;
                }
            }
        }
        
        return hasChanges;
    }
    
    /// <summary>
    /// Processes Scroll instruction - scrolls the entire display in tile increments.
    /// Used for karaoke text effects and screen transitions.
    /// </summary>
    /// <returns>True if visual changes occurred</returns>
    private bool Scroll(CdgPacket packet, bool copy)
    {
        var dataSpan = packet.DataSpan;
        byte color = (byte)(dataSpan[0] & CdgConstants.ColorMask);
        int h = dataSpan[1], v = dataSpan[2];
        
        int hCmd = (h & 0x30) >> 4;
        int hPx = hCmd == 1 ? CdgConstants.TileWidth : hCmd == 2 ? -CdgConstants.TileWidth : 0;
        
        int vCmd = (v & 0x30) >> 4;
        int vPx = vCmd == 1 ? CdgConstants.TileHeight : vCmd == 2 ? -CdgConstants.TileHeight : 0;
        
        // If no scrolling, no changes
        if (hPx == 0 && vPx == 0) return false;
        
        int width = CdgConstants.FrameWidth, height = CdgConstants.FrameHeight;
        Array.Clear(_tempPixelColors, 0, _tempPixelColors.Length);
        
        // Perform the scroll operation
        for (int row = 0; row < height; row++)
        {
            int srcRow = row;
            int dstRow = (row + vPx + height) % height;
            int srcOff = srcRow * width;
            int dstOff = dstRow * width;
            
            for (int col = 0; col < width; col++)
            {
                int dstCol = (col + hPx + width) % width;
                _tempPixelColors[dstOff + dstCol] = _pixelColors[srcOff + col];
            }
        }
        
        // Fill exposed areas if not copying
        if (!copy)
        {
            if (vPx > 0)
            {
                for (int row = 0; row < vPx; row++)
                    Array.Fill(_tempPixelColors, color, row * width, width);
            }
            else if (vPx < 0)
            {
                for (int row = height + vPx; row < height; row++)
                    Array.Fill(_tempPixelColors, color, row * width, width);
            }
            
            if (hPx != 0)
            {
                for (int row = 0; row < height; row++)
                {
                    int rowOff = row * width;
                    if (hPx > 0)
                    {
                        for (int col = 0; col < hPx; col++)
                            _tempPixelColors[rowOff + col] = color;
                    }
                    else
                    {
                        for (int col = width + hPx; col < width; col++)
                            _tempPixelColors[rowOff + col] = color;
                    }
                }
            }
        }
        
        // Check if the scroll actually changed anything
        bool hasChanges = false;
        for (int i = 0; i < _pixelColors.Length; i++)
        {
            if (_pixelColors[i] != _tempPixelColors[i])
            {
                hasChanges = true;
                break;
            }
        }
        
        // Only copy if there are actual changes
        if (hasChanges)
        {
            Buffer.BlockCopy(_tempPixelColors, 0, _pixelColors, 0, _tempPixelColors.Length);
        }
        
        return hasChanges;
    }
    
    /// <summary>
    /// Processes Load Color Table Lower instruction.
    /// </summary>
    /// <returns>True if visual changes occurred</returns>
    private bool ProcessLoadColorTableLower(CdgPacket packet)
    {
        // Color table changes always require visual updates since they affect all pixels
        _colorManager.LoadColorTableLower(packet.DataSpan);
        return true;
    }
    
    /// <summary>
    /// Processes Load Color Table Upper instruction.
    /// </summary>
    /// <returns>True if visual changes occurred</returns>
    private bool ProcessLoadColorTableUpper(CdgPacket packet)
    {
        // Color table changes always require visual updates since they affect all pixels
        _colorManager.LoadColorTableUpper(packet.DataSpan);
        return true;
    }
    
    /// <summary>
    /// Processes Define Transparent Color instruction.
    /// </summary>
    /// <returns>True if visual changes occurred</returns>
    private bool ProcessDefineTransparentColor(CdgPacket packet)
    {
        var colorIndex = packet.DataSpan[0] & CdgConstants.ColorMask;
        // Transparency changes always require visual updates
        _colorManager.DefineTransparentColor(colorIndex);
        return true;
    }
    
    #endregion
}

/// <summary>
/// Represents a CDG packet with command and instruction data.
/// This is the basic unit of CDG processing.
/// </summary>
internal readonly struct CdgPacket
{
    private readonly ReadOnlyMemory<byte> _data;
    
    public CdgPacket(ReadOnlySpan<byte> data)
    {
        _data = data.ToArray();
        Command = (CdgConstants.Command)(data[0] & 0x3F);
        Instruction = (CdgConstants.Instruction)(data[1] & 0x3F);
    }
    
    public CdgConstants.Command Command { get; }
    public CdgConstants.Instruction Instruction { get; }
    public ReadOnlySpan<byte> DataSpan => _data.Span.Slice(4, 16);
}