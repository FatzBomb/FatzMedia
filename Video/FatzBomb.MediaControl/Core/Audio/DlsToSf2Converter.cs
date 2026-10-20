using System.Text;

namespace FatzBomb.MediaControl.Core.Audio;

/// <summary>
/// Converts a DLS soundfont file to SF2 format so BASS MIDI can use it.
/// BASS MIDI only supports SF2/SFZ; DLS (including Windows gm.dls) must be converted.
/// The output is cached as {original_name}.sf2 in LocalAppData.
///
/// This is a self-contained binary reader/writer — no external DLS2/SF2 library needed.
/// It reads only the DLS chunks required for GM conversion and writes a minimal valid SF2.
/// </summary>
internal static class DlsToSf2Converter
{
    public static string? Convert(string dlsPath)
    {
        if (!File.Exists(dlsPath))
            return null;

        var cachePath = GetCachePath(dlsPath);
        if (File.Exists(cachePath))
            return cachePath;

        string? tempCopy = null;
        try
        {
            var readPath = dlsPath;
            try
            {
                using FileStream testStream = File.OpenRead(dlsPath);
            }
            catch (UnauthorizedAccessException)
            {
                tempCopy = Path.Combine(Path.GetTempPath(), "FatzBombMedia", Path.GetFileName(dlsPath));
                var tempDir = Path.GetDirectoryName(tempCopy);
                if (tempDir != null)
                    _ = Directory.CreateDirectory(tempDir);
                File.Copy(dlsPath, tempCopy, overwrite: true);
                readPath = tempCopy;
            }

            return ConvertAndSave(readPath, cachePath);
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "BASSMIDI", $"DLS to SF2 conversion failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (tempCopy != null && File.Exists(tempCopy))
            {
                try { File.Delete(tempCopy); } catch { }
            }
        }
    }

    private static string GetCachePath(string dlsPath)
    {
        // Keyed on the name alone, as before: an SF2 converted by an earlier version keeps being
        // used. (Keying on size and write time would convert Windows' gm.dls again.)
        var fileName = Path.GetFileName(dlsPath) + ".sf2";
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FatzBombMedia", "soundfonts");
        return Path.Combine(cacheDir, fileName);
    }

    // ─── DLS reading ──────────────────────────────────────────────

    private sealed class DlsReader(BinaryReader reader)
    {
        private readonly BinaryReader _reader = reader;

        public string ReadFourCC() => Encoding.ASCII.GetString(_reader.ReadBytes(4));
        public uint ReadUInt32() => _reader.ReadUInt32();
        public ushort ReadUInt16() => _reader.ReadUInt16();
        public short ReadInt16() => _reader.ReadInt16();
        public byte ReadByte() => _reader.ReadByte();
        public sbyte ReadSByte() => _reader.ReadSByte();
        public byte[] ReadBytes(int count) => _reader.ReadBytes(count);
        public long Position => _reader.BaseStream.Position;
        public void Seek(long offset) => _reader.BaseStream.Position = offset;
        public int ReadInt32() => _reader.ReadInt32();

        public string ReadPaddedString(int length)
        {
            var bytes = _reader.ReadBytes(length);
            var sb = new StringBuilder(length);
            foreach (var b in bytes)
            {
                if (b == 0) break;
                _ = sb.Append((char)b);
            }
            return sb.ToString();
        }

        /// <summary>Moves past a chunk's data and its pad byte (RIFF chunks are word aligned).</summary>
        public void SkipToChunkEnd(long chunkStart, uint chunkSize) =>
            Seek(chunkStart + chunkSize + (chunkSize & 1));
    }

    private sealed record DlsSample(short[] Pcm16, uint SampleRate, ushort UnityNote, short FineTune, uint LoopStart, uint LoopLength);

    private sealed record DlsRegion(byte KeyLow, byte KeyHigh, byte VelLow, byte VelHigh, int SampleIndex, ushort UnityNote, short FineTune, uint LoopStart, uint LoopLength);

