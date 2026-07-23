using BubblesBot.Bot.Input;
using BubblesBot.Bot.Settings;
using BubblesBot.Core.Game;
using BubblesBot.Core.Snapshot;
using System;
using System.Linq;

namespace BubblesBot.Bot.Systems;

public sealed class StuckMonitorSystem
{
    private readonly SettingsStore _settings;
    private Vector2i _lastCell;
    private TimeSpan _lastProgressAt;
    private bool _initialized;
    private TimeSpan _lastUnstuckSpamAt;

    public StuckMonitorSystem(SettingsStore settings)
    {
        _settings = settings;
    }

    public void Tick(GameSnapshot? snapshot, InputRouter input)
    {
        var timeoutSeconds = _settings.Current.StuckTimeoutSeconds;
        if (timeoutSeconds <= 0 || snapshot?.Player is not { } player || !_settings.Current.BotActive)
        {
            Reset();
            return;
        }

        var panels = snapshot.OpenPanels;
        if (panels.IsWorldBlocked() || panels.IsOpen("StashElement") || panels.IsOpen("GuildStashElement") || panels.IsOpen("InventoryPanel"))
        {
            Reset();
            return;
        }

        var cell = player.GridPosition;
        var now = BotMonotonicClock.Now;

        if (!_initialized || Distance(cell, _lastCell) > 4f)
        {
            _lastCell = cell;
            _lastProgressAt = now;
            _initialized = true;
            return;
        }

        if ((now - _lastProgressAt).TotalSeconds >= timeoutSeconds)
        {
            // We are stuck. Forcefully spam the walk key.
            if ((now - _lastUnstuckSpamAt).TotalMilliseconds >= 500)
            {
                var walkKey = _settings.Current.Skills.Slots.FirstOrDefault(s => s.Role == SkillRole.Walk)?.Vk;
                if (walkKey.HasValue && walkKey.Value != 0)
                {
                    input.TapKey(walkKey.Value, ClickIntent.UseSkill, "anti-stuck movement");
                    _lastUnstuckSpamAt = now;
                    BubblesBot.Bot.Diagnostics.EventLog.Log("StuckMonitor", $"stuck timeout ({timeoutSeconds}s) reached, sending anti-stuck movement key");
                }
            }
        }
    }

    public void Reset()
    {
        _initialized = false;
        _lastProgressAt = TimeSpan.Zero;
    }

    private static float Distance(Vector2i a, Vector2i b)
    {
        var dx = (float)(a.X - b.X);
        var dy = (float)(a.Y - b.Y);
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
