using System;
using BubblesBot.Bot.Input;
using BubblesBot.Core.Snapshot;

namespace BubblesBot.Bot.Systems;

public sealed class GemLevelingSystem
{
    private InputTicket? _pendingClick;
    private TimeSpan _lastClickAt = TimeSpan.MinValue;

    public void Tick(GameSnapshot snapshot, IInputRouter input, bool enabled)
    {
        if (!enabled)
        {
            _pendingClick = null;
            return;
        }

        if (_pendingClick is { IsResolved: false })
            return;

        if (BotMonotonicClock.ElapsedSince(_lastClickAt) < TimeSpan.FromMilliseconds(500))
            return;

        var view = GemLevelUpView.Read(snapshot.Reader, snapshot.IngameStateAddress);
        if (!view.IsVisible || view.Rows.Count == 0)
            return;

        foreach (var row in view.Rows)
        {
            var rect = row.LevelControl.Rect;
            if (rect.Width > 0 && rect.Height > 0)
            {
                var (sx, sy) = snapshot.Window.ToScreen(
                    (int)(rect.X + rect.Width / 2),
                    (int)(rect.Y + rect.Height / 2));

                _pendingClick = input.Click(
                    sx, sy,
                    ClickIntent.InteractUi,
                    "level up gem",
                    timeoutMs: 1000);

                _lastClickAt = BotMonotonicClock.Now;
                return; // One click per tick
            }
        }
    }
}
