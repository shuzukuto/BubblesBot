using BubblesBot.Bot.Modes;
using BubblesBot.Bot.Systems;

namespace BubblesBot.Tests;

public sealed class SimulacrumRunModeTests
{
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    public void One_shot_discard_has_precedence_over_existing_portal_resume(
        bool discard, bool portalPresent, bool expected)
    {
        Assert.Equal(expected,
            SimulacrumRunMode.ShouldResumeExistingPortal(discard, portalPresent));
    }

    [Theory]
    [InlineData(AreaRole.Map, AreaRole.Unknown, AreaTransitionOutcome.VerifyingDestination, 10u, 20u, true)]
    [InlineData(AreaRole.Map, AreaRole.SafeHub, AreaTransitionOutcome.UnexpectedDestination, 10u, 20u, false)]
    [InlineData(AreaRole.SafeHub, AreaRole.Unknown, AreaTransitionOutcome.VerifyingDestination, 10u, 20u, false)]
    [InlineData(AreaRole.Map, AreaRole.Unknown, AreaTransitionOutcome.TimedOut, 10u, 20u, false)]
    [InlineData(AreaRole.Map, AreaRole.Unknown, AreaTransitionOutcome.VerifyingDestination, 10u, 10u, false)]
    public void Fresh_map_portal_can_attach_an_empty_destination_bubble(
        AreaRole expectedDestination,
        AreaRole observedRole,
        AreaTransitionOutcome outcome,
        uint originHash,
        uint observedHash,
        bool expected)
    {
        var transition = new AreaTransitionState(
            "intent", originHash, AreaRole.SafeHub, expectedDestination,
            observedHash, observedRole, outcome, 1000);

        Assert.Equal(expected,
            SimulacrumRunMode.ShouldAttachFreshUnknownMap(transition));
    }
}
