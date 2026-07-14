using FatzBomb.MediaControl.Core.Audio;

namespace FatzBomb.MediaControl.Tests;

public class OutputLatencyHeuristicTests
{
    [Theory]
    [InlineData(0.005, false, false)]
    [InlineData(0.010, true, false)]
    [InlineData(0.080, true, false)]
    [InlineData(0.005, true, true)]
    public void RequiresDeviceName_OnlyRequestsNativeDeviceForLowEnabledReports(
        double reportedSeconds,
        bool enabled,
        bool expected) =>
        Assert.Equal(expected, OutputLatencyHeuristic.RequiresDeviceName(reportedSeconds, enabled));

    [Fact]
    public void ApplyHeuristic_WhenDisabled_ReturnsReportedValue()
    {
        var result = OutputLatencyHeuristic.Apply(0.005, "USB Audio Device", enabled: false);

        Assert.Equal(0.005, result, 6);
    }

    [Fact]
    public void ApplyHeuristic_WhenReportedAboveThreshold_ReturnsReportedValue()
    {
        var result = OutputLatencyHeuristic.Apply(0.080, "USB Audio Device", enabled: true);

        Assert.Equal(0.080, result, 6);
    }

    [Fact]
    public void ApplyHeuristic_AtThreshold_ReturnsReportedValue()
    {
        var result = OutputLatencyHeuristic.Apply(0.010, "USB Audio Device", enabled: true);

        Assert.Equal(0.010, result, 6);
    }

    [Fact]
    public void ApplyHeuristic_BelowThreshold_UsbDevice_ReturnsUsbOffset()
    {
        var result = OutputLatencyHeuristic.Apply(0.005, "USB Audio DAC", enabled: true);

        Assert.Equal(0.050, result, 6);
    }

    [Fact]
    public void ApplyHeuristic_BelowThreshold_UsbDeviceUpperCase_ReturnsUsbOffset()
    {
        var result = OutputLatencyHeuristic.Apply(0.005, "SPEAKERS (USB)", enabled: true);

        Assert.Equal(0.050, result, 6);
    }

    [Fact]
    public void ApplyHeuristic_BelowThreshold_BluetoothDevice_ReturnsBluetoothOffset()
    {
        var result = OutputLatencyHeuristic.Apply(0.005, "Bluetooth Headphones", enabled: true);

        Assert.Equal(0.150, result, 6);
    }

    [Fact]
    public void ApplyHeuristic_BelowThreshold_BluetoothAbbreviated_ReturnsBluetoothOffset()
    {
        var result = OutputLatencyHeuristic.Apply(0.005, "Headset (BT)", enabled: true);

        Assert.Equal(0.150, result, 6);
    }

    [Fact]
    public void ApplyHeuristic_BelowThreshold_NoMatchingName_ReturnsReportedValue()
    {
        var result = OutputLatencyHeuristic.Apply(0.005, "Realtek High Definition Audio", enabled: true);

        Assert.Equal(0.005, result, 6);
    }

    [Fact]
    public void ApplyHeuristic_BelowThreshold_NullName_ReturnsReportedValue()
    {
        var result = OutputLatencyHeuristic.Apply(0.005, null, enabled: true);

        Assert.Equal(0.005, result, 6);
    }

    [Fact]
    public void ApplyHeuristic_BelowThreshold_EmptyName_ReturnsReportedValue()
    {
        var result = OutputLatencyHeuristic.Apply(0.005, string.Empty, enabled: true);

        Assert.Equal(0.005, result, 6);
    }

}
