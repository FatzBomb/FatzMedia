#version 450

// CDG overlay expanded on the CPU to straight BGRA (fully transparent or opaque texels only),
// sampled with a nearest, clamp-to-edge sampler and premultiplied for the
// ONE / ONE_MINUS_SRC_ALPHA blend.
layout(set = 0, binding = 0) uniform sampler2D cdgTexture;

layout(location = 0) noperspective in vec2 inTexCoord;
layout(location = 0) out vec4 outColor;

void main()
{
    vec4 texel = texture(cdgTexture, inTexCoord);
    outColor = vec4(texel.rgb * texel.a, texel.a);
}
