using System.Diagnostics;

namespace FatzBombMedia.MediaControl.Core.Cdg;

/// <summary>
/// Authoritative CDG state model. It keeps CDG pixels as color indices plus
/// palette/wash state so the GPU presenter can render CDG directly without
/// generating a full bitmap frame.
/// </summary>
public sealed class CdgStateMachine
{
    private readonly byte[] _colorIndices = new byte[CdgConstants.TotalPixels];
    private readonly byte[] _scratchColorIndices = new byte[CdgConstants.TotalPixels];
    private readonly int[] _paletteArgb = new int[CdgConstants.ColorCount];
    private readonly CdgColorManager _colorManager = new();
    private readonly List<CdgDirtyRect> _dirtyRects = [];
    private readonly Lock _lock = new();

    private long _revision;
    private long _paletteRevision;
    private uint _backgroundWashArgb;

    public CdgStateMachine() => Reset();

#pragma warning disable CA1822 // Public API instance properties; intentionally NOT static for callers.
    public int Width => CdgConstants.FrameWidth;
    public int Height => CdgConstants.FrameHeight;
#pragma warning restore CA1822
    public long Revision
    {
        get
        {
            lock (_lock)
            {
                return _revision;
            }
        }
    }

    public uint BackgroundWashArgb
    {
        get
        {
            lock (_lock)
            {
                return _backgroundWashArgb;
            }
        }
    }

    public byte BackgroundOpacity
    {
        get => _colorManager.BackgroundOpacity;
        set
        {
            lock (_lock)
            {
                _colorManager.BackgroundOpacity = value;
                RefreshColorStateUnsafe(addDirtyRect: false);
            }
        }
    }

    public uint? BackgroundOverrideArgb
    {
        get => _colorManager.BackgroundOverrideArgb;
        set
        {
            lock (_lock)
            {
                _colorManager.BackgroundOverrideArgb = value;
                RefreshColorStateUnsafe(addDirtyRect: true);
            }
        }
    }

