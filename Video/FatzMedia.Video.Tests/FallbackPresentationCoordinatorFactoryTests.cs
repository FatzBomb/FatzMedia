using FatzMedia.Video.Controllers;
using FatzMedia.Video.Core.Abstractions;
using FatzMedia.Video.Core.Configuration;

namespace FatzMedia.Video.Tests;

/// <summary>Stage 036A: choosing the first usable renderer (for example Direct3D11, then software).</summary>
public sealed class FallbackPresentationCoordinatorFactoryTests
{
    [Fact]
    public void TheFirstAvailableCandidate_IsSelected_AndLaterOnesAreNotProbed()
    {
        var gpu = new FakeFactory("Direct3D11", MediaPresentationAvailability.Available("GPU, Test Adapter"));
        var software = new FakeFactory("Software", MediaPresentationAvailability.Available("unused"));
        var chain = new FallbackPresentationCoordinatorFactory(gpu, software);

        Assert.Equal("Direct3D11", chain.BackendName);
        Assert.Same(gpu, chain.Selected);
        var only = Assert.Single(chain.Selection);
        Assert.Equal(("Direct3D11", true, "GPU, Test Adapter", true), (only.BackendName, only.IsAvailable, only.Detail, only.IsSelected));
        Assert.Equal(0, software.ProbeCount);
    }

    [Fact]
    public void AnUnavailableCandidate_IsSkipped_WithItsReason()
    {
        var gpu = new FakeFactory("Direct3D11", MediaPresentationAvailability.Unavailable("no device"));
        var software = new FakeFactory("Software", MediaPresentationAvailability.Available("CPU"));
        var chain = new FallbackPresentationCoordinatorFactory(gpu, software);

        Assert.Same(software, chain.Selected);
        Assert.Equal(
            [("Direct3D11", false, "no device", false), ("Software", true, "CPU", true)],
            chain.Selection.Select(static result => (result.BackendName, result.IsAvailable, result.Detail, result.IsSelected)));
    }

    [Fact]
    public void SelectedAvailability_CarriesWhatTheRendererNeeds()
    {
        var gpu = new FakeFactory("Direct3D11", MediaPresentationAvailability.Unavailable("no device"));
        var software = new FakeFactory(
            "Software",
            MediaPresentationAvailability.Available("CPU") with { RequiresSoftwareDecoding = true, RequiresFrameSinkSurface = true });
        var chain = new FallbackPresentationCoordinatorFactory(gpu, software);

        Assert.True(chain.SelectedAvailability.RequiresSoftwareDecoding);
        Assert.True(chain.SelectedAvailability.RequiresFrameSinkSurface);
    }

    [Fact]
    public void ACandidateWithoutAProbe_IsAlwaysAvailable()
    {
        var gpu = new FakeFactory("Direct3D11", MediaPresentationAvailability.Unavailable("no device"));
        var plain = new PlainFactory("Software");

        var chain = new FallbackPresentationCoordinatorFactory(gpu, plain);

        Assert.Same(plain, chain.Selected);
        Assert.True(chain.Selection[^1].IsAvailable);
    }

    [Fact]
    public void WhenNothingIsAvailable_TheLastCandidateIsUsed()
    {
        var gpu = new FakeFactory("Direct3D11", MediaPresentationAvailability.Unavailable("no device"));
        var software = new FakeFactory("Software", MediaPresentationAvailability.Unavailable("disabled"));
        var chain = new FallbackPresentationCoordinatorFactory(gpu, software);

        Assert.Same(software, chain.Selected);
        Assert.Equal(("Software", false, "disabled", true), (chain.Selection[^1].BackendName, chain.Selection[^1].IsAvailable, chain.Selection[^1].Detail, chain.Selection[^1].IsSelected));
        Assert.False(chain.Selection[0].IsSelected);
    }

    [Fact]
    public void AThrowingProbe_CountsAsUnavailable()
    {
        var gpu = new FakeFactory("Direct3D11", () => throw new InvalidOperationException("driver crashed"));
        var software = new FakeFactory("Software", MediaPresentationAvailability.Available("CPU"));
        var chain = new FallbackPresentationCoordinatorFactory(gpu, software);

        Assert.Same(software, chain.Selected);
        Assert.Equal("probe failed: driver crashed", chain.Selection[0].Detail);
    }

    [Fact]
    public void SelectionRunsOnce_AndCreateUsesTheSelectedCandidate()
    {
        var gpu = new FakeFactory("Direct3D11", MediaPresentationAvailability.Available("GPU"));
        var chain = new FallbackPresentationCoordinatorFactory(gpu, new PlainFactory("Software"));

        _ = chain.BackendName;
        _ = chain.Selection;
        _ = chain.Create(null!, new MediaPlayerConfiguration());

        Assert.Equal(1, gpu.ProbeCount);
        Assert.Equal(1, gpu.CreateCount);
    }

    [Fact]
    public void Construction_RequiresAtLeastOneNonNullCandidate()
    {
        _ = Assert.Throws<ArgumentException>(() => new FallbackPresentationCoordinatorFactory());
        _ = Assert.Throws<ArgumentException>(() => new FallbackPresentationCoordinatorFactory(new PlainFactory("A"), null!));
        _ = Assert.Throws<ArgumentNullException>(() => new FallbackPresentationCoordinatorFactory(null!));
    }

    class PlainFactory(string backendName) : IMediaPresentationCoordinatorFactory
    {
        public int CreateCount { get; private set; }

        public string BackendName { get; } = backendName;

        public IMediaPresentationCoordinator Create(IMediaEngine mediaEngine, MediaPlayerConfiguration configuration)
        {
            CreateCount++;
            return null!;
        }
    }

    sealed class FakeFactory(string backendName, Func<MediaPresentationAvailability> probe)
        : PlainFactory(backendName), IMediaPresentationAvailabilityProbe
    {
        public FakeFactory(string backendName, MediaPresentationAvailability availability)
            : this(backendName, () => availability)
        {
        }

        public int ProbeCount { get; private set; }

        public MediaPresentationAvailability ProbeAvailability()
        {
            ProbeCount++;
            return probe();
        }
    }
}
