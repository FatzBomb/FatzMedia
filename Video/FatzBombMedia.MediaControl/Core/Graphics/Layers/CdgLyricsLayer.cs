namespace FatzBombMedia.MediaControl.Core.Graphics.Layers;

/// <summary>
/// Retains the latest native-size transparent-background CDG lyric frame.
/// The CDG engine owns packet decoding; this layer only caches the current
/// 300x216-ish ARGB overlay and tracks whether the GPU texture needs refresh.
/// </summary>
internal sealed class CdgLyricsLayer
{
    private int[]? _argb;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsGpuUploadDirty { get; private set; } = true;

    public bool HasFrame => _argb != null && Width > 0 && Height > 0;

    public void SetFrame(ReadOnlySpan<int> sourceArgb, int width, int height)
    {
        if (width <= 0 || height <= 0 || sourceArgb.IsEmpty)
        {
            Clear();
            return;
        }

        var count = checked(width * height);
        if (sourceArgb.Length < count)
        {
            Clear();
            return;
        }

        if (_argb == null || _argb.Length != count)
        {
            _argb = new int[count];
        }

        sourceArgb.Slice(0, count).CopyTo(_argb);
        Width = width;
        Height = height;
        IsGpuUploadDirty = true;
    }

    public bool TryGetFrame(out int[] argb, out int width, out int height)
    {
        if (_argb != null && Width > 0 && Height > 0)
        {
            argb = _argb;
            width = Width;
            height = Height;
            return true;
        }

        argb = [];
        width = 0;
        height = 0;
        return false;
    }

    public void MarkGpuUploadClean() => IsGpuUploadDirty = false;

    public void Clear()
    {
        _argb = null;
        Width = 0;
        Height = 0;
        IsGpuUploadDirty = true;
    }
}
