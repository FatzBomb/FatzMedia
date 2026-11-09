#!/usr/bin/env sh
# Checks that the committed SPIR-V in FatzMedia.Video.Vulkan/Shaders matches its
# GLSL source (ENG-014). Compiles every Shaders/*.vert and *.frag the way
# compile-vulkan-shaders.sh does and compares the result with the committed .spv. The
# SPIR-V header's generator word (bytes 8-11, the compiler and its version) is ignored, so a
# glslang patch update alone does not fail the check.
# Requires glslangValidator (Debian/Ubuntu: apt-get install glslang-tools).
set -eu

shader_dir="$(cd "$(dirname "$0")/../FatzMedia.Video.Vulkan/Shaders" && pwd)"
work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT

glslangValidator --version | head -n 1
status=0
for source in "$shader_dir"/*.vert "$shader_dir"/*.frag; do
    name="$(basename "$source")"
    compiled="$work_dir/$name.spv"
    glslangValidator -V --target-env vulkan1.0 -o "$compiled" "$source" >/dev/null
    committed="$source.spv"
    if [ ! -f "$committed" ]; then
        echo "MISSING: $name.spv is not committed."
        status=1
    elif ! cmp -s -n 8 "$compiled" "$committed" || ! cmp -s -i 12 "$compiled" "$committed"; then
        echo "STALE: $name.spv does not match $name."
        status=1
    else
        echo "ok: $name"
    fi
done

if [ "$status" -ne 0 ]; then
    echo "Run scripts/compile-vulkan-shaders.sh and commit the .spv files. If only the glslang"
    echo "version changed, recompiling and committing is also the fix."
fi
exit "$status"
