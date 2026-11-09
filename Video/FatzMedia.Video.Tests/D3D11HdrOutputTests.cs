using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FatzMedia.Video.Core.Configuration;
using FatzMedia.Video.Core.Graphics;
using FatzMedia.Video.Core.Graphics.Composition;
using FatzMedia.Video.Core.Graphics.Composition.Planning;
using FatzMedia.Video.Core.Graphics.D3D.Device;
using FatzMedia.Video.Core.Graphics.D3D.Presentation;
using FatzMedia.Video.Core.Graphics.D3D.Presentation.Hdr;
using FatzMedia.Video.Core.Graphics.D3D.Video;
using FatzMedia.Video.Core.Video.Hdr;
using FatzMedia.Video.Direct3D11;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FatzMedia.Video.Tests;

/// <summary>
/// Stage 042A slice 2: HDR output on HDR displays (Direct3D11). The shader's scRGB constants
/// reproduce <see cref="HdrOutputPolicy"/>; the controller decides, switches the primary swap
/// chain, falls back to SDR on a failure and reports each change once; on WARP, a composition
/// swap chain switches format (or reports why not) and SDR layers land in an FP16 target at the
/// SDR white level. A real HDR monitor is a manual smoke check (docs/roadmap/updates/042A_2).
/// </summary>
public sealed class D3D11HdrOutputTests
{
    private const string Category = "D3D11.HdrOutput";
    private static readonly VideoColorSpace Hdr10 = new(VideoColorMatrix.Bt2020, VideoColorRange.Limited);
    private static readonly HdrDisplayInfo HdrMonitor = new(HdrEnabled: true, PeakNits: 600, SdrWhiteNits: 200);
    private static readonly HdrDisplayInfo SdrMonitor = new(HdrEnabled: false, PeakNits: 300, SdrWhiteNits: 0);
    private static readonly HdrToneMapRequest Pq4000 = new(4000, 203) { Transfer = HdrTransferFunction.Pq };
    private static readonly HdrToneMapRequest Hlg = new(1000, 203) { Transfer = HdrTransferFunction.Hlg };

    // --- The shader's scRGB output against the policy -------------------------------------

    [Theory]
    [InlineData(4000, 600)]   // compressed into a dimmer display
    [InlineData(1000, 1000)]  // unchanged on a bright enough display
    [InlineData(1000, 600)]
    public void PqGrays_MatchThePolicy(double contentPeak, double displayPeak)
    {
        var request = new HdrToneMapRequest(contentPeak, 203);
        HdrOutputDecision decision = ScRgbDecision(displayPeak);
        for (var code = 64; code <= 940; code += 4)
        {
            var signal = (code - 64) / 876.0;
            var expected = HdrOutputPolicy.PqNitsToScRgb(PqCurve.ToNits(signal), contentPeak, displayPeak);
            double[] scRgb = ShaderScRgb(code, 512, 512, request, decision);
            for (var channel = 0; channel < 3; channel++)
                Assert.Equal(expected, scRgb[channel], 0.002 * Math.Max(1, expected));
        }
    }

    [Theory]
    [InlineData(600)]
    [InlineData(1000)]
    [InlineData(1500)]
    public void HlgGrays_UseTheDisplaysOotf(double displayPeak)
    {
        HdrOutputDecision decision = ScRgbDecision(displayPeak);
        for (var code = 64; code <= 940; code += 4)
        {
            var light = HlgCurve.ToSceneLinear((code - 64) / 876.0);
            var expected = HdrOutputPolicy.HlgToScRgb(light, light, displayPeak);
            double[] scRgb = ShaderScRgb(code, 512, 512, Hlg, decision);
            for (var channel = 0; channel < 3; channel++)
                Assert.Equal(expected, scRgb[channel], 0.002 * Math.Max(1, expected));
        }

        // HLG white is the display's peak.
        Assert.Equal(displayPeak / HdrOutputPolicy.ScRgbUnitNits, ShaderScRgb(940, 512, 512, Hlg, decision)[1], 0.01);
    }

