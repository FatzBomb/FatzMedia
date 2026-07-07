using FatzBombMedia.MediaControl.Core.Audio;

namespace FatzBombMedia.MediaControl.Tests;

public class BassOutputLatencyProviderTests
{
    [Fact]
    public void ApplyHeuristic_WhenDisabled_ReturnsReportedValue()
    {
        var result = BassOutputLatencyProvider.ApplyHeuristic(0.005, "USB Audio Device", enableHeuristic: false);

        Assert.Equal(0.005, result, 6);
    }

    [Fact]
    public void ApplyHeuristic_WhenReportedAboveThreshold_ReturnsReportedValue()
    {
        var result = BassOutputLatencyProvider.ApplyHeuristic(0.080, "USB Audio Device", enableHeuristic: true);

        Assert.Equal(0.080, result, 6);
    }

    [Fact]
    public void ApplyHeuristic_AtThreshold_ReturnsReportedValue()
    {
        var result = BassOutputLatencyProvider.ApplyHeuristic(0.010, "USB Audio Device", enableHeuristic: true);

        Assert.Equal(0.010, result, 6);
    }

    [Fact]
    public void ApplyHeuristic_BelowThreshold_UsbDevice_ReturnsUsbOffset()
    {
        var result = BassOutputLatencyProvider.ApplyHeuristic(0.005, "USB Audio DAC", enableHeuristic: true);

        Assert.Equal(0.050, result, 6);
    }

    [Fact]
    public void ApplyHeuristic_BelowThreshold_UsbDeviceUpperCase_ReturnsUsbOffset()
    {
        var result = BassOutputLatencyProvider.ApplyHeuristic(0.005, "SPEAKERS (USB)", enableHeuristic: true);

        Assert.Equal(0.050, result, 6);
    }

    [Fact]
    public void ApplyHeuristic_BelowThreshold_BluetoothDevice_ReturnsBluetoothOffset()
    {
        var result = BassOutputLatencyProvider.ApplyHeuristic(0.005, "Bluetooth Headphones", enableHeuristic: true);

        Assert.Equal(0.150, result, 6);
    }

    [Fact]
    public void ApplyHeuristic_BelowThreshold_BluetoothAbbreviated_ReturnsBluetoothOffset()
    {
        var result = BassOutputLatencyProvider.ApplyHeuristic(0.005, "Headset (BT)", enableHeuristic: true);

        Assert.Equal(0.150, result, 6);
    }

    [Fact]
    public void ApplyHeuristic_BelowThreshold_NoMatchingName_ReturnsReportedValue()
    {
        var result = BassOutputLatencyProvider.ApplyHeuristic(0.005, "Realtek High Definition Audio", enableHeuristic: true);

        Assert.Equal(0.005, result, 6);
    }

    [Fact]
    public void ApplyHeuristic_BelowThreshold_NullName_ReturnsReportedValue()
    {
        var result = BassOutputLatencyProvider.ApplyHeuristic(0.005, null, enableHeuristic: true);

        Assert.Equal(0.005, result, 6);
    }

    [Fact]
    public void ApplyHeuristic_BelowThreshold_EmptyName_ReturnsReportedValue()
    {
        var result = BassOutputLatencyProvider.ApplyHeuristic(0.005, string.Empty, enableHeuristic: true);

        Assert.Equal(0.005, result, 6);
    }

    [Fact]
    public void BassOutputLatencyProvider_DefaultCtor_HasHeuristicDisabled()
    {
        var provider = new BassOutputLatencyProvider();

        _ = Assert.IsType<BassOutputLatencyProvider>(provider);
    }
}
