#version 450

// CDG overlay expanded on the CPU to straight BGRA (fully transparent or opaque texels only),
// premultiplied for the ONE / ONE_MINUS_SRC_ALPHA blend. The source texel of every destination
// column and row comes from the software renderer's integer mapping (NearestSampledPlacement),
// computed on the CPU into cdgMap (columns first, then rows), so no GPU rounding can pick a
// neighbouring CD+G pixel (ENG-049: NVIDIA's interpolated coordinates did at
// some scales). push.origin: the clipped destination's left (x) and top (y), and the column count (z).
layout(set = 0, binding = 0) uniform sampler2D cdgTexture;
layout(set = 0, binding = 1, std430) readonly buffer CdgMap
{
    uint source[];
} cdgMap;

layout(push_constant) uniform Push
{
    vec4 origin;
} push;

layout(location = 0) noperspective in vec2 inTexCoord;
layout(location = 0) out vec4 outColor;

void main()
{
    ivec2 pixel = ivec2(gl_FragCoord.xy);
    int columns = int(push.origin.z);
    int sourceX = int(cdgMap.source[pixel.x - int(push.origin.x)]);
    int sourceY = int(cdgMap.source[columns + pixel.y - int(push.origin.y)]);
    vec4 texel = texelFetch(cdgTexture, ivec2(sourceX, sourceY), 0);
    outColor = vec4(texel.rgb * texel.a, texel.a);
}
