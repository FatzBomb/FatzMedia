#!/usr/bin/env sh
# Regenerates the committed SPIR-V for FatzMedia.Video.Vulkan.
# Run this after editing any Shaders/*.vert or *.frag source and commit the resulting .spv
# files (they are embedded into the assembly). Linux CI fails when they do not match the
# source (scripts/check-vulkan-shaders.sh).
# Requires glslangValidator (Debian/Ubuntu: apt-get install glslang-tools).
set -eu

shader_dir="$(cd "$(dirname "$0")/../FatzMedia.Video.Vulkan/Shaders" && pwd)"
for source in "$shader_dir"/*.vert "$shader_dir"/*.frag; do
    glslangValidator -V --target-env vulkan1.0 -o "$source.spv" "$source"
done
