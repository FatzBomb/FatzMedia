using System.Diagnostics;

namespace FatzBombMedia.MediaControl.Core.DxCdg;

/// <summary>
/// Authoritative DXCDG state model. It keeps CDG pixels as color indices plus
/// palette/wash state so the GPU presenter can render CDG directly without
/// generating a full bitmap frame.
/// </summary>
public sealed class DxCdgStateMachine
{
    private readonly byte[] _colorIndices = new byte[DxCdgConstants.TotalPixels];
    private readonly byte[] _scratchColorIndices = new byte[DxCdgConstants.TotalPixels];
    private readonly int[] _paletteArgb = new int[DxCdgConstants.ColorCount];
    private readonly DxCdgColorManager _colorManager = new();
    private readonly List<DxCdgDirtyRect> _dirtyRects = [];
    private readonly Lock _lock = new();

    private long _revision;
    private uint _backgroundWashArgb;

    public DxCdgStateMachine()
    {
        Reset();
    }

    public int Width => DxCdgConstants.FrameWidth;
    public int Height => DxCdgConstants.FrameHeight;
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
            RefreshColorStateUnsafe(addDirtyRect: false);
            AddDirtyRectUnsafe(DxCdgDirtyRect.FullFrame);
            _revision++;
        }
    }

    /// <summary>
    /// Processes one raw 24-byte CDG packet into indexed-color state.
    /// Returns true when the packet changed visual state or palette state.
    /// </summary>
    public bool ProcessPacket(ReadOnlySpan<byte> packetBytes)
    {
        if (packetBytes.Length < DxCdgConstants.PacketSize)
        {
            return false;
        }

        return ProcessPacket(new DxCdgPacket(packetBytes[..DxCdgConstants.PacketSize]));
    }

    internal bool ProcessPacket(DxCdgPacket packet)
    {
        if (packet.Command != DxCdgConstants.Command.Graphic)
        {
            return false;
        }

        lock (_lock)
        {
            var changed = packet.Instruction switch
            {
                DxCdgConstants.Instruction.MemoryPreset => ProcessMemoryPresetUnsafe(packet),
                DxCdgConstants.Instruction.BorderPreset => ProcessBorderPresetUnsafe(packet),
                DxCdgConstants.Instruction.TileBlockNormal => ProcessTileBlockUnsafe(packet, xor: false),
                DxCdgConstants.Instruction.TileBlockXor => ProcessTileBlockUnsafe(packet, xor: true),
                DxCdgConstants.Instruction.ScrollPreset => ScrollUnsafe(packet, copy: false),
                DxCdgConstants.Instruction.ScrollCopy => ScrollUnsafe(packet, copy: true),
                DxCdgConstants.Instruction.LoadColorTableLower => LoadColorTableUnsafe(packet, lower: true),
                DxCdgConstants.Instruction.LoadColorTableUpper => LoadColorTableUnsafe(packet, lower: false),
                DxCdgConstants.Instruction.DefineTransparentColor => DefineTransparentColorUnsafe(packet),
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

    public DxCdgRenderSnapshot CreateSnapshot(bool consumeDirtyRegions = true)
    {
        lock (_lock)
        {
            var colorCopy = _colorIndices.ToArray();
            var paletteCopy = _paletteArgb.ToArray();
            var dirtyCopy = _dirtyRects.Count == 0
                ? Array.Empty<DxCdgDirtyRect>()
                : _dirtyRects.ToArray();

            if (consumeDirtyRegions)
            {
                _dirtyRects.Clear();
            }

            return new DxCdgRenderSnapshot(
                _revision,
                colorCopy,
                paletteCopy,
                _backgroundWashArgb,
                _colorManager.BackgroundColorIndex,
                dirtyCopy);
        }
    }

    private bool ProcessMemoryPresetUnsafe(DxCdgPacket packet)
    {
        var dataSpan = packet.DataSpan;
        var color = (byte)(dataSpan[0] & DxCdgConstants.ColorMask);
        var changed = false;

        _colorManager.SetBackgroundColor(color);
        RefreshColorStateUnsafe(addDirtyRect: false);
        changed = true;

        if ((dataSpan[1] & DxCdgConstants.ColorMask) == 0)
        {
            var span = _colorIndices.AsSpan();
            for (var i = 0; i < span.Length; i++)
            {
                if (span[i] == color)
                {
                    continue;
                }

                span.Fill(color);
                AddDirtyRectUnsafe(DxCdgDirtyRect.FullFrame);
                return true;
            }
        }

        if (changed)
        {
            AddDirtyRectUnsafe(DxCdgDirtyRect.FullFrame);
        }

        return changed;
    }

    private bool ProcessBorderPresetUnsafe(DxCdgPacket packet)
    {
        var dataSpan = packet.DataSpan;
        var color = (byte)(dataSpan[0] & DxCdgConstants.ColorMask);
        var changed = false;

        _colorManager.SetBorderColor(color);
        RefreshColorStateUnsafe(addDirtyRect: false);

        for (var row = 0; row < DxCdgConstants.FrameHeight; row++)
        {
            var rowOffset = row * DxCdgConstants.FrameWidth;
            changed |= FillRangeIfChangedUnsafe(rowOffset, DxCdgConstants.BorderLeftRight, color);
            changed |= FillRangeIfChangedUnsafe(rowOffset + DxCdgConstants.FrameWidth - DxCdgConstants.BorderLeftRight, DxCdgConstants.BorderLeftRight, color);
        }

        var fillWidth = DxCdgConstants.FrameWidth - (2 * DxCdgConstants.BorderLeftRight);
        for (var row = 0; row < DxCdgConstants.BorderTopBottom; row++)
        {
            var topOffset = (row * DxCdgConstants.FrameWidth) + DxCdgConstants.BorderLeftRight;
            var bottomOffset = ((DxCdgConstants.FrameHeight - DxCdgConstants.BorderTopBottom + row) * DxCdgConstants.FrameWidth) + DxCdgConstants.BorderLeftRight;
            changed |= FillRangeIfChangedUnsafe(topOffset, fillWidth, color);
            changed |= FillRangeIfChangedUnsafe(bottomOffset, fillWidth, color);
        }

        if (changed)
        {
            AddDirtyRectUnsafe(DxCdgDirtyRect.FullFrame);
        }

        return true;
    }

    private bool ProcessTileBlockUnsafe(DxCdgPacket packet, bool xor)
    {
        var dataSpan = packet.DataSpan;
        var color0 = (byte)(dataSpan[0] & DxCdgConstants.ColorMask);
        var color1 = (byte)(dataSpan[1] & DxCdgConstants.ColorMask);
        var columnIndex = (dataSpan[3] & DxCdgConstants.TileBlockXMask) * DxCdgConstants.TileWidth;
        var rowIndex = (dataSpan[2] & DxCdgConstants.TileBlockYMask) * DxCdgConstants.TileHeight;

        if (rowIndex >= DxCdgConstants.FrameHeight || columnIndex >= DxCdgConstants.FrameWidth)
        {
            return false;
        }

        Span<byte> lookup = stackalloc byte[] { color0, color1 };
        var changed = false;
        var maxColumn = Math.Min(DxCdgConstants.TileWidth, DxCdgConstants.FrameWidth - columnIndex);
        var maxRow = Math.Min(DxCdgConstants.TileHeight, DxCdgConstants.FrameHeight - rowIndex);

        for (var row = 0; row < maxRow; row++)
        {
            var rowBits = dataSpan[4 + row] & DxCdgConstants.TilePixelDataMask;
            var frameRow = rowIndex + row;
            for (var col = 0; col < maxColumn; col++)
            {
                var bit = (rowBits >> (DxCdgConstants.TilePixelBitShift - col)) & DxCdgConstants.TilePixelBitMask;
                var newColor = lookup[bit];
                var index = (frameRow * DxCdgConstants.FrameWidth) + columnIndex + col;

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
            AddDirtyRectUnsafe(new DxCdgDirtyRect(columnIndex, rowIndex, maxColumn, maxRow));
        }

        return changed;
    }

    private bool ScrollUnsafe(DxCdgPacket packet, bool copy)
    {
        var dataSpan = packet.DataSpan;
        var color = (byte)(dataSpan[0] & DxCdgConstants.ColorMask);
        var h = dataSpan[1];
        var v = dataSpan[2];

        var hCmd = (h & 0x30) >> 4;
        var hPx = hCmd == 1 ? DxCdgConstants.TileWidth : hCmd == 2 ? -DxCdgConstants.TileWidth : 0;

        var vCmd = (v & 0x30) >> 4;
        var vPx = vCmd == 1 ? DxCdgConstants.TileHeight : vCmd == 2 ? -DxCdgConstants.TileHeight : 0;

        if (hPx == 0 && vPx == 0)
        {
            return false;
        }

        var width = DxCdgConstants.FrameWidth;
        var height = DxCdgConstants.FrameHeight;
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
        AddDirtyRectUnsafe(DxCdgDirtyRect.FullFrame);
        return true;
    }

    private bool LoadColorTableUnsafe(DxCdgPacket packet, bool lower)
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

    private bool DefineTransparentColorUnsafe(DxCdgPacket packet)
    {
        var colorIndex = packet.DataSpan[0] & DxCdgConstants.ColorMask;
        _colorManager.DefineTransparentColor(colorIndex);
        RefreshColorStateUnsafe(addDirtyRect: true);
        return true;
    }

    private bool FillRangeIfChangedUnsafe(int start, int length, byte color)
    {
        var span = _colorIndices.AsSpan(start, length);
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
        _colorManager.UpdateLutIfNeeded();
        _colorManager.UpdateBackgroundWashIfNeeded();
        _colorManager.ColorLut.CopyTo(_paletteArgb);
        _backgroundWashArgb = _colorManager.BackgroundWashArgb;

        if (addDirtyRect)
        {
            AddDirtyRectUnsafe(DxCdgDirtyRect.FullFrame);
        }
    }

    private void AddDirtyRectUnsafe(DxCdgDirtyRect rect)
    {
        var clamped = rect.ClampToFrame();
        if (clamped.IsEmpty)
        {
            return;
        }

        if (clamped == DxCdgDirtyRect.FullFrame)
        {
            _dirtyRects.Clear();
            _dirtyRects.Add(DxCdgDirtyRect.FullFrame);
            return;
        }

        if (_dirtyRects.Count == 1 && _dirtyRects[0] == DxCdgDirtyRect.FullFrame)
        {
            return;
        }

        _dirtyRects.Add(clamped);

        // Avoid an unbounded dirty-list during dense lyric updates. A full-frame update is
        // simpler and still cheap at 300x216 until the GPU tile atlas lands.
        if (_dirtyRects.Count > 64)
        {
            Debug.WriteLine("[DXCDG] Dirty region list exceeded threshold; promoting to full-frame dirty.");
            _dirtyRects.Clear();
            _dirtyRects.Add(DxCdgDirtyRect.FullFrame);
        }
    }
}
