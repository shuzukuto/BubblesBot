using BubblesBot.Core.Knowledge;

namespace BubblesBot.Tests;

public sealed class EntityDispositionCatalogTests
{
    [Theory]
    [InlineData("Metadata/Monsters/InvisibleFire/AfflictionBossFinalDeathZone@75")]
    [InlineData("Metadata/Monsters/LeagueAffliction/Demons/FinalBossDeathZones/LightningVolatileObject")]
    public void SimulacrumBossDeathEffectsAreHazards(string path)
        => Assert.Equal(EntityDisposition.Hazard, EntityDispositionCatalog.Classify(path));

    [Fact]
    public void OrdinarySimulacrumMonsterRemainsCombatant()
        => Assert.Equal(
            EntityDisposition.Combatant,
            EntityDispositionCatalog.Classify(
                "Metadata/Monsters/LeagueAffliction/AfflictionDemonBossFinal@75"));
}
