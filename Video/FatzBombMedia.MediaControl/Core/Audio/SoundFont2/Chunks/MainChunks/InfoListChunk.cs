using Kermalis.EndianBinaryIO;

namespace Kermalis.SoundFont2;

public sealed class InfoListChunk : SF2ListChunk
{
    private const string DEFAULT_ENGINE = "EMU8000";
    private const string DEFAULT_BANK = "General MIDI";
    private const int COMMENT_MAX_SIZE = 0x10000;

    private readonly List<SF2Chunk> _subChunks;
    public string Engine
    {
        get
        {
            if (_subChunks.Find(s => s.ChunkName == "isng") is HeaderSubChunk chunk)
                return chunk.Field;
            _subChunks.Add(new HeaderSubChunk(_sf2, "isng", DEFAULT_ENGINE));
            return DEFAULT_ENGINE;
        }
        set
        {
            if (_subChunks.Find(s => s.ChunkName == "isng") is HeaderSubChunk chunk)
                chunk.Field = value;
            else
                _subChunks.Add(new HeaderSubChunk(_sf2, "isng", value));
        }
    }
    public string Bank
    {
        get
        {
            if (_subChunks.Find(s => s.ChunkName == "INAM") is HeaderSubChunk chunk)
                return chunk.Field;
            _subChunks.Add(new HeaderSubChunk(_sf2, "INAM", DEFAULT_BANK));
            return DEFAULT_BANK;
        }
        set
        {
            if (_subChunks.Find(s => s.ChunkName == "INAM") is HeaderSubChunk chunk)
                chunk.Field = value;
            else
                _subChunks.Add(new HeaderSubChunk(_sf2, "INAM", value));
        }
    }

    public string? ROM
    {
        get => _subChunks.Find(s => s.ChunkName == "irom") is HeaderSubChunk chunk ? chunk.Field : null;
        set
        {
            var chunk = _subChunks.Find(s => s.ChunkName == "irom") as HeaderSubChunk;
            if (value is not null)
                if (chunk is not null)
                    chunk.Field = value;
                else
                    _subChunks.Add(new HeaderSubChunk(_sf2, "irom", value));
            else
                if (chunk is not null)
                    _ = _subChunks.Remove(chunk);
        }
    }
    public SF2VersionTag? ROMVersion
    {
        get => _subChunks.Find(s => s.ChunkName == "iver") is VersionSubChunk chunk ? chunk.Version : null;
        set
        {
            var chunk = _subChunks.Find(s => s.ChunkName == "iver") as VersionSubChunk;
            if (value is not null)
                if (chunk is not null)
                    chunk.Version = value;
                else
                    _subChunks.Add(new VersionSubChunk(_sf2, "iver", value));
            else
                if (chunk is not null)
                    _ = _subChunks.Remove(chunk);
        }
    }
    public string? Date
    {
        get => _subChunks.Find(s => s.ChunkName == "ICRD") is HeaderSubChunk chunk ? chunk.Field : null;
        set
        {
            var chunk = _subChunks.Find(s => s.ChunkName == "ICRD") as HeaderSubChunk;
            if (value is not null)
                if (chunk is not null)
                    chunk.Field = value;
                else
                    _subChunks.Add(new HeaderSubChunk(_sf2, "ICRD", value));
            else
                if (chunk is not null)
                    _ = _subChunks.Remove(chunk);
        }
    }
    public string? Designer
    {
        get => _subChunks.Find(s => s.ChunkName == "IENG") is HeaderSubChunk chunk ? chunk.Field : null;
        set
        {
            var chunk = _subChunks.Find(s => s.ChunkName == "IENG") as HeaderSubChunk;
            if (value is not null)
                if (chunk is not null)
                    chunk.Field = value;
                else
                    _subChunks.Add(new HeaderSubChunk(_sf2, "IENG", value));
            else
                if (chunk is not null)
                    _ = _subChunks.Remove(chunk);
        }
    }
    public string? Products
    {
        get => _subChunks.Find(s => s.ChunkName == "IPRD") is HeaderSubChunk chunk ? chunk.Field : null;
        set
        {
            var chunk = _subChunks.Find(s => s.ChunkName == "IPRD") as HeaderSubChunk;
            if (value is not null)
                if (chunk is not null)
                    chunk.Field = value;
                else
                    _subChunks.Add(new HeaderSubChunk(_sf2, "IPRD", value));
            else
                if (chunk is not null)
                    _ = _subChunks.Remove(chunk);
        }
    }
    public string? Copyright
    {
        get => _subChunks.Find(s => s.ChunkName == "ICOP") is HeaderSubChunk icop ? icop.Field : null;
        set
        {
            var chunk = _subChunks.Find(s => s.ChunkName == "ICOP") as HeaderSubChunk;
            if (value is not null)
                if (chunk is not null)
                    chunk.Field = value;
                else
                    _subChunks.Add(new HeaderSubChunk(_sf2, "ICOP", value));
            else
                if (chunk is not null)
                    _ = _subChunks.Remove(chunk);
        }
    }

