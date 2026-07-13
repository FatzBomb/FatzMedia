using Kermalis.DLS2;
using Kermalis.SoundFont2;

namespace FatzBombMedia.MediaControl.Core.Audio;

/// <summary>
/// Converts a DLS soundfont file to SF2 format so BASS MIDI can use it.
/// BASS MIDI only supports SF2/SFZ; DLS (including Windows gm.dls) must be converted.
/// The output is cached as {original_name}.sf2 in LocalAppData.
/// </summary>
internal static class DlsToSf2Converter
{
    /// <summary>
    /// Converts a DLS file to SF2 and returns the path to the converted file.
    /// If a cached conversion already exists, returns that directly.
    /// </summary>
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
            DLS dls;
            try
            {
                dls = new DLS(dlsPath);
            }
            catch (UnauthorizedAccessException)
            {
                tempCopy = Path.Combine(Path.GetTempPath(), "FatzBombMedia", Path.GetFileName(dlsPath));
                var tempDir = Path.GetDirectoryName(tempCopy);
                if (tempDir != null)
                    _ = Directory.CreateDirectory(tempDir);
                File.Copy(dlsPath, tempCopy, overwrite: true);
                dls = new DLS(tempCopy);
            }

            return ConvertAndSave(dls, cachePath, dlsPath);
        }
        catch (Exception ex)
        {
            DiagnosticRouter.Raise(MediaDiagnosticLevel.Warning, "BASSMIDI", $"DLS to SF2 conversion failed: {ex.Message}");
            if (File.Exists(cachePath))
            {
                try { File.Delete(cachePath); } catch { }
            }
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

    private static string? ConvertAndSave(DLS dls, string cachePath, string sourcePath)
    {
        var sf2 = new SF2();

        sf2.InfoChunk.Engine = "Converted from DLS";
        sf2.InfoChunk.Bank = Path.GetFileNameWithoutExtension(sourcePath);

        ListChunk wavePool = dls.WavePool;
        var sampleIndexMap = new Dictionary<uint, uint>();

        for (uint i = 0; i < wavePool.Count; i++)
        {
            if (wavePool[(int)i] is not ListChunk waveList || waveList.Identifier != "wave")
                continue;

            var pcm16 = ExtractPcm16Samples(waveList, out var sampleRate, out var unityNote, out var fineTune, out var loopStart, out var loopLength);
            if (pcm16 == null || pcm16.Length == 0)
                continue;

            var name = $"Sample{i}";
            var hasLoop = loopLength > 0;
            var sampleIdx = sf2.AddSample(pcm16, name, hasLoop, hasLoop ? loopStart : 0, sampleRate, (byte)unityNote, (sbyte)fineTune);
            sampleIndexMap[i] = sampleIdx;
        }

        ListChunk instrumentList = dls.InstrumentList;
        for (var instIdx = 0; instIdx < instrumentList.Count; instIdx++)
        {
            if (instrumentList[instIdx] is not ListChunk instList || instList.Identifier != "ins ")
                continue;

            InstrumentHeaderChunk? header = FindChunk<InstrumentHeaderChunk>(instList);
            if (header == null)
                continue;

            MIDILocale locale = header.Locale;
            var bank = (ushort)(locale.IsDrum ? 128 : locale.CC0);
            var preset = locale.Instrument;

            var instName = $"Inst{instIdx}";
            var sf2InstIdx = sf2.AddInstrument(instName);

            List<ListChunk> regions = GetRegions(instList);
            foreach (ListChunk region in regions)
            {
                RegionHeaderChunk? rgnh = FindChunk<RegionHeaderChunk>(region);
                WaveLinkChunk? wlnk = FindChunk<WaveLinkChunk>(region);
                WaveSampleChunk? wsmp = FindChunk<WaveSampleChunk>(region);

                if (rgnh == null || wlnk == null)
                    continue;

                if (!sampleIndexMap.TryGetValue(wlnk.TableIndex, out var sampleId))
                    continue;

                sf2.AddInstrumentBag();

                sf2.AddInstrumentGenerator(SF2Generator.KeyRange, new SF2GeneratorAmount
                {
                    LowByte = (byte)rgnh.KeyRange.Low,
                    HighByte = (byte)rgnh.KeyRange.High
                });

                sf2.AddInstrumentGenerator(SF2Generator.VelRange, new SF2GeneratorAmount
                {
                    LowByte = (byte)rgnh.VelocityRange.Low,
                    HighByte = (byte)rgnh.VelocityRange.High
                });

                if (wsmp != null)
                {
                    sf2.AddInstrumentGenerator(SF2Generator.OverridingRootKey, new SF2GeneratorAmount { Amount = (short)wsmp.UnityNote });
                    if (wsmp.FineTune != 0)
                        sf2.AddInstrumentGenerator(SF2Generator.FineTune, new SF2GeneratorAmount { Amount = wsmp.FineTune });
                }

                sf2.AddInstrumentGenerator(SF2Generator.SampleID, new SF2GeneratorAmount { Amount = (short)sampleId });
                sf2.AddInstrumentModulator();
            }

            sf2.AddInstrumentBag();
            sf2.AddInstrumentGenerator();
            sf2.AddInstrumentModulator();

            sf2.AddPreset(instName, preset, bank);
            sf2.AddPresetBag();
            sf2.AddPresetGenerator(SF2Generator.Instrument, new SF2GeneratorAmount { Amount = (short)sf2InstIdx });
            sf2.AddPresetModulator();
        }

        var cacheDir = Path.GetDirectoryName(cachePath);
        if (cacheDir != null)
            _ = Directory.CreateDirectory(cacheDir);

        sf2.Save(cachePath);

        DiagnosticRouter.Raise(MediaDiagnosticLevel.Info, "BASSMIDI", $"DLS to SF2 conversion complete: {cachePath}");
        return cachePath;
    }

    private static string GetCachePath(string dlsPath)
    {
        var fileName = Path.GetFileName(dlsPath) + ".sf2";
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FatzBombMedia", "soundfonts");

        return Path.Combine(cacheDir, fileName);
    }

    private static short[]? ExtractPcm16Samples(ListChunk waveList, out uint sampleRate, out ushort unityNote, out short fineTune, out uint loopStart, out uint loopLength)
    {
        sampleRate = 44100;
        unityNote = 60;
        fineTune = 0;
        loopStart = 0;
        loopLength = 0;

        FormatChunk? fmt = FindChunk<FormatChunk>(waveList);
        DataChunk? data = FindChunk<DataChunk>(waveList);
        WaveSampleChunk? wsmp = FindChunk<WaveSampleChunk>(waveList);

        if (fmt == null || data == null)
            return null;

        sampleRate = fmt.WaveInfo.SamplesPerSec;
        var bitsPerSample = fmt.FormatInfo.BitsPerSample;
        var channels = fmt.WaveInfo.Channels;

        if (bitsPerSample != 16 || channels > 1)
            return null;

        var rawData = data.Data;
        var sampleCount = rawData.Length / 2;
        var samples = new short[sampleCount];
        Buffer.BlockCopy(rawData, 0, samples, 0, rawData.Length);

        if (wsmp != null)
        {
            unityNote = wsmp.UnityNote;
            fineTune = wsmp.FineTune;
            if (wsmp.Loop != null)
            {
                loopStart = wsmp.Loop.LoopStart;
                loopLength = wsmp.Loop.LoopLength;
            }
        }

        return samples;
    }

    private static List<ListChunk> GetRegions(ListChunk instrumentList)
    {
        var regions = new List<ListChunk>();
        foreach (DLSChunk child in instrumentList)
        {
            if (child is ListChunk lc && lc.Identifier == "lrgn")
            {
                foreach (DLSChunk region in lc)
                {
                    if (region is ListChunk rc && rc.Identifier == "rgn ")
                        regions.Add(rc);
                }
            }
        }
        return regions;
    }

    private static T? FindChunk<T>(IReadOnlyList<DLSChunk> chunks) where T : DLSChunk
    {
        foreach (DLSChunk chunk in chunks)
        {
            if (chunk is T typed)
                return typed;
        }
        return null;
    }
}
