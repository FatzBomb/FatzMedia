using FatzBomb.MediaControl.Core.Graphics.Composition;

namespace FatzBomb.MediaControl.Tests;

public sealed class CompositionPresentOutcomeTests
{
    [Fact]
    public void NoTargets_IsNotSuccess() => Assert.False(new CompositionPresentOutcome().Succeeded);

    [Fact]
    public void WithPrimary_SuccessIsThePrimarysPresent()
    {
        var failedPrimary = new CompositionPresentOutcome();
        failedPrimary.Record(isPrimary: false, presented: true);
        failedPrimary.Record(isPrimary: true, presented: false);

        var presentedPrimary = new CompositionPresentOutcome();
        presentedPrimary.Record(isPrimary: true, presented: true);
        presentedPrimary.Record(isPrimary: false, presented: false);

        Assert.False(failedPrimary.Succeeded);
        Assert.True(presentedPrimary.Succeeded);
    }

    [Fact]
    public void WithoutPrimary_AnyPresentedSecondaryIsSuccess()
    {
        var outcome = new CompositionPresentOutcome();
        outcome.Record(isPrimary: false, presented: false);
        Assert.False(outcome.Succeeded);

        outcome.Record(isPrimary: false, presented: true);
        Assert.True(outcome.Succeeded);
    }
}
