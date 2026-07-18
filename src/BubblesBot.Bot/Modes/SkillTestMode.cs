using BubblesBot.Bot.Behaviors;
using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Bot.Systems;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Modes;

/// <summary>
/// Smallest possible test of the combat plumbing. While armed, taps the first non-Walk slot
/// in the user's <see cref="SkillProfile"/> at a configurable aim — once per
/// <see cref="BotSettings.SkillTestIntervalMs"/>. No HP guard, no cooldown reads, no
/// movement. Just <c>HoverAt + TapKey</c>.
///
/// <para>Use case: bind Frostblink to slot 1, set Mode = SkillTest in dashboard, arm,
/// stand in a hideout. Frostblink should fire once per interval toward where the cursor
/// projects (default = closest enemy or self if no enemies). If it doesn't, something is
/// wrong in the input/aim layer and we want to find that BEFORE composing combat behavior
/// trees on top.</para>
/// </summary>
public sealed class SkillTestMode : IBotMode
{
    private readonly Func<GameSnapshot?> _getSnapshot;
    private readonly Func<LivePlayer?>   _getLive;
    private readonly Func<EntityCache?>  _getEntities;
    private readonly SettingsStore _settings;
    private readonly CombatSystem  _combat = new();
    private readonly SkillBook     _skills = new();
    private readonly IBehavior     _root;

    public string Name => "Skill test";
    public IBehavior Root => _root;
    public string LastDecision { get; private set; } = "init";

    public SkillTestMode(SettingsStore settings, Func<GameSnapshot?> getSnapshot, Func<LivePlayer?> getLive, Func<EntityCache?> getEntities)
    {
        _settings = settings;
        _getSnapshot = getSnapshot;
        _getLive = getLive;
        _getEntities = getEntities;

        // Cooldown wraps Cast so we tap at most once per interval. PickFirstActiveSkill
        // grabs whatever non-Walk slot the user has bound — typically slot 1 (default R).
        // Cluster → closest enemy → self. This way the test works in a hideout (no enemies →
        // self-aim makes Frostblink/dashes fire in place; instant cast skills self-cast) AND
        // in combat zones where it'll naturally aim at packs.
        // Pass _skills into Cast so it consults real cooldowns + tracks last-cast time.
        // Cast itself returns Failure when SkillBook says "not ready" — Cooldown wrapper
        // is now redundant for skills with real cooldowns but kept as a min-tap-rate floor
        // for skills with no cooldown (e.g. basic attacks where IsReady would always pass).
        _root = new Cooldown("test interval",
            TimeSpan.FromMilliseconds(settings.Current.SkillTestIntervalMs),
            new Behaviors.Combat.Cast("test cast", _combat,
                ctx => PickFirstActiveSkill(ctx.Settings),
                Aim.BestEffort(60f), _skills));
    }

    public void Reset()
    {
        _combat.StopAllChannels();
        _skills.Reset();
        _root.Reset();
        LastDecision = "reset";
    }

    public void Tick(GameSnapshot snapshot, IInputRouter input)
    {
        // Hand the SkillBook the live Actor component address each tick so the cooldown
        // reader has somewhere to look. Player can be null on loading screens — handled.
        if (snapshot.Player is { } pv)
            _skills.SetActorContext(pv.ActorComponentAddress);
        if (_skills.CooldownReader is null)
            _skills.CooldownReader = new SkillCooldownReader(snapshot.Reader);

        var ctx = new BehaviorContext(snapshot, input, _settings.Current, _getLive(), _getEntities());
        var status = _root.Tick(ctx);
        var slot = PickFirstActiveSkill(_settings.Current);
        var ready = slot is null ? "?" : (_skills.IsReady(slot) ? "READY" : "WAITING");
        LastDecision = slot is null
            ? $"no active skill bound (status={status})"
            : $"{slot.Name} vk=0x{slot.Vk:X2} gem={slot.GemId} {ready} (status={status})";
    }

    private static SkillSlot? PickFirstActiveSkill(BotSettings settings)
    {
        // Skip Walk and Disabled. First eligible slot wins.
        foreach (var s in settings.Skills.Slots)
            if (s.Vk != 0 && s.Role is not SkillRole.Walk and not SkillRole.Disabled)
                return s;
        return null;
    }
}