    [Fact]
    public void ScRgbOutput_KeepsColorsOutsideBt709()
    {
        // Saturated BT.2020 red: its green and blue are negative in BT.709, and it is not clipped.
        double[] scRgb = ShaderScRgb(800, 512, 960, new HdrToneMapRequest(1000, 203), ScRgbDecision(1000));
        Assert.True(scRgb[1] < 0);
        Assert.True(scRgb[0] > 1);
    }

    [Fact]
    public void TheConstants_SelectScRgbOutput()
    {
        D3D11HdrToneMapPresenter.ToneMapConstants sdr = Constants(new HdrToneMapRequest(4000, 203), null);
        D3D11HdrToneMapPresenter.ToneMapConstants pq = Constants(new HdrToneMapRequest(4000, 203), ScRgbDecision(600));
        D3D11HdrToneMapPresenter.ToneMapConstants hlg = Constants(Hlg, ScRgbDecision(600));

        Assert.Equal(0f, sdr.Output.W);
        Assert.Equal(1f, pq.Output.W);
        Assert.Equal(1 / 80f, pq.Output.X, 6);
        Assert.Equal(600f, pq.Output.Z);
        Assert.Equal(1f, pq.Curve.W); // a 4000-nit grade is compressed into 600 nits

        Assert.Equal(1f, hlg.Transfer.X);
        Assert.Equal(600f, hlg.Transfer.Y);
        Assert.Equal((float)(HlgCurve.SystemGamma(600) - 1), hlg.Transfer.Z, 5);
        Assert.Equal(0f, hlg.Curve.W);
    }

    // --- The controller -------------------------------------------------------------------

    [Fact]
    public void Disabled_NeverQueriesOrSwitches()
    {
        var queries = 0;
        var controller = new D3D11HdrOutputController(false, _ => { queries++; return HdrMonitor; });
        var backend = new FakeHdrBackend();

        controller.PrimeDisplay(backend.Hwnd);
        Assert.Null(controller.PreparePrimary(backend, Pq4000, blocker: null));
        Assert.Equal(0, queries);
        Assert.Equal(0, backend.ModeChanges);
        Assert.Equal(HdrOutputMode.Sdr, backend.OutputMode);
    }

    [Fact]
    public void HdrVideoOnAnHdrMonitor_SwitchesToScRgbAndBack()
    {
        var display = HdrMonitor;
        var backend = new FakeHdrBackend();
        var controller = new D3D11HdrOutputController(true, _ => display);
        controller.PrimeDisplay(backend.Hwnd);

        List<MediaDiagnosticInfo> raised = Capture(() =>
        {
            HdrOutputDecision? first = controller.PreparePrimary(backend, Pq4000, blocker: null);
            Assert.Equal(HdrOutputMode.ScRgb, first?.Mode);
            Assert.Equal(600, first?.DisplayPeakNits);
            Assert.Equal(HdrOutputMode.ScRgb, backend.OutputMode);

            // The next frames change nothing and report nothing.
            Assert.NotNull(controller.PreparePrimary(backend, Pq4000, blocker: null));
            Assert.NotNull(controller.PreparePrimary(backend, Pq4000, blocker: null));
            Assert.Equal(1, backend.ModeChanges);

            // SDR video: back to SDR.
            Assert.Null(controller.PreparePrimary(backend, null, blocker: null));
            Assert.Equal(HdrOutputMode.Sdr, backend.OutputMode);
            Assert.Equal(2, backend.ModeChanges);
        });

        Assert.Equal(2, raised.Count);
        Assert.Equal("scRGB (the monitor is in HDR mode (peak 600 nits, SDR white 200 nits)).", raised[0].Message);
        Assert.Equal("SDR (the video is SDR).", raised[1].Message);
        Assert.All(raised, info => Assert.Equal(MediaDiagnosticLevel.Info, info.Level));
    }