    public string? Comment
    {
        get => _subChunks.Find(s => s.ChunkName == "ICMT") is HeaderSubChunk chunk ? chunk.Field : null;
        set
        {
            var chunk = _subChunks.Find(s => s.ChunkName == "ICMT") as HeaderSubChunk;
            if (value is not null)
                if (chunk is not null)
                    chunk.Field = value;
                else
                    _subChunks.Add(new HeaderSubChunk(_sf2, "ICMT", value, maxSize: COMMENT_MAX_SIZE));
            else
                if (chunk is not null)
                    _ = _subChunks.Remove(chunk);
        }
    }
    public string? Tools
    {
        get => _subChunks.Find(s => s.ChunkName == "ISFT") is HeaderSubChunk chunk ? chunk.Field : null;
        set
        {
            var chunk = _subChunks.Find(s => s.ChunkName == "ISFT") as HeaderSubChunk;
            if (value is not null)
                if (chunk is not null)
                    chunk.Field = value;
                else
                    _subChunks.Add(new HeaderSubChunk(_sf2, "ISFT", value));
            else
                if (chunk is not null)
                    _ = _subChunks.Remove(chunk);
        }
    }

    internal InfoListChunk(SF2 inSf2) : base(inSf2, "INFO")
    {
        _subChunks =
        [
				// Mandatory sub-chunks
				new VersionSubChunk(inSf2, "ifil", new SF2VersionTag(2, 1)),
            new HeaderSubChunk(inSf2, "isng", DEFAULT_ENGINE),
            new HeaderSubChunk(inSf2, "INAM", DEFAULT_BANK),
        ];
        inSf2.UpdateSize();
    }
    internal InfoListChunk(SF2 inSf2, EndianBinaryReader reader) : base(inSf2, reader)
    {
        _subChunks = [];
        var startOffset = reader.Stream.Position;
        while (reader.Stream.Position < startOffset + Size - 4) // The 4 represents the INFO that was already read
        {
            // Peek 4 chars for the chunk name
            var name = reader.ReadString_Count(4);
            reader.Stream.Position -= 4;
            switch (name)
            {
                case "ICMT": _subChunks.Add(new HeaderSubChunk(inSf2, reader, maxSize: COMMENT_MAX_SIZE)); break;
                case "ifil":
                case "iver": _subChunks.Add(new VersionSubChunk(inSf2, reader)); break;
                case "isng":
                case "INAM":
                case "ICRD":
                case "IENG":
                case "IPRD":
                case "ICOP":
                case "ISFT":
                case "irom": _subChunks.Add(new HeaderSubChunk(inSf2, reader)); break;
                default: throw new NotSupportedException($"Unsupported chunk name at 0x{reader.Stream.Position:X}: \"{name}\"");
            }
        }
    }

    internal override uint UpdateSize()
    {
        Size = 4;
        foreach (SF2Chunk sub in _subChunks)
            Size += sub.Size + 8;

        return Size;
    }

    internal override void Write(EndianBinaryWriter writer)
    {
        base.Write(writer);
        foreach (SF2Chunk sub in _subChunks)
            sub.Write(writer);
    }

    public override string ToString() => $"Info List Chunk - Sub-chunk count = {_subChunks.Count}";
}
