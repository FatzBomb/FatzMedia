#version 450

// A hardware-decoded two-plane YCbCr frame (NV12, P010; Stage 045A slice 2) to RGB, drawn into the
// renderer's BGRA video layer at the display size. The luma plane is sampled as R8/R16 and the
// chroma plane as R8G8/R16G16 through per-plane views, with a linear, clamp-to-edge sampler.
// HDR10 and HLG frames are tone mapped to SDR as the Direct3D11 shader does (Stages 038A, 042A):
// PQ (or HLG through its inverse OETF and the BT.2100 OOTF) to display light, the BT.2390 curve on
// the brightest channel, BT.2020 to BT.709 primaries, gamma 2.4.
layout(set = 0, binding = 0) uniform sampler2D lumaPlane;
layout(set = 0, binding = 1) uniform sampler2D chromaPlane;
layout(set = 0, binding = 2, std140) uniform VideoConstants
{
    vec4 textureScale;    // xy: display / coded size (the vertex stage's texture rectangle)
    vec4 yuvScaleOffset;  // luma scale, luma offset, chroma scale, chroma offset
    vec4 yuvMatrix;       // rCr, gCb, gCr, bCb
    vec4 curve;           // source peak (PQ), target peak / source peak (PQ), knee start, compress
    vec4 outputParams;    // 1 / target nits, 1 / SDR gamma, target nits, unused
    vec4 sampleClamp;     // normalized sampling bounds inside the picture: left, top, right, bottom
    vec4 transfer;        // HLG (1) or PQ (0), HLG nominal peak nits, HLG system gamma - 1, tone map (1)
    vec4 reserved;
} constants;

layout(location = 0) noperspective in vec2 inTexCoord;
layout(location = 0) out vec4 outColor;

const float PqM1 = 2610.0 / 16384.0;
const float PqM2 = 2523.0 / 4096.0 * 128.0;
const float PqC1 = 3424.0 / 4096.0;
const float PqC2 = 2413.0 / 4096.0 * 32.0;
const float PqC3 = 2392.0 / 4096.0 * 32.0;

float pqToNits(float signal)
{
    float e = pow(clamp(signal, 0.0, 1.0), 1.0 / PqM2);
    return 10000.0 * pow(max(e - PqC1, 0.0) / (PqC2 - PqC3 * e), 1.0 / PqM1);
}

float nitsToPq(float nits)
{
    float y = pow(clamp(nits / 10000.0, 0.0, 1.0), PqM1);
    return pow((PqC1 + PqC2 * y) / (1.0 + PqC3 * y), PqM2);
}

// ARIB STD-B67 / BT.2100 HLG inverse OETF: signal to normalized scene light.
const float HlgA = 0.17883277;
const float HlgB = 0.28466892;
const float HlgC = 0.55991073;

float hlgToScene(float signal)
{
    return signal <= 0.5 ? signal * signal / 3.0 : (exp((signal - HlgC) / HlgA) + HlgB) / 12.0;
}

// The BT.2100 OOTF: display nits Lw * Ys^(gamma - 1) * E, Ys with BT.2020 weights.
vec3 hlgToNits(vec3 signal)
{
    vec3 s = clamp(signal, 0.0, 1.0);
    vec3 scene = vec3(hlgToScene(s.r), hlgToScene(s.g), hlgToScene(s.b));
    float luminance = dot(scene, vec3(0.2627, 0.6780, 0.0593));
    float ootf = luminance > 0.0 ? constants.transfer.y * pow(luminance, constants.transfer.z) : 0.0;
    return scene * ootf;
}

// BT.2390 EETF, as Core's Bt2390ToneCurve.
float toneCurve(float nits)
{
    if (constants.curve.w < 0.5)
        return min(nits, constants.outputParams.z);

    float e1 = min(nitsToPq(nits) / constants.curve.x, 1.0);
    float kneeStart = constants.curve.z;
    float e2 = e1;
    if (e1 > kneeStart)
    {
        float t = (e1 - kneeStart) / (1.0 - kneeStart);
        float t2 = t * t;
        float t3 = t2 * t;
        e2 = (2.0 * t3 - 3.0 * t2 + 1.0) * kneeStart
            + (t3 - 2.0 * t2 + t) * (1.0 - kneeStart)
            + (-2.0 * t3 + 3.0 * t2) * constants.curve.y;
    }

    return min(pqToNits(e2 * constants.curve.x), constants.outputParams.z);
}

void main()
{
    vec2 uv = clamp(inTexCoord, constants.sampleClamp.xy, constants.sampleClamp.zw);
    float y = texture(lumaPlane, uv).r * constants.yuvScaleOffset.x + constants.yuvScaleOffset.y;
    vec2 c = texture(chromaPlane, uv).rg * constants.yuvScaleOffset.z + constants.yuvScaleOffset.w;
    vec3 signal = vec3(
        y + constants.yuvMatrix.x * c.y,
        y - constants.yuvMatrix.y * c.x - constants.yuvMatrix.z * c.y,
        y + constants.yuvMatrix.w * c.x);

    if (constants.transfer.w < 0.5)
    {
        outColor = vec4(clamp(signal, 0.0, 1.0), 1.0);
        return;
    }

    vec3 nits = constants.transfer.x > 0.5
        ? hlgToNits(signal)
        : vec3(pqToNits(signal.r), pqToNits(signal.g), pqToNits(signal.b));
    float brightest = max(nits.r, max(nits.g, nits.b));
    float gain = brightest > 0.0 ? toneCurve(brightest) / brightest : 1.0;
    vec3 linear2020 = nits * (gain * constants.outputParams.x);

    // BT.2020 to BT.709 primaries, linear light (ITU-R BT.2087).
    vec3 linear709 = vec3(
        dot(linear2020, vec3(1.660491, -0.587641, -0.072850)),
        dot(linear2020, vec3(-0.124550, 1.132900, -0.008349)),
        dot(linear2020, vec3(-0.018151, -0.100579, 1.118730)));

    outColor = vec4(pow(clamp(linear709, 0.0, 1.0), vec3(constants.outputParams.y)), 1.0);
}
