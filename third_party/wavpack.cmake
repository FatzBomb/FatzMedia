# libwavpack's decoder (BSD-3-Clause, third_party/wavpack/README.md), linked into
# fatzaudio as a static library of position-independent C. Nothing is exported.

set(FA_WAVPACK_FILES
    common_utils.c decorr_utils.c entropy_utils.c open_utils.c read_words.c tags.c
    unpack.c unpack_floats.c unpack_seek.c unpack_utils.c unpack_dsd.c)
list(TRANSFORM FA_WAVPACK_FILES PREPEND ${CMAKE_CURRENT_LIST_DIR}/wavpack/src/)
add_library(fa_wavpack STATIC ${FA_WAVPACK_FILES})
target_include_directories(fa_wavpack PRIVATE ${CMAKE_CURRENT_LIST_DIR}/wavpack/src)
target_include_directories(fa_wavpack PUBLIC ${CMAKE_CURRENT_LIST_DIR}/wavpack/include)
# DSD files decode to PCM; no worker threads (FatzAudio has its own), no WavPack 3 files.
target_compile_definitions(fa_wavpack PRIVATE ENABLE_DSD)
# Third-party code: its own warnings are not ours to fix.
if(MSVC)
    target_compile_definitions(fa_wavpack PRIVATE _CRT_SECURE_NO_WARNINGS)
    target_compile_options(fa_wavpack PRIVATE /W0)
    set_property(TARGET fa_wavpack PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
else()
    target_compile_options(fa_wavpack PRIVATE -w)
endif()
