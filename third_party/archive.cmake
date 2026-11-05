# Decoders for archives, linked into fatzaudio as static libraries of position-independent
# C: the LZMA SDK (public domain; LZMA, LZMA2 and xz, single-threaded: our own code runs xz
# blocks in parallel) and the bzip2 library's decompressor (BSD-style licence). Nothing is
# exported from the shared library. Notices: THIRD_PARTY_NOTICES.md.

set(FA_LZMA_FILES
    LzmaDec.c Lzma2Dec.c Xz.c XzDec.c XzIn.c XzCrc64.c XzCrc64Opt.c 7zCrc.c 7zCrcOpt.c
    Sha256.c Sha256Opt.c Bra.c Bra86.c BraIA64.c Delta.c Bcj2.c CpuArch.c
    7zStream.c Alloc.c)
list(TRANSFORM FA_LZMA_FILES PREPEND ${CMAKE_CURRENT_LIST_DIR}/lzma/)
add_library(fa_lzma STATIC ${FA_LZMA_FILES})
target_include_directories(fa_lzma PUBLIC ${CMAKE_CURRENT_LIST_DIR}/lzma)
# Z7_ST: no multithreaded decoders (they need the SDK's thread layer); it changes the
# SDK's headers too, so every user sees it.
target_compile_definitions(fa_lzma PUBLIC Z7_ST)

file(STRINGS ${CMAKE_CURRENT_LIST_DIR}/bzip2/sources.txt FA_BZIP2_FILES)
list(TRANSFORM FA_BZIP2_FILES PREPEND ${CMAKE_CURRENT_LIST_DIR}/bzip2/)
add_library(fa_bzip2 STATIC ${FA_BZIP2_FILES})
target_include_directories(fa_bzip2 PUBLIC ${CMAKE_CURRENT_LIST_DIR}/bzip2)
# PATCHES.md: the compressor is not built; no stdio, so errors come back as return codes.
target_compile_definitions(fa_bzip2 PUBLIC BZ_DECOMPRESS_ONLY BZ_NO_STDIO)

foreach(t fa_lzma fa_bzip2)
    # Third-party code: its own warnings are not ours to fix.
    if(MSVC)
        target_compile_options(${t} PRIVATE /W0)
        set_property(TARGET ${t} PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    else()
        target_compile_options(${t} PRIVATE -w)
    endif()
endforeach()
# The SDK reads 16/32/64-bit values through plain pointers where the CPU allows unaligned loads (x86, ARM64): fine
# for the hardware, flagged by UBSan's alignment check, so the check is off for this library (a sanitizer build).
if(NOT MSVC)
    target_compile_options(fa_lzma PRIVATE -fno-sanitize=alignment)
endif()
