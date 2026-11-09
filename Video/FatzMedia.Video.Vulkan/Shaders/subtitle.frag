#version 450

// Subtitle and karaoke raster, already premultiplied BGRA (the rasterizer's output), sampled
// with a nearest, clamp-to-edge sampler and blended ONE / ONE_MINUS_SRC_ALPHA as is
// (software renderer: SoftwareCompositionBuffer.BlendPremultipliedAt).
layout(set = 0, binding = 0) uniform sampler2D subtitleTexture;

layout(location = 0) noperspective in vec2 inTexCoord;
layout(location = 0) out vec4 outColor;

void main()
{
    outColor = texture(subtitleTexture, inTexCoord);
}