    [Fact]
    public void TheMetadata_IsTheBrightestLightTheOutputHolds()
    {
        var backend = new FakeHdrBackend();
        var controller = new D3D11HdrOutputController(true, _ => HdrMonitor);
        controller.PrimeDisplay(backend.Hwnd);

        _ = controller.PreparePrimary(backend, new HdrToneMapRequest(400, 203), blocker: null);
        Assert.Equal((ushort)400, backend.Metadata?.MaxContentLightLevel);

        // A grade brighter than the display is mapped to the display's peak.
        _ = controller.PreparePrimary(backend, Pq4000, blocker: null);
        Assert.Equal((ushort)600, backend.Metadata?.MaxContentLightLevel);

        _ = controller.PreparePrimary(backend, Hlg, blocker: null);
        Assert.Equal((ushort)600, backend.Metadata?.MaxContentLightLevel);
        Assert.Equal(1, backend.ModeChanges);
    }

    [Fact]
    public void AnSdrMonitor_OrABlocker_KeepsSdr()
    {
        var display = SdrMonitor;
        var backend = new FakeHdrBackend();
        var controller = new D3D11HdrOutputController(true, _ => display);
        controller.PrimeDisplay(backend.Hwnd);

        Assert.Null(controller.PreparePrimary(backend, Pq4000, blocker: null));
        Assert.Equal("the monitor is not in HDR mode", controller.LastReported?.Reason);

        display = HdrMonitor;
        controller.ForgetDisplay(backend.Hwnd);
        controller.PrimeDisplay(backend.Hwnd);
        Assert.Null(controller.PreparePrimary(backend, Pq4000, D3D11HdrOutputController.SecondaryTargetReason));
        Assert.Equal(D3D11HdrOutputController.SecondaryTargetReason, controller.LastReported?.Reason);
        Assert.Equal(0, backend.ModeChanges);
    }