    /// <summary>
    /// Resets indexed CDG state back to the default palette and blank screen.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            Array.Clear(_colorIndices);
            Array.Clear(_scratchColorIndices);
            _dirtyRects.Clear();
            _colorManager.Reset();
            _paletteRevision++;
            RefreshColorStateUnsafe(addDirtyRect: false);
            AddDirtyRectUnsafe(CdgDirtyRect.FullFrame);
            _revision++;
        }
    }

    /// <summary>
    /// Processes one raw 24-byte CDG packet into indexed-color state.
    /// Returns true when the packet changed visual state or palette state.
    /// </summary>
    public bool ProcessPacket(ReadOnlySpan<byte> packetBytes) =>
        packetBytes.Length >= CdgConstants.PacketSize && ProcessPacket(new CdgPacket(packetBytes[..CdgConstants.PacketSize]));

    internal bool ProcessPacket(CdgPacket packet)
    {
        if (packet.Command != CdgConstants.Command.Graphic)
        {
            return false;
        }

        lock (_lock)
        {
            var changed = packet.Instruction switch
            {
                CdgConstants.Instruction.MemoryPreset => ProcessMemoryPresetUnsafe(packet),
                CdgConstants.Instruction.BorderPreset => ProcessBorderPresetUnsafe(packet),
                CdgConstants.Instruction.TileBlockNormal => ProcessTileBlockUnsafe(packet, xor: false),
                CdgConstants.Instruction.TileBlockXor => ProcessTileBlockUnsafe(packet, xor: true),
                CdgConstants.Instruction.ScrollPreset => ScrollUnsafe(packet, copy: false),
                CdgConstants.Instruction.ScrollCopy => ScrollUnsafe(packet, copy: true),
                CdgConstants.Instruction.LoadColorTableLower => LoadColorTableUnsafe(packet, lower: true),
                CdgConstants.Instruction.LoadColorTableUpper => LoadColorTableUnsafe(packet, lower: false),
                CdgConstants.Instruction.DefineTransparentColor => DefineTransparentColorUnsafe(packet),
                _ => false
            };

            if (changed)
            {
                _revision++;
            }

            return changed;
        }
    }

    public void CopyColorIndices(Span<byte> destination)
    {
        if (destination.Length < _colorIndices.Length)
        {
            throw new ArgumentException("Destination is too small for the CDG color-index frame.", nameof(destination));
        }

        lock (_lock)
        {
            _colorIndices.AsSpan().CopyTo(destination);
        }
    }

    public CdgRenderSnapshot CreateSnapshot(bool consumeDirtyRegions = true)
    {
        lock (_lock)
        {
            var colorCopy = _colorIndices.ToArray();
            var paletteCopy = _paletteArgb.ToArray();
            CdgDirtyRect[] dirtyCopy = _dirtyRects.Count == 0
                ? []
                : [.. _dirtyRects];

            if (consumeDirtyRegions)
            {
                _dirtyRects.Clear();
            }

            return new CdgRenderSnapshot(
                _revision,
                _paletteRevision,
                colorCopy,
                paletteCopy,
                _backgroundWashArgb,
                _colorManager.BackgroundColorIndex,
                dirtyCopy);
        }
    }

    private bool ProcessMemoryPresetUnsafe(CdgPacket packet)
    {
        ReadOnlySpan<byte> dataSpan = packet.DataSpan;
        var color = (byte)(dataSpan[0] & CdgConstants.ColorMask);
        _colorManager.SetBackgroundColor(color);
        RefreshColorStateUnsafe(addDirtyRect: false);
        var changed = true;
        if ((dataSpan[1] & CdgConstants.ColorMask) == 0)
        {
            Span<byte> span = _colorIndices.AsSpan();
            for (var i = 0; i < span.Length; i++)
            {
                if (span[i] == color)
                {
                    continue;
                }

                span.Fill(color);
                AddDirtyRectUnsafe(CdgDirtyRect.FullFrame);
                return true;
            }
        }

        if (changed)
        {
            AddDirtyRectUnsafe(CdgDirtyRect.FullFrame);
        }

        return changed;
    }

    private bool ProcessBorderPresetUnsafe(CdgPacket packet)
    {
        ReadOnlySpan<byte> dataSpan = packet.DataSpan;
        var color = (byte)(dataSpan[0] & CdgConstants.ColorMask);
        var changed = false;

        _colorManager.SetBorderColor(color);
        RefreshColorStateUnsafe(addDirtyRect: false);

        for (var row = 0; row < CdgConstants.FrameHeight; row++)
        {
            var rowOffset = row * CdgConstants.FrameWidth;
            changed |= FillRangeIfChangedUnsafe(rowOffset, CdgConstants.BorderLeftRight, color);
            changed |= FillRangeIfChangedUnsafe(rowOffset + CdgConstants.FrameWidth - CdgConstants.BorderLeftRight, CdgConstants.BorderLeftRight, color);
        }

        var fillWidth = CdgConstants.FrameWidth - (2 * CdgConstants.BorderLeftRight);
        for (var row = 0; row < CdgConstants.BorderTopBottom; row++)
        {
            var topOffset = (row * CdgConstants.FrameWidth) + CdgConstants.BorderLeftRight;
            var bottomOffset = ((CdgConstants.FrameHeight - CdgConstants.BorderTopBottom + row) * CdgConstants.FrameWidth) + CdgConstants.BorderLeftRight;
            changed |= FillRangeIfChangedUnsafe(topOffset, fillWidth, color);
            changed |= FillRangeIfChangedUnsafe(bottomOffset, fillWidth, color);
        }

        if (changed)
        {
            AddDirtyRectUnsafe(CdgDirtyRect.FullFrame);
        }

        return true;
    }

    private bool ProcessTileBlockUnsafe(CdgPacket packet, bool xor)
    {
        ReadOnlySpan<byte> dataSpan = packet.DataSpan;
        var color0 = (byte)(dataSpan[0] & CdgConstants.ColorMask);
        var color1 = (byte)(dataSpan[1] & CdgConstants.ColorMask);
        var columnIndex = (dataSpan[3] & CdgConstants.TileBlockXMask) * CdgConstants.TileWidth;
        var rowIndex = (dataSpan[2] & CdgConstants.TileBlockYMask) * CdgConstants.TileHeight;

        if (rowIndex >= CdgConstants.FrameHeight || columnIndex >= CdgConstants.FrameWidth)
        {
            return false;
        }

        Span<byte> lookup = [color0, color1];
        var changed = false;
        var maxColumn = Math.Min(CdgConstants.TileWidth, CdgConstants.FrameWidth - columnIndex);
        var maxRow = Math.Min(CdgConstants.TileHeight, CdgConstants.FrameHeight - rowIndex);

        for (var row = 0; row < maxRow; row++)
        {
            var rowBits = dataSpan[4 + row] & CdgConstants.TilePixelDataMask;
            var frameRow = rowIndex + row;
            for (var col = 0; col < maxColumn; col++)
            {
                var bit = (rowBits >> (CdgConstants.TilePixelBitShift - col)) & CdgConstants.TilePixelBitMask;
                var newColor = lookup[bit];
                var index = (frameRow * CdgConstants.FrameWidth) + columnIndex + col;

                if (xor)
                {
                    newColor ^= _colorIndices[index];
                }

                if (_colorIndices[index] == newColor)
                {
                    continue;
                }

                _colorIndices[index] = newColor;
                changed = true;
            }
        }

        if (changed)
        {
            AddDirtyRectUnsafe(new CdgDirtyRect(columnIndex, rowIndex, maxColumn, maxRow));
        }

        return changed;
    }

    private bool ScrollUnsafe(CdgPacket packet, bool copy)
    {
        ReadOnlySpan<byte> dataSpan = packet.DataSpan;
        var color = (byte)(dataSpan[0] & CdgConstants.ColorMask);
        var h = dataSpan[1];
        var v = dataSpan[2];

        var hCmd = (h & 0x30) >> 4;
        var hPx = hCmd == 1 ? CdgConstants.TileWidth : hCmd == 2 ? -CdgConstants.TileWidth : 0;

        var vCmd = (v & 0x30) >> 4;
        var vPx = vCmd == 1 ? CdgConstants.TileHeight : vCmd == 2 ? -CdgConstants.TileHeight : 0;

        if (hPx == 0 && vPx == 0)
        {
            return false;
        }

        var width = CdgConstants.FrameWidth;
        var height = CdgConstants.FrameHeight;
        Array.Clear(_scratchColorIndices);

        for (var row = 0; row < height; row++)
        {
            var srcOffset = row * width;
            var destinationRow = (row + vPx + height) % height;
            var destinationOffset = destinationRow * width;

            for (var col = 0; col < width; col++)
            {
                var destinationColumn = (col + hPx + width) % width;
                _scratchColorIndices[destinationOffset + destinationColumn] = _colorIndices[srcOffset + col];
            }
        }

        if (!copy)
        {
            FillScrollExposedAreasUnsafe(color, hPx, vPx, width, height);
        }

        if (_colorIndices.AsSpan().SequenceEqual(_scratchColorIndices))
        {
            return false;
        }

        Buffer.BlockCopy(_scratchColorIndices, 0, _colorIndices, 0, _scratchColorIndices.Length);
        AddDirtyRectUnsafe(CdgDirtyRect.FullFrame);
        return true;
    }

    private bool LoadColorTableUnsafe(CdgPacket packet, bool lower)
    {
        if (lower)
        {
            _colorManager.LoadColorTableLower(packet.DataSpan);
        }
        else
        {
            _colorManager.LoadColorTableUpper(packet.DataSpan);
        }

        RefreshColorStateUnsafe(addDirtyRect: true);
        return true;
    }

    private bool DefineTransparentColorUnsafe(CdgPacket packet)
    {
        var colorIndex = packet.DataSpan[0] & CdgConstants.ColorMask;
        _colorManager.DefineTransparentColor(colorIndex);
        RefreshColorStateUnsafe(addDirtyRect: true);
        return true;
    }

    private bool FillRangeIfChangedUnsafe(int start, int length, byte color)
    {
        Span<byte> span = _colorIndices.AsSpan(start, length);
        var changed = false;
        for (var i = 0; i < span.Length; i++)
        {
            if (span[i] == color)
            {
                continue;
            }

            span[i] = color;
            changed = true;
        }

        return changed;
    }

    private void FillScrollExposedAreasUnsafe(byte color, int hPx, int vPx, int width, int height)
    {
        if (vPx > 0)
        {
            for (var row = 0; row < vPx; row++)
            {
                Array.Fill(_scratchColorIndices, color, row * width, width);
            }
        }
        else if (vPx < 0)
        {
            for (var row = height + vPx; row < height; row++)
            {
                Array.Fill(_scratchColorIndices, color, row * width, width);
            }
        }

        if (hPx == 0)
        {
            return;
        }

        for (var row = 0; row < height; row++)
        {
            var rowOffset = row * width;
            if (hPx > 0)
            {
                for (var col = 0; col < hPx; col++)
                {
                    _scratchColorIndices[rowOffset + col] = color;
                }
            }
            else
            {
                for (var col = width + hPx; col < width; col++)
                {
                    _scratchColorIndices[rowOffset + col] = color;
                }
            }
        }
    }

    private void RefreshColorStateUnsafe(bool addDirtyRect)
    {
        if (_colorManager.UpdateLutIfNeeded())
        {
            _paletteRevision++;
        }

        _ = _colorManager.UpdateBackgroundWashIfNeeded();
        _colorManager.ColorLut.CopyTo(_paletteArgb);
        _backgroundWashArgb = _colorManager.BackgroundWashArgb;

        if (addDirtyRect)
        {
            AddDirtyRectUnsafe(CdgDirtyRect.FullFrame);
        }
    }

    private void AddDirtyRectUnsafe(CdgDirtyRect rect)
    {
        CdgDirtyRect clamped = rect.ClampToFrame();
        if (clamped.IsEmpty)
        {
            return;
        }

        if (clamped == CdgDirtyRect.FullFrame)
        {
            _dirtyRects.Clear();
            _dirtyRects.Add(CdgDirtyRect.FullFrame);
            return;
        }

        if (_dirtyRects.Count == 1 && _dirtyRects[0] == CdgDirtyRect.FullFrame)
        {
            return;
        }

        _dirtyRects.Add(clamped);

        // Avoid an unbounded dirty-list during dense lyric updates. A full-frame update is
        // simpler and still cheap at 300x216 until the GPU tile atlas lands.
        if (_dirtyRects.Count > 64)
        {
            Debug.WriteLine("[CDG] Dirty region list exceeded threshold; promoting to full-frame dirty.");
            _dirtyRects.Clear();
            _dirtyRects.Add(CdgDirtyRect.FullFrame);
        }
    }
}
