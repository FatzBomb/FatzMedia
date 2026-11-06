/* The C side of the vendored Monkey's Audio SDK (see codec_ape.h). Built into the
 * fa_mac static library (third_party/monkeysaudio.cmake) with the SDK's own
 * settings: C++11, exceptions on, FA_MAC_SYNC_DECODE (frames decode on the
 * calling thread, no worker thread per stream). No exception leaves this file. */
#include "All.h"
#include "MACLib.h"
#include "APEInfo.h"
#include "APEDecompress.h"
#include "IAPEIO.h"

#include <new>

#include "codec_ape.h"

using namespace APE;

namespace {

/* fa_ape_io as the SDK's read-only I/O object. */
class FaApeIO : public IAPEIO
{
public:
    explicit FaApeIO(const fa_ape_io& io) : m_io(io) { }

    int Open(const str_utfn *, bool) APE_OVERRIDE { return APE_ERROR_UNDEFINED; }
    int Close() APE_OVERRIDE { return APE_ERROR_SUCCESS; }

    int Read(void * pBuffer, int64 nBytesToRead, int64 * pBytesRead) APE_OVERRIDE
    {
        int64 nDone = 0;
        unsigned char * p = static_cast<unsigned char *>(pBuffer);
        int nResult = APE_ERROR_SUCCESS;
        while (nDone < nBytesToRead)
        {
            const int64 n = m_io.read(m_io.user, p + nDone, static_cast<size_t>(nBytesToRead - nDone));
            if (n < 0) { nResult = APE_ERROR_IO_READ; break; }
            if (n == 0) break;
            nDone += n;
        }
        if (pBytesRead) *pBytesRead = nDone;
        return nResult;
    }

    int Write(const void *, int64, int64 * pBytesWritten) APE_OVERRIDE
    {
        if (pBytesWritten) *pBytesWritten = 0;
        return APE_ERROR_IO_WRITE;
    }

    int Seek(int64 nPosition, SeekMethod nMethod) APE_OVERRIDE
    {
        int64 nTarget = nPosition;
        if (nMethod == SeekFileCurrent) nTarget += m_io.tell(m_io.user);
        else if (nMethod == SeekFileEnd) nTarget += m_io.size;
        if (nTarget < 0) return APE_ERROR_IO_READ;
        if (nTarget > m_io.size) nTarget = m_io.size;   /* reads there return 0 bytes */
        return m_io.seek(m_io.user, nTarget) == 0 ? APE_ERROR_SUCCESS : APE_ERROR_IO_READ;
    }

    int Create(const str_utfn *) APE_OVERRIDE { return APE_ERROR_UNDEFINED; }
    int Delete() APE_OVERRIDE { return APE_ERROR_UNDEFINED; }
    int SetEOF() APE_OVERRIDE { return APE_ERROR_UNDEFINED; }
    unsigned char * GetBuffer(int * pnBufferBytes) APE_OVERRIDE
    {
        if (pnBufferBytes) *pnBufferBytes = 0;
        return APE_NULL;
    }
    int64 GetPosition() APE_OVERRIDE { return m_io.tell(m_io.user); }
    int64 GetSize() APE_OVERRIDE { return m_io.size; }

private:
    fa_ape_io m_io;
};

} // namespace

/* The SDK sizes its buffers from the header and the seek table: a damaged file
 * could ask for gigabytes. A frame holds at most 73728 * 16 blocks (insane level)
 * of at most 64 MB of PCM; each frame starts after the one before and its
 * compressed bytes fit the file and at most twice its PCM plus 1 MB. */
