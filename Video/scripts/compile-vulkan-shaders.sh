#!/usr/bin/env sh
# Regenerates the committed SPIR-V for FatzBomb.MediaControl.Rendering.Vulkan.
# CI machines do not have glslang, so run this after editing any Shaders/*.vert or *.frag
# source and commit the resulting .spv files (they are embedded into the assembly).
# Requires glslangValidator (Debian/Ubuntu: apt-get install glslang-tools).
set -eu

shader_dir="$(cd "$(dirname "$0")/../FatzBomb.MediaControl.Rendering.Vulkan/Shaders" && pwd)"
for source in "$shader_dir"/*.vert "$shader_dir"/*.frag; do
    glslangValidator -V --target-env vulkan1.0 -o "$source.spv" "$source"
done
