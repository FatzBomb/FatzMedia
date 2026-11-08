#version 450

// CDG overlay expanded on the CPU to straight BGRA (fully transparent or opaque texels only),
// premultiplied for the ONE / ONE_MINUS_SRC_ALPHA blend. The source texel of every destination
// column and row comes from the software renderer's integer mapping (NearestSampledPlacement),
// computed on the CPU into cdgMap (columns first, then rows), so no GPU rounding can pick a
// neighbouring CD+G pixel (ENG-049: NVIDIA's interpolated coordinates did at
// some scales). push.origin: the clipped destination's left (x) and top (y), its column count (z)
// and row count (w). Indices are clamped to the map: helper invocations next to the quad's edges
// run the shader too, and an out-of-range storage-buffer read is undefined (it crashed lavapipe).
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
    int rows = int(push.origin.w);
    int column = clamp(pixel.x - int(push.origin.x), 0, columns - 1);
    int row = clamp(pixel.y - int(push.origin.y), 0, rows - 1);
    int sourceX = int(cdgMap.source[column]);
    int sourceY = int(cdgMap.source[columns + row]);
    vec4 texel = texelFetch(cdgTexture, ivec2(sourceX, sourceY), 0);
    outColor = vec4(texel.rgb * texel.a, texel.a);
}
