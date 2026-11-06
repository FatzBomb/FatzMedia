# Monkey's Audio SDK (BSD-3-Clause), decoder part only, for .ape files: linked
# into fatzaudio as a static library of position-independent C++11, together with
# src/codec_ape.cpp, its C interface (src/codec_ape.h). Nothing is exported from
# the shared library. See third_party/monkeysaudio/README.md for the version and
# the changes made to the SDK.

set(FA_MAC_DIR ${CMAKE_CURRENT_LIST_DIR}/monkeysaudio/Source)
set(FA_MAC_SIMD
    ${FA_MAC_DIR}/MACLib/NNFilterSSE2.cpp
    ${FA_MAC_DIR}/MACLib/NNFilterSSE4.1.cpp
    ${FA_MAC_DIR}/MACLib/NNFilterAVX2.cpp
    ${FA_MAC_DIR}/MACLib/NNFilterAVX512.cpp
    ${FA_MAC_DIR}/MACLib/NNFilterNeon.cpp)
add_library(fa_mac STATIC
    ${CMAKE_CURRENT_LIST_DIR}/../src/codec_ape.cpp
    ${FA_MAC_DIR}/MACLib/APEDecompress.cpp
    ${FA_MAC_DIR}/MACLib/APEDecompressCore.cpp
    ${FA_MAC_DIR}/MACLib/APEHeader.cpp
    ${FA_MAC_DIR}/MACLib/APEInfo.cpp
    ${FA_MAC_DIR}/MACLib/APETag.cpp
    ${FA_MAC_DIR}/MACLib/FloatTransform.cpp
    ${FA_MAC_DIR}/MACLib/Interim.cpp
    ${FA_MAC_DIR}/MACLib/MACLibDecode.cpp
    ${FA_MAC_DIR}/MACLib/NewPredictor.cpp
    ${FA_MAC_DIR}/MACLib/NNFilter.cpp
    ${FA_MAC_DIR}/MACLib/NNFilterGeneric.cpp
    ${FA_MAC_DIR}/MACLib/Prepare.cpp
    ${FA_MAC_DIR}/MACLib/UnBitArray.cpp
    ${FA_MAC_DIR}/MACLib/UnBitArrayBase.cpp
    ${FA_MAC_DIR}/MACLib/Old/AntiPredictorExtraHighOld.cpp
    ${FA_MAC_DIR}/MACLib/Old/AntiPredictorFastOld.cpp
    ${FA_MAC_DIR}/MACLib/Old/AntiPredictorHighOld.cpp
    ${FA_MAC_DIR}/MACLib/Old/AntiPredictorNormalOld.cpp
    ${FA_MAC_DIR}/MACLib/Old/AntiPredictorOld.cpp
    ${FA_MAC_DIR}/MACLib/Old/APEDecompressCoreOld.cpp
    ${FA_MAC_DIR}/MACLib/Old/APEDecompressOld.cpp
    ${FA_MAC_DIR}/MACLib/Old/NewPredictorOld.cpp
    ${FA_MAC_DIR}/MACLib/Old/UnBitArrayOld.cpp
    ${FA_MAC_DIR}/MACLib/Old/UnMACOld.cpp
    ${FA_MAC_DIR}/Shared/CharacterHelper.cpp
    ${FA_MAC_DIR}/Shared/CircleBuffer.cpp
    ${FA_MAC_DIR}/Shared/CPUFeatures.cpp
    ${FA_MAC_DIR}/Shared/CRC.cpp
    ${FA_MAC_DIR}/Shared/GlobalFunctions.cpp
    ${FA_MAC_DIR}/Shared/MemoryIO.cpp
    ${FA_MAC_DIR}/Shared/Semaphore.cpp
    ${FA_MAC_DIR}/Shared/Thread.cpp
    ${FA_MAC_DIR}/Shared/WholeFileIO.cpp
    ${FA_MAC_SIMD})
set_target_properties(fa_mac PROPERTIES
    CXX_STANDARD 11
    CXX_STANDARD_REQUIRED ON
    CXX_EXTENSIONS OFF
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN ON
    POSITION_INDEPENDENT_CODE ON)
target_include_directories(fa_mac PRIVATE ${FA_MAC_DIR}/MACLib ${FA_MAC_DIR}/Shared)
# FA_MAC_SYNC_DECODE: frames decode on the thread that reads (our change, see the
# README); DLLEXPORT empty: the SDK would otherwise export its C functions.
target_compile_definitions(fa_mac PRIVATE FA_MAC_SYNC_DECODE "DLLEXPORT=")
if(WIN32)
    target_compile_definitions(fa_mac PRIVATE PLATFORM_WINDOWS UNICODE _UNICODE)
endif()
if(MSVC)
    target_compile_options(fa_mac PRIVATE /W0 /EHsc /utf-8)
    target_compile_definitions(fa_mac PRIVATE _CRT_SECURE_NO_WARNINGS NOMINMAX)
    set_property(TARGET fa_mac PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
else()
    # Its own warnings are not ours to fix; it needs exceptions and RTTI (the
    # Android NDK turns both off by default). A section per function so the
    # linker drops the encoder parts the shared files still carry.
    # -fwrapv: the predictors let signed integers wrap, as the format was designed
    # (the reference encoder does the same), so wrapping must be defined behaviour.
    target_compile_options(fa_mac PRIVATE -w -fexceptions -frtti -fwrapv -ffunction-sections -fdata-sections
        $<$<NOT:$<CONFIG:Debug>>:-Os>)
    # The AVX2 / AVX-512 / SSE4.1 filters are picked at run time from the CPU's
    # features; each file needs its instruction set switched on (x86 only; the
    # others compile to "not available" stubs elsewhere). Apple builds both
    # architectures in one go, so the flags go to the x86_64 half only
    # (-Xarch_x86_64 covers just the one option after it, so each gets its own).
    if(APPLE)
        set(fa_mac_x86 "-Xarch_x86_64")
    elseif(CMAKE_SYSTEM_PROCESSOR MATCHES "^(x86_64|AMD64|amd64|i[3-6]86|x86)$")
        set(fa_mac_x86 "")
    endif()
    if(DEFINED fa_mac_x86)
        # COMPILE_FLAGS is a plain string, so CMake keeps the repeated -Xarch_x86_64
        # (COMPILE_OPTIONS would merge the duplicates).
        foreach(fa_mac_simd SSE2:-msse2 SSE4.1:-msse4.1 AVX2:-mavx2 AVX512:-mavx512dq,-mavx512bw)
            string(REPLACE ":" ";" fa_mac_simd "${fa_mac_simd}")
            list(GET fa_mac_simd 0 fa_mac_name)
            list(GET fa_mac_simd 1 fa_mac_flags)
            string(REPLACE "," ";" fa_mac_flags "${fa_mac_flags}")
            set(fa_mac_opts "")
            foreach(fa_mac_flag IN LISTS fa_mac_flags)
                string(APPEND fa_mac_opts " ${fa_mac_x86} ${fa_mac_flag}")
            endforeach()
            set_source_files_properties(${FA_MAC_DIR}/MACLib/NNFilter${fa_mac_name}.cpp PROPERTIES COMPILE_FLAGS "${fa_mac_opts}")
        endforeach()
    endif()
endif()
if(UNIX)
    find_package(Threads REQUIRED)
    target_link_libraries(fa_mac PRIVATE Threads::Threads)
endif()
