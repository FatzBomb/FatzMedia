// FatzAudio: the decoder's share of MACLib.cpp (Monkey's Audio SDK 13.45), copied
// unchanged: CreateIAPEDecompressCore, CreateIAPEDecompressEx2 and the WAV header
// helpers that APEInfo.cpp uses. MACLib.cpp itself is not built, since it also
// holds the encoder and file-conversion functions. CreateIAPEIO returns no I/O
// object: FatzAudio opens .ape files only through its own I/O (src/codec_ape.cpp).
#include "All.h"
#include "MACLib.h"
#include "APEDecompress.h"
#include "APEInfo.h"
#ifdef APE_BACKWARDS_COMPATIBILITY
    #include "Old/APEDecompressOld.h"
#endif

using namespace APE;

IAPEDecompress * CreateIAPEDecompressCore(IAPEInfo * pAPEInfo, int nStartBlock, int nFinishBlock, int * pErrorCode);

/**************************************************************************************************
Functions to create the interfaces
**************************************************************************************************/
IAPEDecompress * CreateIAPEDecompressCore(IAPEInfo * pAPEInfo, int nStartBlock, int nFinishBlock, int * pErrorCode)
{
    // create the decompressor (this eats the CAPEInfo object)
    CSmartPtr<IAPEDecompress> spAPEDecompress;

    // proceed if we have an info object
    if (pAPEInfo != APE_NULL)
    {
        // proceed if there's no error with the info object
        if (*pErrorCode == APE_ERROR_SUCCESS)
        {
            try
            {
                // create
                const int nVersion = static_cast<int>(pAPEInfo->GetInfo(IAPEDecompress::APE_INFO_FILE_VERSION));
                if ((nVersion >= 3990) && (nVersion <= APE_FILE_VERSION_NUMBER))
                    spAPEDecompress.Assign(new CAPEDecompress(pErrorCode, pAPEInfo, nStartBlock, nFinishBlock));
                else if (nVersion == 4110)
                    spAPEDecompress.Assign(new CAPEDecompress(pErrorCode, pAPEInfo, nStartBlock, nFinishBlock)); // a few users have mailed me files with this version -- I don't know where they came from but they verify fine using this code
#ifdef APE_BACKWARDS_COMPATIBILITY
                else if ((nVersion >= 3930) && (nVersion <= APE_FILE_VERSION_NUMBER))
                    spAPEDecompress.Assign(new CAPEDecompress(pErrorCode, pAPEInfo, nStartBlock, nFinishBlock));
                else if (nVersion < 3930)
                    spAPEDecompress.Assign(new CAPEDecompressOld(pErrorCode, pAPEInfo, nStartBlock, nFinishBlock));
#endif
                else
                    *pErrorCode = APE_ERROR_UNSUPPORTED_FILE_VERSION;

                // error check
                if (spAPEDecompress == APE_NULL || *pErrorCode != APE_ERROR_SUCCESS)
                {
                    spAPEDecompress.Delete();
                }
            }
            catch(...)
            {
                spAPEDecompress.Delete();
                *pErrorCode = APE_ERROR_UNDEFINED;
            }
        }
        else
        {
            // eat the CAPEInfo object if we didn't create a decompressor
            APE_SAFE_DELETE(pAPEInfo)
        }
    }

    // return
    spAPEDecompress.SetDelete(false);
    return spAPEDecompress.GetPtr();
}

IAPEDecompress * __stdcall CreateIAPEDecompressEx2(IAPEInfo * pAPEInfo, int nStartBlock, int nFinishBlock, int * pErrorCode)
{
    int nErrorCode = APE_ERROR_SUCCESS;
    IAPEDecompress * pAPEDecompress = CreateIAPEDecompressCore(pAPEInfo, nStartBlock, nFinishBlock, &nErrorCode);
    if (pErrorCode) *pErrorCode = nErrorCode;

    return pAPEDecompress;
}


APE::IAPEIO * __stdcall CreateIAPEIO()
{
    return APE_NULL;
}