static bool SeekTableSane(CAPEInfo * pInfo, int64 nFileSize, int64 nAlign)
{
    const int64 nBlocksPerFrame = pInfo->GetInfo(IAPEDecompress::APE_INFO_BLOCKS_PER_FRAME);
    const int64 nFrames = pInfo->GetInfo(IAPEDecompress::APE_INFO_TOTAL_FRAMES);
    if (nBlocksPerFrame < 1 || nBlocksPerFrame > 73728 * 16 || nBlocksPerFrame * nAlign > (int64(64) << 20))
        return false;
    if (nFrames < 0 || pInfo->GetInfo(IAPEDecompress::APE_INFO_TOTAL_BLOCKS) < 0)
        return false;
    const int64 nMaxFrameBytes = 2 * nBlocksPerFrame * nAlign + (int64(1) << 20);
    int64 nPrevious = -1;
    for (int64 nFrame = 0; nFrame < nFrames; nFrame++)
    {
        const int64 nStart = pInfo->GetInfo(IAPEDecompress::APE_INFO_SEEK_BYTE, nFrame);
        const int64 nBytes = pInfo->GetInfo(IAPEDecompress::APE_INFO_FRAME_BYTES, nFrame);
        if (nStart < nPrevious || nStart > nFileSize || nBytes < 0 || nBytes > nMaxFrameBytes || nBytes > nFileSize - nStart)
            return false;
        nPrevious = nStart;
    }
    return true;
}

struct fa_ape {
    FaApeIO * io;
    IAPEDecompress * dec;
    /* An exception left the SDK (out of memory mid-read): its state may be half
     * updated (a semaphore taken and not given back), so it is not called again. */
    bool broken;
};

extern "C" fa_ape* fa_ape_open(const fa_ape_io* io, fa_ape_info* out_info, int* error)
{
    fa_ape * a = APE_NULL;
    *error = 4;
    try
    {
        a = new fa_ape();
        a->io = new FaApeIO(*io);
        int nError = APE_ERROR_SUCCESS;
        /* CAPEInfo borrows the I/O object; the decompressor takes the CAPEInfo. */
        CAPEInfo * pInfo = new CAPEInfo(&nError, a->io);
        if (nError != APE_ERROR_SUCCESS)
        {
            delete pInfo;
            fa_ape_close(a);
            *error = 1;
            return APE_NULL;
        }
        const int64 nVersion = pInfo->GetInfo(IAPEDecompress::APE_INFO_FILE_VERSION);
        const int64 nChannels = pInfo->GetInfo(IAPEDecompress::APE_INFO_CHANNELS);
        const int64 nBits = pInfo->GetInfo(IAPEDecompress::APE_INFO_BITS_PER_SAMPLE);
        const int64 nRate = pInfo->GetInfo(IAPEDecompress::APE_INFO_SAMPLE_RATE);
        const int64 nAlign = pInfo->GetInfo(IAPEDecompress::APE_INFO_BLOCK_ALIGN);
        /* The float flag is only valid on 32-bit samples: GetData's float transform
         * writes 4 bytes a sample, past the end of a buffer sized for fewer. */
        const bool bFloat = (pInfo->GetInfo(IAPEDecompress::APE_INFO_FORMAT_FLAGS) & APE_FORMAT_FLAG_FLOATING_POINT) != 0;
        if (nChannels < 1 || nChannels > 32 || nRate < 1 || nRate > 768000 ||
            (nBits != 8 && nBits != 16 && nBits != 24 && nBits != 32) || nAlign != nChannels * (nBits / 8) ||
            (bFloat && nBits != 32) || !SeekTableSane(pInfo, io->size, nAlign))
        {
            delete pInfo;
            fa_ape_close(a);
            return APE_NULL;
        }
        /* Versions the SDK has no decompressor for: CreateIAPEDecompressEx2 would
         * neither use nor delete pInfo (see MACLibDecode.cpp). */
        if (nVersion > APE_FILE_VERSION_NUMBER && nVersion != 4110)
        {
            delete pInfo;
            fa_ape_close(a);
            *error = 2;
            return APE_NULL;
        }
        a->dec = CreateIAPEDecompressEx2(pInfo, -1, -1, &nError);   /* deletes pInfo on failure */
        if (a->dec == APE_NULL || nError != APE_ERROR_SUCCESS)
        {
            fa_ape_close(a);
            *error = nError == APE_ERROR_INSUFFICIENT_MEMORY ? 3 : nError == APE_ERROR_UNSUPPORTED_FILE_VERSION ? 2 : 4;
            return APE_NULL;
        }

        IAPEDecompress * d = a->dec;
        out_info->sample_rate = static_cast<uint32_t>(nRate);
        out_info->channels = static_cast<uint32_t>(nChannels);
        out_info->bits = static_cast<uint32_t>(nBits);
        out_info->is_float = bFloat;
        out_info->block_align = static_cast<uint32_t>(nAlign);
        out_info->total_blocks = d->GetInfo(IAPEDecompress::APE_INFO_TOTAL_BLOCKS);
        out_info->file_version = static_cast<int32_t>(nVersion);
        out_info->compression = static_cast<int32_t>(d->GetInfo(IAPEDecompress::APE_INFO_COMPRESSION_LEVEL));
        out_info->audio_bytes = -1;
        out_info->bitrate = 0;
        const int64 nFrames = d->GetInfo(IAPEDecompress::APE_INFO_TOTAL_FRAMES);
        if (nFrames > 0)
        {
            const int64 nStart = d->GetInfo(IAPEDecompress::APE_INFO_SEEK_BYTE, 0);
            const int64 nEnd = d->GetInfo(IAPEDecompress::APE_INFO_SEEK_BYTE, nFrames - 1) +
                               d->GetInfo(IAPEDecompress::APE_INFO_FRAME_BYTES, nFrames - 1);
            if (nEnd > nStart && nEnd <= io->size) out_info->audio_bytes = nEnd - nStart;
        }
        if (out_info->audio_bytes > 0 && out_info->total_blocks > 0)
            out_info->bitrate = static_cast<uint32_t>(static_cast<double>(out_info->audio_bytes) * 8.0 * static_cast<double>(nRate) /
                                                      static_cast<double>(out_info->total_blocks) + 0.5);
        *error = 0;
        return a;
    }
    catch (const std::bad_alloc &)
    {
        *error = 3;
    }
    catch (...)
    {
        *error = 4;
    }
    if (a) fa_ape_close(a);
    return APE_NULL;
}

