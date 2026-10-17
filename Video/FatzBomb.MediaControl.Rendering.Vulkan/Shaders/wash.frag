#version 450

// Straight-alpha wash colour (RGBA), premultiplied here and blended ONE / ONE_MINUS_SRC_ALPHA
// over the premultiplied target (software renderer: SoftwareCompositionBuffer.BlendStraight).
layout(push_constant) uniform Push
{
    vec4 color;
} push;

layout(location = 0) out vec4 outColor;

void main()
{
    outColor = vec4(push.color.rgb * push.color.a, push.color.a);
}