int __stdcall FillWaveFormatEx(APE::WAVEFORMATEX * pWaveFormatEx, int nFormatTag, int nSampleRate, int nBitsPerSample, int nChannels)
{
    pWaveFormatEx->cbSize = 0;
    pWaveFormatEx->nSamplesPerSec = static_cast<uint32>(nSampleRate);
    pWaveFormatEx->wBitsPerSample = static_cast<WORD>(nBitsPerSample);
    pWaveFormatEx->nChannels = static_cast<WORD>(nChannels);
    pWaveFormatEx->wFormatTag = static_cast<WORD>(nFormatTag);

    pWaveFormatEx->nBlockAlign = static_cast<WORD>((pWaveFormatEx->wBitsPerSample / 8) * pWaveFormatEx->nChannels);
    pWaveFormatEx->nAvgBytesPerSec = pWaveFormatEx->nBlockAlign * pWaveFormatEx->nSamplesPerSec;

    return APE_ERROR_SUCCESS;
}

int __stdcall FillWaveHeader(WAVE_HEADER * pWAVHeader, APE::int64 nAudioBytes, const APE::WAVEFORMATEX * pWaveFormatEx, APE::intn nTerminatingBytes)
{
    try
    {
        // RIFF header
        memcpy(pWAVHeader->cRIFFHeader, "RIFF", 4);
        pWAVHeader->nRIFFBytes = static_cast<unsigned int>((nAudioBytes + 44) - 8 + nTerminatingBytes);

        // format header
        memcpy(pWAVHeader->cDataTypeID, "WAVE", 4);
        memcpy(pWAVHeader->cFormatHeader, "fmt ", 4);

        // the format chunk is the first 16 bytes of a waveformatex
        pWAVHeader->nFormatBytes = 16;
        memcpy(&pWAVHeader->nFormatTag, pWaveFormatEx, 16);

        // the data header
        memcpy(pWAVHeader->cDataHeader, "data", 4);
        if (nAudioBytes >= 0xFFFFFFFF)
            pWAVHeader->nDataBytes = static_cast<unsigned int>(-1);
        else
            pWAVHeader->nDataBytes = static_cast<unsigned int>(nAudioBytes);

        return APE_ERROR_SUCCESS;
    }
    catch(...) { return APE_ERROR_UNDEFINED; }
}

int __stdcall FillRF64Header(RF64_HEADER * pWAVHeader, APE::int64 nAudioBytes, const APE::WAVEFORMATEX * pWaveFormatEx)
{
    try
    {
        // RIFF header
        memcpy(pWAVHeader->cRIFFHeader, "RF64", 4);
        pWAVHeader->nRIFFBytes = static_cast<unsigned int>(-1); // only used for big files

        // WAV header
        memcpy(pWAVHeader->cDataTypeID, "WAVE", 4);

        // DS64 header
        memcpy(pWAVHeader->cDS64, "ds64", 4);
        pWAVHeader->nDSHeaderSize = static_cast<int>(reinterpret_cast<char *>(&pWAVHeader->cFormatHeader) - reinterpret_cast<char *>(&pWAVHeader->nRIFFSize)); // size between nRIFFSize and nTableLength (cFormatHeader is just after nTableLength)
        pWAVHeader->nRIFFSize = static_cast<int64>(sizeof(RF64_HEADER)) + nAudioBytes - 8; // size of entire ds64 chunk minus the very header
        pWAVHeader->nDataSize = nAudioBytes;
        pWAVHeader->nSampleCount = nAudioBytes / pWaveFormatEx->nBlockAlign; // it's called sample count, but David Bryant puts blocks in WavPack so I'll do the same
        pWAVHeader->nTableLength = 0;

        // format header
        memcpy(pWAVHeader->cFormatHeader, "fmt ", 4);

        // the format chunk is the first 16 bytes of a waveformatex
        pWAVHeader->nFormatBytes = 16;
        memcpy(&pWAVHeader->nFormatTag, pWaveFormatEx, 16);

        // the data header
        memcpy(pWAVHeader->cDataHeader, "data", 4);
        if (nAudioBytes >= 0xFFFFFFFF)
            pWAVHeader->nDataBytes = static_cast<unsigned int>(-1);
        else
            pWAVHeader->nDataBytes = static_cast<unsigned int>(nAudioBytes);

        return APE_ERROR_SUCCESS;
    }
    catch(...) { return APE_ERROR_UNDEFINED; }
}
