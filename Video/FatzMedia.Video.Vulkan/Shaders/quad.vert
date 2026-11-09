#version 450

// Unit quad drawn as a 4-vertex triangle strip covering the whole viewport. The viewport
// (and scissor) select the destination rectangle; texRect holds the texture coordinates at
// the viewport's top-left (xy) and bottom-right (zw) edges. The wash pipeline pushes its colour
// into the same 16 bytes and ignores the resulting texture coordinates.
layout(push_constant) uniform Push
{
    vec4 texRect;
} push;

layout(location = 0) noperspective out vec2 outTexCoord;

void main()
{
    vec2 corner = vec2(float(gl_VertexIndex & 1), float(gl_VertexIndex >> 1));
    outTexCoord = mix(push.texRect.xy, push.texRect.zw, corner);
    gl_Position = vec4((corner * 2.0) - 1.0, 0.0, 1.0);
}
