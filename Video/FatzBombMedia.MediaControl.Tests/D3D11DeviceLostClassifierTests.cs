using FatzBombMedia.MediaControl.Core.Graphics.D3D.Diagnostics;
using SharpGen.Runtime;
using DxgiResultCode = Vortice.DXGI.ResultCode;

namespace FatzBombMedia.MediaControl.Tests;

/// <summary>
/// Covers the pure, deterministic HRESULT-classification logic used for GPU device-lost
/// detection (Stage 005A/005B). This is the one part of the device-lost feature that can be
/// unit-tested without real D3D11 hardware/driver behavior; the actual detect-and-recover flow
/// (Present/ResizeBuffers/Commit against a real swap chain and device) requires a live GPU and is
/// covered only by manual smoke testing per docs/VALIDATION.md.
/// </summary>
public class D3D11DeviceLostClassifierTests
{
#pragma warning disable xUnit1044 // Result is not serializable; Test Explorer row enumeration is cosmetic.
    public static TheoryData<Result, string> DeviceLostResults() =>
#pragma warning restore xUnit1044
        new()
        {
            { DxgiResultCode.DeviceRemoved, "Device removed" },
            { DxgiResultCode.DeviceReset, "Device reset" },
            { DxgiResultCode.DeviceHung, "Device hung" },
            { DxgiResultCode.DriverInternalError, "Driver internal error" }
        };

    [Theory]
    [MemberData(nameof(DeviceLostResults))]
    public void IsDeviceLost_ReturnsTrue_ForDeviceLostResults(Result result, string expectedPrefix)
    {
        Assert.NotNull(expectedPrefix);
        Assert.True(D3D11DeviceLostClassifier.IsDeviceLost(result));
    }

    [Fact]
    public void IsDeviceLost_ReturnsFalse_ForSuccess()
    {
        Result result = 0; // S_OK

        Assert.False(D3D11DeviceLostClassifier.IsDeviceLost(result));
    }

    [Fact]
    public void IsDeviceLost_ReturnsFalse_ForGenericFailure()
    {
        Result result = unchecked((int)0x80004005); // E_FAIL

        Assert.False(D3D11DeviceLostClassifier.IsDeviceLost(result));
    }

    [Theory]
    [MemberData(nameof(DeviceLostResults))]
    public void Describe_UsesClassifiedName_ForDeviceLostResults(Result result, string expectedPrefix)
    {
        var description = D3D11DeviceLostClassifier.Describe(result, swapChain: null);

        Assert.StartsWith($"{expectedPrefix}: ", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_DoesNotThrow_WhenSwapChainIsNull()
    {
        Result result = DxgiResultCode.DeviceRemoved;

        var description = D3D11DeviceLostClassifier.Describe(result, swapChain: null);

        Assert.False(string.IsNullOrWhiteSpace(description));
    }

    [Fact]
    public void Describe_FallsBackToGenericClassification_ForUnclassifiedFailure()
    {
        Result result = unchecked((int)0x80004005); // E_FAIL

        var description = D3D11DeviceLostClassifier.Describe(result, swapChain: null);

        Assert.StartsWith("Device lost: ", description, StringComparison.Ordinal);
    }

    [Fact]
    public void IsDeviceLost_DevicePointerOverload_ReturnsFalse_ForZeroPointer() =>
        // The decode-side overload must treat a null/zero device pointer as "not lost" (nothing to
        // check) rather than throwing — this is the only branch of that overload testable without a
        // real D3D11 device.
        Assert.False(D3D11DeviceLostClassifier.IsDeviceLost(IntPtr.Zero));
}