    [Fact]
    public void AFailedSwitch_FallsBackToSdrOnceAndIsNotRetried()
    {
        var backend = new FakeHdrBackend { FailWith = "ResizeBuffers to R16G16B16A16_Float failed" };
        var controller = new D3D11HdrOutputController(true, _ => HdrMonitor);
        controller.PrimeDisplay(backend.Hwnd);

        List<MediaDiagnosticInfo> raised = Capture(() =>
        {
            for (var frame = 0; frame < 5; frame++)
                Assert.Null(controller.PreparePrimary(backend, Pq4000, blocker: null));
        });

        Assert.Equal(1, backend.ModeChanges);
        Assert.Equal(HdrOutputMode.Sdr, backend.OutputMode);
        MediaDiagnosticInfo warning = Assert.Single(raised);
        Assert.Equal(MediaDiagnosticLevel.Warning, warning.Level);
        Assert.Contains("ResizeBuffers", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedSwitchBackToSdr_IsNotRetriedEveryFrame_AndKeepsDrawingScRgb()
    {
        var backend = new FakeHdrBackend { FailRevertWith = "ResizeBuffers to B8G8R8A8_UNorm failed" };
        var controller = new D3D11HdrOutputController(true, _ => HdrMonitor);
        controller.PrimeDisplay(backend.Hwnd);

        Assert.Equal(HdrOutputMode.ScRgb, controller.PreparePrimary(backend, Pq4000, blocker: null)?.Mode);

        // SDR video: going back fails and the buffers stay FP16. The caller is told to draw
        // scRGB (what the buffer holds), and the switch is not tried again every frame.
        for (var frame = 0; frame < 5; frame++)
        {
            HdrOutputDecision? output = controller.PreparePrimary(backend, null, blocker: null);
            Assert.Equal(HdrOutputMode.ScRgb, output?.Mode);
            Assert.Equal(200, output?.SdrWhiteNits);
        }

        Assert.Equal(2, backend.ModeChanges);

        // A resize gives the swap chain new buffers: one more try, which now works.
        backend.Width = 32;
        backend.FailRevertWith = null;
        Assert.Null(controller.PreparePrimary(backend, null, blocker: null));
        Assert.Equal(HdrOutputMode.Sdr, backend.OutputMode);
        Assert.Equal(3, backend.ModeChanges);
    }

    [Fact]
    public void AnOffscreenPrimary_StaysSdr()
    {
        var controller = new D3D11HdrOutputController(true, _ => HdrMonitor);
        using var offscreen = new FakeBackend();

        Assert.Null(controller.PreparePrimary(offscreen, Pq4000, blocker: null));
        Assert.Equal(D3D11HdrOutputController.OffscreenSurfaceReason, controller.LastReported?.Reason);
    }

    [Fact]
    public void NativeLayouts_HaveTheirWindowsSizes()
    {
        Assert.Equal(28, Marshal.SizeOf<D3D11HdrSwapChain.DxgiHdrMetadataHdr10>());
        Assert.Equal(24, Marshal.SizeOf<Win32MonitorHdr.DisplayConfigSdrWhiteLevel>());

        D3D11HdrSwapChain.DxgiHdrMetadataHdr10 native = D3D11HdrSwapChain.ToNative(HdrOutputPolicy.CreateMetadata(1000, 400, 0.005));
        Assert.Equal((35400, 14600, 15635, 16450), (native.RedX, native.RedY, native.WhiteX, native.WhiteY));
        Assert.Equal((1000u, 50u, (ushort)1000, (ushort)400), (native.MaxMasteringLuminance, native.MinMasteringLuminance, native.MaxContentLightLevel, native.MaxFrameAverageLightLevel));
    }

    [Fact]
    public void TryGet_InvalidWindow_ReturnsNull() => Assert.Null(Win32MonitorHdr.TryGet(0));

    // --- WARP ------------------------------------------------------------------------------

    [Fact]
    public void ACompositionSwapChain_SwitchesFormatOrSaysWhyNot()
    {
        using var deviceResources = new D3D11DeviceResources(Direct3D11DeviceSelection.Warp);
        Assert.True(deviceResources.Initialize(new MediaPlayerConfiguration()));
        using IDXGIDevice dxgiDevice = deviceResources.Device!.QueryInterface<IDXGIDevice>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
        using IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>();
        using IDXGISwapChain1 swapChain = factory.CreateSwapChainForComposition(
            deviceResources.Device,
            Core.Graphics.D3D.SwapChain.D3D11SwapChainFactory.CreateCompositionSwapChainDescription(16, 8, AlphaMode.Premultiplied));

        var applied = D3D11HdrSwapChain.TryApply(swapChain, 16, 8, HdrOutputMode.ScRgb, HdrOutputPolicy.CreateMetadata(1000), out var failure);
        using (ID3D11Texture2D buffer = swapChain.GetBuffer<ID3D11Texture2D>(0))
        {
            // Either the switch worked, or it failed, said why and left an SDR buffer.
            Assert.Equal(applied ? HdrOutputMode.ScRgb : HdrOutputMode.Sdr, D3D11HdrSwapChain.ModeOf(buffer));
            Assert.Equal(applied, failure == null);
            Assert.Equal((16u, 8u), (buffer.Description.Width, buffer.Description.Height));
        }

        Assert.True(D3D11HdrSwapChain.TryApply(swapChain, 16, 8, HdrOutputMode.Sdr, null, out failure), failure);
        using ID3D11Texture2D sdrBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
        Assert.Equal(HdrOutputMode.Sdr, D3D11HdrSwapChain.ModeOf(sdrBuffer));
    }

    [Fact]
    public void SdrLayers_LandInAnScRgbTargetAtTheSdrWhiteLevel()
    {
        using var deviceResources = new D3D11DeviceResources(Direct3D11DeviceSelection.Warp);
        Assert.True(deviceResources.Initialize(new MediaPlayerConfiguration()));
        var compositor = new D3D11MediaCompositionPresenter();
        try
        {
            // White, then a half-transparent red wash: on scRGB, SDR white is 200 / 80 = 2.5, and
            // the wash blends in linear light.
            MediaCompositionFrame frame = MediaCompositionFrame.Empty(MediaCompositionClearMode.ConfiguredColor, 0xFFFFFFFF) with { WashArgb = 0x80FF0000 };
            HdrOutputDecision decision = HdrOutputPolicy.Choose(true, HdrTransferFunction.Pq, HdrMonitor);
            var alpha = 0x80 / 255.0;

            float[] scRgb = Compose(deviceResources, compositor, frame, Format.R16G16B16A16_Float, HdrOutputMode.ScRgb, decision);
            Assert.Equal(2.5, scRgb[0], 0.01);
            Assert.Equal(2.5 * (1 - alpha), scRgb[1], 0.01);
            Assert.Equal(2.5 * (1 - alpha), scRgb[2], 0.01);

            // An SDR target of the same frame is unchanged by HDR output: gamma-space blending.
            float[] sdr = Compose(deviceResources, compositor, frame, Format.B8G8R8A8_UNorm, HdrOutputMode.Sdr, null);
            Assert.Equal(1.0, sdr[2], 0.01);        // red (BGRA)
            Assert.Equal(1 - alpha, sdr[1], 0.01);  // green
        }
        finally
        {
            compositor.Dispose();
        }
    }

    private static float[] Compose(
        D3D11DeviceResources deviceResources,
        D3D11MediaCompositionPresenter compositor,
        MediaCompositionFrame frame,
        Format format,
        HdrOutputMode mode,
        HdrOutputDecision? decision)
    {
        ID3D11Device device = deviceResources.Device!;
        ID3D11DeviceContext context = deviceResources.Context!;
        const int width = 8, height = 4;
        using ID3D11Texture2D target = device.CreateTexture2D(new Texture2DDescription(format, width, height, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
        using ID3D11Texture2D staging = device.CreateTexture2D(new Texture2DDescription(format, width, height, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        var snapshot = new D3D11PresentationTargetSnapshot(
            CompositionTargetDescriptor.PrimaryFinalTargetId,
            target,
            width,
            height,
            IntPtr.Zero,
            D3D11PresentationBackendKind.ManagedFrameSink,
            "test",
            revision: format == Format.B8G8R8A8_UNorm ? 1 : 2,
            CompositionTargetKind.FinalPresentation,
            outputMode: mode);

        Assert.True(compositor.PresentToTargets(deviceResources, frame, [snapshot], null, decision, out _), compositor.LastFailureReason);

        context.CopyResource(staging, target);
        MappedSubresource mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            if (format == Format.R16G16B16A16_Float)
            {
                var halves = new short[4];
                Marshal.Copy(mapped.DataPointer, halves, 0, 4);
                return [.. halves.Select(h => (float)BitConverter.Int16BitsToHalf(h))];
            }

            var bytes = new byte[4];
            Marshal.Copy(mapped.DataPointer, bytes, 0, 4);
            return [.. bytes.Select(b => b / 255f)];
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    // --- Helpers ---------------------------------------------------------------------------

    private static HdrOutputDecision ScRgbDecision(double displayPeak) =>
        HdrOutputPolicy.Choose(true, HdrTransferFunction.Pq, new HdrDisplayInfo(true, displayPeak, 200));

    private static D3D11HdrToneMapPresenter.ToneMapConstants Constants(HdrToneMapRequest request, HdrOutputDecision? decision) =>
        D3D11HdrToneMapPresenter.CreateConstants(
            Hdr10, request, 16, 16, 16, 16, new Vortice.RawRect(0, 0, 16, 16), new Vortice.RawRect(0, 0, 16, 16), decision);

    // The PSToneMap shader with scRGB output (Output.w = 1), line for line.
    private static double[] ShaderScRgb(int y, int cb, int cr, HdrToneMapRequest request, HdrOutputDecision decision)
    {
        D3D11HdrToneMapPresenter.ToneMapConstants k = Constants(request, decision);
        Assert.Equal(1f, k.Output.W);
        double Sample(int code) => code * 64 / 65535.0;

        var yy = (Sample(y) * k.YuvScaleOffset.X) + k.YuvScaleOffset.Y;
        var u = (Sample(cb) * k.YuvScaleOffset.Z) + k.YuvScaleOffset.W;
        var v = (Sample(cr) * k.YuvScaleOffset.Z) + k.YuvScaleOffset.W;
        double[] signal = [yy + (k.Matrix.X * v), yy - (k.Matrix.Y * u) - (k.Matrix.Z * v), yy + (k.Matrix.W * u)];

        double[] nits = k.Transfer.X > 0.5 ? HlgToNits(signal, k) : [.. signal.Select(s => PqCurve.ToNits(Math.Clamp(s, 0, 1)))];
        var brightest = nits.Max();
        var gain = brightest > 0 ? ToneCurve(brightest, k) / brightest : 1;
        double[] l = [.. nits.Select(n => n * gain * k.Output.X)];
        return
        [
            (1.660491 * l[0]) - (0.587641 * l[1]) - (0.072850 * l[2]),
            (-0.124550 * l[0]) + (1.132900 * l[1]) - (0.008349 * l[2]),
            (-0.018151 * l[0]) - (0.100579 * l[1]) + (1.118730 * l[2]),
        ];
    }

    private static double[] HlgToNits(double[] signal, D3D11HdrToneMapPresenter.ToneMapConstants k)
    {
        const double a = 0.17883277, b = 0.28466892, c = 0.55991073;
        double[] scene = [.. signal.Select(s => Math.Clamp(s, 0, 1)).Select(s => s <= 0.5 ? s * s / 3 : (Math.Exp((s - c) / a) + b) / 12)];
        var luminance = (0.2627 * scene[0]) + (0.6780 * scene[1]) + (0.0593 * scene[2]);
        var ootf = luminance > 0 ? k.Transfer.Y * Math.Pow(luminance, k.Transfer.Z) : 0;
        return [.. scene.Select(e => e * ootf)];
    }

    private static double ToneCurve(double nits, D3D11HdrToneMapPresenter.ToneMapConstants k)
    {
        if (k.Curve.W < 0.5)
            return Math.Min(nits, k.Output.Z);

        var e1 = Math.Min(PqCurve.FromNits(nits) / k.Curve.X, 1);
        double kneeStart = k.Curve.Z, e2 = e1;
        if (e1 > kneeStart)
        {
            var t = (e1 - kneeStart) / (1 - kneeStart);
            var t2 = t * t;
            var t3 = t2 * t;
            e2 = (((2 * t3) - (3 * t2) + 1) * kneeStart) + ((t3 - (2 * t2) + t) * (1 - kneeStart)) + (((-2 * t3) + (3 * t2)) * k.Curve.Y);
        }

        return Math.Min(PqCurve.ToNits(e2 * k.Curve.X), k.Output.Z);
    }

    private static List<MediaDiagnosticInfo> Capture(Action action)
    {
        var captured = new ConcurrentQueue<MediaDiagnosticInfo>();
        void Record(MediaDiagnosticInfo info)
        {
            if (info.Category == Category)
                captured.Enqueue(info);
        }

        DiagnosticRouter.DiagnosticOccurred += Record;
        try
        {
            action();
        }
        finally
        {
            DiagnosticRouter.DiagnosticOccurred -= Record;
        }

        return [.. captured];
    }

    private class FakeBackend : ID3D11PresentationBackend
    {
        public D3D11PresentationBackendKind Kind => D3D11PresentationBackendKind.ManagedFrameSink;
        public bool IsActive => true;
        public int Width { get; set; } = 16;
        public int Height => 8;
        public ID3D11Texture2D? TargetTexture => null;
        public D3D11TargetViewCache TargetViews { get; } = new();
        public bool Present(int liveResizeFlag, bool syncImmediate) => true;
        public void Cleanup() { }
        public void Dispose() { }
    }

    private sealed class FakeHdrBackend : FakeBackend, ID3D11HdrOutputBackend
    {
        public IntPtr Hwnd => 0x1234;
        public HdrOutputMode OutputMode { get; private set; }
        public string? HdrOutputFailure { get; private set; }
        public string? FailWith { get; init; }

        // Going back to SDR fails and leaves the buffers FP16 (a transient ResizeBuffers failure).
        public string? FailRevertWith { get; set; }
        public int ModeChanges { get; private set; }
        public Hdr10MetadataValues? Metadata { get; private set; }

        public bool TrySetOutputMode(HdrOutputMode mode, Hdr10MetadataValues? metadata)
        {
            if (mode == OutputMode)
            {
                Metadata = metadata;
                return true;
            }

            ModeChanges++;
            if (mode == HdrOutputMode.Sdr && FailRevertWith != null)
            {
                HdrOutputFailure = FailRevertWith;
                return false;
            }

            if (FailWith != null)
            {
                HdrOutputFailure = FailWith;
                return false;
            }

            OutputMode = mode;
            Metadata = metadata;
            return true;
        }
    }
}
