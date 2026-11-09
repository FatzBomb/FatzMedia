using FatzMedia.Video.Core.Graphics.D3D.Diagnostics;
using SharpGen.Runtime;
using DxgiResultCode = Vortice.DXGI.ResultCode;

namespace FatzMedia.Video.Tests;

/// <summary>
/// Covers the pure, deterministic HRESULT-classification logic used for GPU device-lost
/// detection (Stage 005A/005B). This is the one part of the device-lost feature that can be
/// unit-tested without real D3D11 hardware/driver behavior; the actual detect-and-recover flow
/// (Present/ResizeBuffers/Commit against a real swap chain and device) requires a live GPU and is
/// covered only by manual smoke testing per docs/VALIDATION.md.
/// </summary>
public class D3D11DeviceLostClassifierTests
{
    public static TheoryData<Result, string> DeviceLostResults() => new()
        {
            { DxgiResultCode.DeviceRemoved, "Device removed" },
            { DxgiResultCode.DeviceReset, "Device reset" },
            { DxgiResultCode.DeviceHung, "Device hung" },
            { DxgiResultCode.DriverInternalError, "Driver internal error" }
        };

    [Theory]
#pragma warning disable xUnit1044 // Avoid using TheoryData type arguments that are not serializable
    [MemberData(nameof(DeviceLostResults))]
#pragma warning restore xUnit1044 // Avoid using TheoryData type arguments that are not serializable
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
#pragma warning disable xUnit1044 // Avoid using TheoryData type arguments that are not serializable
    [MemberData(nameof(DeviceLostResults))]
#pragma warning restore xUnit1044 // Avoid using TheoryData type arguments that are not serializable
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
}