    private sealed record DlsInstrument(ushort Bank, ushort Preset, List<DlsRegion> Regions);

    internal static string? ConvertAndSave(string dlsPath, string cachePath)
    {
        using FileStream stream = File.OpenRead(dlsPath);
        var reader = new BinaryReader(stream, Encoding.ASCII);
        var dls = new DlsReader(reader);

        if (dls.ReadFourCC() != "RIFF")
            throw new InvalidDataException("RIFF header not found.");
        var riffSize = dls.ReadUInt32();
        var riffEnd = dls.Position + riffSize;
        if (dls.ReadFourCC() != "DLS ")
            throw new InvalidDataException("DLS type not found.");

        // Parse top-level chunks to find lins and wvpl
        (long Offset, uint Size)? instrumentListInfo = null;
        (long Offset, uint Size)? wavePoolInfo = null;

        while (dls.Position < riffEnd)
        {
            var chunkName = dls.ReadFourCC();
            var chunkSize = dls.ReadUInt32();
            var chunkStart = dls.Position;

            if (chunkName == "LIST")
            {
                var listType = dls.ReadFourCC();
                if (listType == "lins")
                    instrumentListInfo = (chunkStart, chunkSize);
                else if (listType == "wvpl")
                    wavePoolInfo = (chunkStart, chunkSize);
            }

            // Skip to end of chunk (with padding)
            dls.SkipToChunkEnd(chunkStart, chunkSize);
        }

        if (instrumentListInfo == null || wavePoolInfo == null)
            throw new InvalidDataException("Required DLS chunks (lins, wvpl) not found.");

        // Parse wave pool entries
        var samples = new List<DlsSample?>();
        var sampleIndexMap = new Dictionary<uint, int>();

        dls.Seek(wavePoolInfo.Value.Offset + 4); // skip past "wvpl"
        var wavePoolEnd = wavePoolInfo.Value.Offset + wavePoolInfo.Value.Size;
        uint waveIndex = 0;
        while (dls.Position < wavePoolEnd)
        {
            var chunkName = dls.ReadFourCC();
            var chunkSize = dls.ReadUInt32();
            var chunkStart = dls.Position;

            if (chunkName == "LIST")
            {
                var listType = dls.ReadFourCC();
                if (listType == "wave")
                {
                    DlsSample? sample = ParseWaveChunk(dls, chunkStart + chunkSize);
                    if (sample != null)
                    {
                        sampleIndexMap[waveIndex] = samples.Count;
                        samples.Add(sample);
                    }
                    else
                    {
                        samples.Add(null);
                    }
                }
            }

            waveIndex++;
            dls.SkipToChunkEnd(chunkStart, chunkSize);
        }

        // Parse instruments
        var instruments = new List<DlsInstrument>();
        dls.Seek(instrumentListInfo.Value.Offset + 4); // skip past "lins"
        var insListEnd = instrumentListInfo.Value.Offset + instrumentListInfo.Value.Size;
        while (dls.Position < insListEnd)
        {
            var chunkName = dls.ReadFourCC();
            var chunkSize = dls.ReadUInt32();
            var chunkStart = dls.Position;

            if (chunkName == "LIST")
            {
                var listType = dls.ReadFourCC();
                if (listType == "ins ")
                {
                    DlsInstrument? inst = ParseInstrumentChunk(dls, chunkStart + chunkSize, sampleIndexMap);
                    if (inst != null)
                        instruments.Add(inst);
                }
            }

            dls.SkipToChunkEnd(chunkStart, chunkSize);
        }

        // Write SF2
        var cacheDir = Path.GetDirectoryName(cachePath);
        if (cacheDir != null)
            _ = Directory.CreateDirectory(cacheDir);

        // Written beside the cache file and moved into place, so a crash mid-write never leaves
        // a truncated SF2 that later runs would load as the cache.
        var partialPath = $"{cachePath}.{Guid.NewGuid():N}.partial"; // one per writer: two processes may convert at once
        try
        {
            WriteSf2(partialPath, samples, instruments, Path.GetFileNameWithoutExtension(dlsPath));
            File.Move(partialPath, cachePath, overwrite: true);
        }
        catch
        {
            try { File.Delete(partialPath); } catch { }
            throw;
        }

        DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "BASSMIDI", $"DLS to SF2 conversion complete: {cachePath}");
        return cachePath;
    }

    private static DlsSample? ParseWaveChunk(DlsReader dls, long endOffset)
    {
        uint sampleRate = 44100;
        ushort unityNote = 60;
        short fineTune = 0;
        uint loopStart = 0;
        uint loopLength = 0;
        short[]? pcm16 = null;

        while (dls.Position < endOffset)
        {
            var chunkName = dls.ReadFourCC();
            var chunkSize = dls.ReadUInt32();
            var chunkStart = dls.Position;

            if (chunkName == "fmt ")
            {
                _ = dls.ReadUInt16();
                var channels = dls.ReadUInt16();
                sampleRate = dls.ReadUInt32();
                _ = dls.ReadUInt32(); // avg bytes/sec
                _ = dls.ReadUInt16(); // block align
                var bitsPerSample = dls.ReadUInt16();

                if (bitsPerSample != 16 || channels > 1)
                    return null;
            }
            else if (chunkName == "data")
            {
                var rawData = dls.ReadBytes((int)chunkSize);
                var sampleCount = rawData.Length / 2;
                pcm16 = new short[sampleCount];
                Buffer.BlockCopy(rawData, 0, pcm16, 0, rawData.Length);
            }
            else if (chunkName == "wsmp")
            {
                _ = dls.ReadUInt32(); // cbSize
                unityNote = dls.ReadUInt16();
                fineTune = dls.ReadInt16();
                _ = dls.ReadInt32(); // lAttenuation
                _ = dls.ReadUInt32(); // fulOptions
                var loopCount = dls.ReadUInt32();
                if (loopCount > 0)
                {
                    _ = dls.ReadUInt32(); // cbSize
                    _ = dls.ReadUInt32(); // ulType
                    loopStart = dls.ReadUInt32();
                    loopLength = dls.ReadUInt32();
                }
            }

            dls.SkipToChunkEnd(chunkStart, chunkSize);
        }

        return pcm16 == null || pcm16.Length == 0 ? null : new DlsSample(pcm16, sampleRate, unityNote, fineTune, loopStart, loopLength);
    }

    private static DlsInstrument? ParseInstrumentChunk(DlsReader dls, long endOffset, Dictionary<uint, int> sampleIndexMap)
    {
        ushort bank = 0;
        ushort preset = 0;
        var regions = new List<DlsRegion>();

        while (dls.Position < endOffset)
        {
            var chunkName = dls.ReadFourCC();
            var chunkSize = dls.ReadUInt32();
            var chunkStart = dls.Position;

            if (chunkName == "insh")
            {
                _ = dls.ReadUInt32(); // cRegions
                var bankValue = dls.ReadUInt32();
                var instrumentValue = dls.ReadUInt32();
                bank = (ushort)((bankValue & 0x80000000) != 0 ? 128 : ((bankValue >> 8) & 0x7F));
                preset = (ushort)(instrumentValue & 0x7F);
            }
            else if (chunkName == "LIST")
            {
                var listType = dls.ReadFourCC();
                if (listType == "lrgn")
                {
                    var lrgnEnd = chunkStart + chunkSize;
                    while (dls.Position < lrgnEnd)
                    {
                        var rgnName = dls.ReadFourCC();
                        var rgnSize = dls.ReadUInt32();
                        var rgnStart = dls.Position;

                        if (rgnName == "LIST")
                        {
                            var rgnType = dls.ReadFourCC();
                            if (rgnType == "rgn ")
                            {
                                DlsRegion? region = ParseRegionChunk(dls, rgnStart + rgnSize, sampleIndexMap);
                                if (region != null)
                                    regions.Add(region);
                            }
                        }

                        dls.SkipToChunkEnd(rgnStart, rgnSize);
                    }
                }
            }

            dls.SkipToChunkEnd(chunkStart, chunkSize);
        }

        return regions.Count == 0 ? null : new DlsInstrument(bank, preset, regions);
    }

    private static DlsRegion? ParseRegionChunk(DlsReader dls, long endOffset, Dictionary<uint, int> sampleIndexMap)
    {
        byte keyLow = 0, keyHigh = 127;
        byte velLow = 0, velHigh = 127;
        uint tableIndex = 0;
        ushort unityNote = 60;
        short fineTune = 0;
        uint loopStart = 0;
        uint loopLength = 0;

        while (dls.Position < endOffset)
        {
            var chunkName = dls.ReadFourCC();
            var chunkSize = dls.ReadUInt32();
            var chunkStart = dls.Position;

            if (chunkName == "rgnh")
            {
                keyLow = (byte)dls.ReadUInt16();
                keyHigh = (byte)dls.ReadUInt16();
                velLow = (byte)dls.ReadUInt16();
                velHigh = (byte)dls.ReadUInt16();
                _ = dls.ReadUInt16(); // fusOptions
                _ = dls.ReadUInt16(); // usKeyGroup
            }
            else if (chunkName == "wlnk")
            {
                _ = dls.ReadUInt16(); // fusOptions
                _ = dls.ReadUInt16(); // usPhaseGroup
                _ = dls.ReadUInt32(); // ulChannel
                tableIndex = dls.ReadUInt32(); // ulTableIndex
            }
            else if (chunkName == "wsmp")
            {
                _ = dls.ReadUInt32(); // cbSize
                unityNote = dls.ReadUInt16();
                fineTune = dls.ReadInt16();
                _ = dls.ReadInt32(); // lAttenuation
                _ = dls.ReadUInt32(); // fulOptions
                var loopCount = dls.ReadUInt32();
                if (loopCount > 0)
                {
                    _ = dls.ReadUInt32(); // cbSize
                    _ = dls.ReadUInt32(); // ulType
                    loopStart = dls.ReadUInt32();
                    loopLength = dls.ReadUInt32();
                }
            }

            dls.SkipToChunkEnd(chunkStart, chunkSize);
        }

        // The region names a wave by its pool position; samples holds it at the mapped index.
        return !sampleIndexMap.TryGetValue(tableIndex, out var sampleIndex)
            ? null
            : new DlsRegion(keyLow, keyHigh, velLow, velHigh, sampleIndex, unityNote, fineTune, loopStart, loopLength);
    }

    // ─── SF2 writing ─────────────────────────────────────────────

    private static void WriteSf2(string path, List<DlsSample?> samples, List<DlsInstrument> instruments, string bankName)
    {
        // Build SF2 in memory as lists, then serialize
        var smplData = new List<short>();
        var shdrEntries = new List<(string Name, uint Start, uint End, uint LoopStart, uint LoopEnd, uint SampleRate, byte OriginalKey, sbyte PitchCorrection)>();
        var phdrEntries = new List<(string Name, ushort Preset, ushort Bank, ushort BagIndex)>();
        var pbagEntries = new List<(ushort GenIndex, ushort ModIndex)>();
        var pgenEntries = new List<(ushort Gen, short Amount)>();
        var pmodEntries = new List<(ushort Src, ushort Dest, short Amount, ushort SrcAmt, ushort Transform)>();
        var instEntries = new List<(string Name, ushort BagIndex)>();
        var ibagEntries = new List<(ushort GenIndex, ushort ModIndex)>();
        var igenEntries = new List<(ushort Gen, short Amount)>();
        var imodEntries = new List<(ushort Src, ushort Dest, short Amount, ushort SrcAmt, ushort Transform)>();

        // Add samples. Unconvertible waves (null) get no shdr entry, so each sample's SF2 index
        // is recorded for the instruments' SampleID generators.
        var shdrIndexBySample = new int[samples.Count];
        for (var i = 0; i < samples.Count; i++)
        {
            if (samples[i] is not DlsSample sample)
                continue;

            shdrIndexBySample[i] = shdrEntries.Count;

            var start = (uint)smplData.Count;
            smplData.AddRange(sample.Pcm16);

            uint end, loopStart, loopEnd;
            if (sample.LoopLength > 0 && (ulong)sample.LoopStart + sample.LoopLength <= (ulong)sample.Pcm16.Length)
            {
                // SF2 requires 8 samples from loop start appended after the sample
                for (var j = 0; j < 8; j++)
                    smplData.Add(sample.Pcm16[(int)(sample.LoopStart + (j % sample.LoopLength))]);
                end = (uint)smplData.Count + 46; // 46 zero samples at end
                loopStart = start + sample.LoopStart;
                loopEnd = start + sample.LoopStart + sample.LoopLength;
            }
            else
            {
                end = start + (uint)sample.Pcm16.Length + 46;
                loopStart = 0;
                loopEnd = 0;
            }

            // Add 46 zero samples at end
            for (var j = 0; j < 46; j++)
                smplData.Add(0);

            shdrEntries.Add(($"Sample{i}", start, end, loopStart, loopEnd, sample.SampleRate, (byte)sample.UnityNote, (sbyte)sample.FineTune));
        }

        // Add instruments and presets
        ushort instIdx = 0;
        foreach (DlsInstrument inst in instruments)
        {
            var instName = $"Inst{instIdx}";
            var instBagIndex = (ushort)ibagEntries.Count;

            foreach (DlsRegion region in inst.Regions)
            {
                if (samples[region.SampleIndex] is null)
                    continue;

                var genIndex = (ushort)igenEntries.Count;
                var modIndex = (ushort)imodEntries.Count;

                igenEntries.Add((43, (short)((region.KeyHigh << 8) | region.KeyLow))); // KeyRange
                igenEntries.Add((44, (short)((region.VelHigh << 8) | region.VelLow))); // VelRange
                igenEntries.Add((58, (short)region.UnityNote)); // OverridingRootKey
                if (region.FineTune != 0)
                    igenEntries.Add((52, region.FineTune)); // FineTune
                igenEntries.Add((53, (short)shdrIndexBySample[region.SampleIndex])); // SampleID
                imodEntries.Add((0, 0, 0, 0, 0)); // terminal modulator

                ibagEntries.Add((genIndex, modIndex));
            }

            // Terminal bag
            ibagEntries.Add(((ushort)igenEntries.Count, (ushort)imodEntries.Count));
            igenEntries.Add((60, 0)); // EndOper
            imodEntries.Add((0, 0, 0, 0, 0));

            instEntries.Add((instName, instBagIndex));

            // Preset
            var presetBagIndex = (ushort)pbagEntries.Count;
            pbagEntries.Add(((ushort)pgenEntries.Count, (ushort)pmodEntries.Count));
            pgenEntries.Add((41, (short)instIdx)); // Instrument
            pmodEntries.Add((0, 0, 0, 0, 0));
            phdrEntries.Add((instName, inst.Preset, inst.Bank, presetBagIndex));

            instIdx++;
        }

        // Terminal entries
        var eosSampleStart = (uint)smplData.Count;
        shdrEntries.Add(("EOS", eosSampleStart, eosSampleStart, 0, 0, 0, 0, 0));
        instEntries.Add(("EOI", (ushort)ibagEntries.Count));
        ibagEntries.Add(((ushort)igenEntries.Count, (ushort)imodEntries.Count));
        igenEntries.Add((60, 0));
        imodEntries.Add((0, 0, 0, 0, 0));
        phdrEntries.Add(("EOP", 0xFF, 0xFF, (ushort)pbagEntries.Count));
        pbagEntries.Add(((ushort)pgenEntries.Count, (ushort)pmodEntries.Count));
        pgenEntries.Add((60, 0));
        pmodEntries.Add((0, 0, 0, 0, 0));

        // Serialize to file
        using FileStream outStream = File.Create(path);
        using var writer = new BinaryWriter(outStream, Encoding.ASCII);

        // Build INFO LIST
        var infoData = new MemoryStream();
        WriteInfoChunk(infoData, "ifil", [2, 1, 0, 0]); // version 2.1 little-endian
        WriteInfoString(infoData, "isng", "Converted from DLS");
        WriteInfoString(infoData, "INAM", bankName);
        var infoBytes = infoData.ToArray();

        // Build sdta LIST
        var sdtaData = new MemoryStream();
        var smplBytes = new byte[smplData.Count * 2];
        Buffer.BlockCopy(smplData.ToArray(), 0, smplBytes, 0, smplBytes.Length);
        WriteChunk(sdtaData, "smpl", smplBytes);
        var sdtaBytes = sdtaData.ToArray();

        // Build pdta LIST
        var pdtaData = new MemoryStream();
        WritePhdrChunk(pdtaData, phdrEntries);
        WriteBagChunk(pdtaData, "pbag", pbagEntries);
        WriteModChunk(pdtaData, "pmod", pmodEntries);
        WriteGenChunk(pdtaData, "pgen", pgenEntries);
        WriteInstChunk(pdtaData, instEntries);
        WriteBagChunk(pdtaData, "ibag", ibagEntries);
        WriteModChunk(pdtaData, "imod", imodEntries);
        WriteGenChunk(pdtaData, "igen", igenEntries);
        WriteShdrChunk(pdtaData, shdrEntries);
        var pdtaBytes = pdtaData.ToArray();

        // Write RIFF container
        var riffPayloadSize = checked((uint)(
            4 // "sfbk"
            + 12 + infoBytes.Length // LIST + size + type + INFO data
            + 12 + sdtaBytes.Length
            + 12 + pdtaBytes.Length));

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(riffPayloadSize);
        writer.Write(Encoding.ASCII.GetBytes("sfbk"));
        WriteListChunk(writer, "INFO", infoBytes);
        WriteListChunk(writer, "sdta", sdtaBytes);
        WriteListChunk(writer, "pdta", pdtaBytes);
    }

    private static void WriteInfoChunk(MemoryStream ms, string name, byte[] data)
    {
        ms.Write(Encoding.ASCII.GetBytes(name));
        ms.Write(BitConverter.GetBytes((uint)data.Length));
        ms.Write(data);
        if (data.Length % 2 != 0)
            ms.WriteByte(0);
    }

    private static void WriteInfoString(MemoryStream ms, string name, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value + '\0');
        if (bytes.Length % 2 != 0)
            Array.Resize(ref bytes, bytes.Length + 1);
        WriteInfoChunk(ms, name, bytes);
    }

    private static void WriteChunk(MemoryStream ms, string name, byte[] data)
    {
        ms.Write(Encoding.ASCII.GetBytes(name));
        ms.Write(BitConverter.GetBytes((uint)data.Length));
        ms.Write(data);
        if (data.Length % 2 != 0)
            ms.WriteByte(0);
    }

    private static void WriteListChunk(BinaryWriter writer, string listType, byte[] data)
    {
        writer.Write(Encoding.ASCII.GetBytes("LIST"));
        writer.Write((uint)(4 + data.Length));
        writer.Write(Encoding.ASCII.GetBytes(listType));
        writer.Write(data);
    }

    private static void WritePhdrChunk(MemoryStream ms, List<(string Name, ushort Preset, ushort Bank, ushort BagIndex)> entries)
    {
        using var chunkMs = new MemoryStream();
        using var bw = new BinaryWriter(chunkMs, Encoding.ASCII);
        foreach ((var Name, var Preset, var Bank, var BagIndex) in entries)
        {
            var nameBytes = new byte[20];
            var src = Encoding.ASCII.GetBytes(Name);
            Array.Copy(src, nameBytes, Math.Min(src.Length, 20));
            bw.Write(nameBytes);
            bw.Write(Preset);
            bw.Write(Bank);
            bw.Write(BagIndex);
            bw.Write(0u); // library
            bw.Write(0u); // genre
            bw.Write(0u); // morphology
        }
        WriteChunk(ms, "phdr", chunkMs.ToArray());
    }

    private static void WriteBagChunk(MemoryStream ms, string name, List<(ushort GenIndex, ushort ModIndex)> entries)
    {
        using var chunkMs = new MemoryStream();
        using var bw = new BinaryWriter(chunkMs, Encoding.ASCII);
        foreach ((var GenIndex, var ModIndex) in entries)
        {
            bw.Write(GenIndex);
            bw.Write(ModIndex);
        }
        WriteChunk(ms, name, chunkMs.ToArray());
    }

    private static void WriteModChunk(MemoryStream ms, string name, List<(ushort Src, ushort Dest, short Amount, ushort SrcAmt, ushort Transform)> entries)
    {
        using var chunkMs = new MemoryStream();
        using var bw = new BinaryWriter(chunkMs, Encoding.ASCII);
        foreach ((var Src, var Dest, var Amount, var SrcAmt, var Transform) in entries)
        {
            bw.Write(Src);
            bw.Write(Dest);
            bw.Write(Amount);
            bw.Write(SrcAmt);
            bw.Write(Transform);
        }
        WriteChunk(ms, name, chunkMs.ToArray());
    }

    private static void WriteGenChunk(MemoryStream ms, string name, List<(ushort Gen, short Amount)> entries)
    {
        using var chunkMs = new MemoryStream();
        using var bw = new BinaryWriter(chunkMs, Encoding.ASCII);
        foreach ((var Gen, var Amount) in entries)
        {
            bw.Write(Gen);
            bw.Write(Amount);
        }
        WriteChunk(ms, name, chunkMs.ToArray());
    }

    private static void WriteInstChunk(MemoryStream ms, List<(string Name, ushort BagIndex)> entries)
    {
        using var chunkMs = new MemoryStream();
        using var bw = new BinaryWriter(chunkMs, Encoding.ASCII);
        foreach ((var Name, var BagIndex) in entries)
        {
            var nameBytes = new byte[20];
            var src = Encoding.ASCII.GetBytes(Name);
            Array.Copy(src, nameBytes, Math.Min(src.Length, 20));
            bw.Write(nameBytes);
            bw.Write(BagIndex);
        }
        WriteChunk(ms, "inst", chunkMs.ToArray());
    }

    private static void WriteShdrChunk(MemoryStream ms, List<(string Name, uint Start, uint End, uint LoopStart, uint LoopEnd, uint SampleRate, byte OriginalKey, sbyte PitchCorrection)> entries)
    {
        using var chunkMs = new MemoryStream();
        using var bw = new BinaryWriter(chunkMs, Encoding.ASCII);
        foreach ((var Name, var Start, var End, var LoopStart, var LoopEnd, var SampleRate, var OriginalKey, var PitchCorrection) in entries)
        {
            var nameBytes = new byte[20];
            var src = Encoding.ASCII.GetBytes(Name);
            Array.Copy(src, nameBytes, Math.Min(src.Length, 20));
            bw.Write(nameBytes);
            bw.Write(Start);
            bw.Write(End);
            bw.Write(LoopStart);
            bw.Write(LoopEnd);
            bw.Write(SampleRate);
            bw.Write(OriginalKey);
            bw.Write(PitchCorrection);
            bw.Write((ushort)0); // sample link
            bw.Write((ushort)1); // mono sample
        }
        WriteChunk(ms, "shdr", chunkMs.ToArray());
    }
}
