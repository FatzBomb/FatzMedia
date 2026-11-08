# Cross-compiles for Linux arm64 on an x86_64 Linux PC (Ubuntu: apt install
# gcc-aarch64-linux-gnu g++-aarch64-linux-gnu). On Ubuntu 22.04 the library needs
# only glibc 2.35, like the x64 one.
#   cmake -S . -B build-linux-arm64 -DCMAKE_TOOLCHAIN_FILE=cmake/toolchain-linux-arm64.cmake
set(CMAKE_SYSTEM_NAME Linux)
set(CMAKE_SYSTEM_PROCESSOR aarch64)
set(CMAKE_C_COMPILER aarch64-linux-gnu-gcc)
set(CMAKE_CXX_COMPILER aarch64-linux-gnu-g++)
set(CMAKE_STRIP aarch64-linux-gnu-strip)
set(CMAKE_FIND_ROOT_PATH /usr/aarch64-linux-gnu)
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
# Tests run under qemu-user (CI and PCs with qemu-user-static installed).
set(CMAKE_CROSSCOMPILING_EMULATOR "qemu-aarch64-static;-L;/usr/aarch64-linux-gnu")