extern "C" int fa_ape_read(fa_ape* a, void* out, int64_t frames, int64_t* got)
{
    *got = 0;
    if (a->broken) return APE_ERROR_UNDEFINED;
    try
    {
        /* Float samples back to IEEE floats. The SDK keeps every file's audio as
         * little-endian with unsigned 8-bit (the encoder converts AIFF, CAF and SND
         * input that way); the other two steps would turn it back into the original
         * file's byte order and 8-bit sign, which only writing that file wants. These
         * are the SDK's own playback defaults. */
        IAPEDecompress::APE_GET_DATA_PROCESSING processing = { true, false, false };
        int64 nGot = 0;
        const int nResult = a->dec->GetData(static_cast<unsigned char *>(out), frames, &nGot, &processing);
        *got = nGot;
        return nResult;
    }
    catch (...)
    {
        a->broken = true;
        return APE_ERROR_UNDEFINED;
    }
}

extern "C" int fa_ape_seek(fa_ape* a, int64_t frame)
{
    if (a->broken) return APE_ERROR_UNDEFINED;
    try
    {
        return a->dec->Seek(frame);
    }
    catch (...)
    {
        a->broken = true;
        return APE_ERROR_UNDEFINED;
    }
}

extern "C" void fa_ape_close(fa_ape* a)
{
    if (!a) return;
    try
    {
        delete a->dec;   /* deletes its CAPEInfo, which leaves the borrowed I/O object alone */
    }
    catch (...)
    {
    }
    delete a->io;
    delete a;
}
