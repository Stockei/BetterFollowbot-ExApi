using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using SharpDX;

namespace BetterFollowbot.PartyCommands;

/// <summary>
/// Leader command (default "lets stash"): walk to the guild stash, open it, make sure the first tab is showing and
/// has the expected name, Ctrl+click every inventory item except the ignored slots, wait a moment, close with Escape.
/// The steps and their limits live in StashDumpSequence; this class reads the game for it and sends the inputs.
/// Ported from the PoE2 Follower fork (GuildStashDump.cs).
/// </summary>
internal sealed class GuildStashDump
{
    private const float ReachDistance = 450f;
    private const int WalkStepMs = 300;
    private const int CursorSettleMs = 80;
    private const int TabCursorSettleMs = 120;
    internal const string DebugFileName = "GuildStashDumpDebug.txt";

    private readonly BetterFollowbot _plugin;
    private readonly UiClicker _clicker = new UiClicker();
    private StashDumpSequence _sequence;
    private uint _stashEntityId;
    private DateTime _nextWalkAt = DateTime.MinValue;
    private bool _clickingStash;
    private string _lastPhase = string.Empty;
    private Dictionary<(int X, int Y), Vector2> _slotCenters = new Dictionary<(int X, int Y), Vector2>();

    public GuildStashDump(BetterFollowbot plugin)
    {
        _plugin = plugin;
    }

    public bool IsActive => _sequence?.IsActive == true;

    public string Status => IsActive ? $"{_sequence.Phase}, pass {_sequence.Pass}, {_sequence.Moved} moved" : "Idle";

    public void Start(string leaderName, string commandText)
    {
        try
        {
            var settings = _plugin.Settings;
            _sequence = new StashDumpSequence(
                settings.guildStashDumpTabName.Value,
                settings.guildStashItemConfirmTimeoutMs.Value,
                settings.guildStashItemPauseMs.Value,
                settings.guildStashCloseDelayMs.Value);
            _stashEntityId = 0;
            _nextWalkAt = DateTime.MinValue;
            _clicker.ReleaseAll();
            _clickingStash = false;
            _lastPhase = string.Empty;
            _sequence.Start(_plugin.ActiveClockMs);
            WriteDebug($"START leader={leaderName} command='{commandText}'");
        }
        catch (Exception ex)
        {
            Abort("start error: " + ex.Message);
        }
    }

    /// <summary>Returns true while the dump runs, which keeps following and skills out for that frame.</summary>
    public bool Tick()
    {
        if (!IsActive)
            return false;

        try
        {
            var now = DateTime.Now;
            _clicker.Advance(now);

            var stash = FindGuildStash();
            var step = _sequence.Update(Observe(stash));

            if (_clickingStash && step.Action != StashDumpAction.ClickStash)
            {
                _plugin.ResetVerifiedClick();
                _clickingStash = false;
            }

            if (_sequence.IsActive && _sequence.Phase != _lastPhase)
            {
                _lastPhase = _sequence.Phase;
                WriteDebug($"phase {_lastPhase}" + (_sequence.Pass > 1 ? $" (pass {_sequence.Pass}, {_sequence.Moved} moved so far)" : string.Empty));
            }

            switch (step.Action)
            {
                case StashDumpAction.Walk:
                    WalkTo(stash, now);
                    return true;

                case StashDumpAction.ClickStash:
                    ClickStash(stash);
                    return true;

                case StashDumpAction.ClickFirstTab:
                    BeginFirstTabClick(now);
                    return true;

                case StashDumpAction.CtrlClickItem:
                    BeginItemClick(step.Slot, now);
                    return true;

                case StashDumpAction.PressEscape:
                    _clicker.ReleaseAll();
                    _plugin.TapEscape();
                    WriteDebug("pressed Escape to close the stash");
                    return true;

                case StashDumpAction.Finished:
                    Finish(step);
                    return false;

                default:
                    return true;
            }
        }
        catch (Exception ex)
        {
            Abort("error: " + ex.Message);
            return false;
        }
    }

    /// <summary>Ends the dump in an orderly way: the stash gets closed if it is open.</summary>
    public void RequestStop(string reason)
    {
        if (!IsActive)
            return;

        WriteDebug("stop requested: " + reason);
        _sequence.RequestStop(_plugin.ActiveClockMs, reason);
    }

    /// <summary>Stops immediately without touching the game UI (hotkey, zone change, death).</summary>
    public void Abort(string reason)
    {
        _clicker.ReleaseAll();
        if (_clickingStash)
        {
            _plugin.ResetVerifiedClick();
            _clickingStash = false;
        }

        if (!IsActive)
            return;

        _sequence.Abort();
        WriteDebug("ABORT " + reason);
        try { _plugin.LogMessage("StashDump: aborted, " + reason + "."); } catch { }
    }

    private void Finish(StashDumpStep step)
    {
        _clicker.ReleaseAll();
        WriteDebug($"DONE {step.Result}: {step.Note}");
        try { _plugin.LogMessage($"StashDump: {step.Note}."); } catch { }
    }

    private StashDumpObservation Observe(Entity stash)
    {
        var guildStashOpen = IsVisible(GuildStashWindow);
        return new StashDumpObservation
        {
            NowMs = _plugin.ActiveClockMs,
            StashInSight = stash != null,
            // Close enough, and its label or the chest is on screen to aim at. Otherwise walk closer.
            StashInReach = stash != null && !guildStashOpen && DistanceTo(stash) <= ReachDistance && _plugin.HasClickAim(stash),
            GuildStashOpen = guildStashOpen,
            PersonalStashOpen = IsVisible(PersonalStashWindow),
            InventoryOpen = IsVisible(InventoryPanel),
            TabsLoaded = AreTabsLoaded(),
            VisibleTabIndex = GetVisibleTabIndex(),
            FirstTabName = GetFirstTabName(),
            DumpableSlots = ReadDumpableSlots(),
            InputBusy = _clicker.IsBusy || _plugin.IsEscapeTapDown
        };
    }

    private void WalkTo(Entity stash, DateTime now)
    {
        if (stash == null || now < _nextWalkAt)
            return;

        // Walk with the movement key, like following: a plain left click on the ground may be bound to a skill.
        _plugin.WalkTowards(stash.Pos);
        _nextWalkAt = now.AddMilliseconds(WalkStepMs);
    }

    private void ClickStash(Entity stash)
    {
        _clickingStash = true;
        var result = stash == null
            ? ClickResult.Failed
            : _plugin.TryVerifiedClick(stash, "guild stash");
        if (result == ClickResult.Pending)
            return;

        _clickingStash = false;
        _sequence.StashClickFinished(result == ClickResult.Clicked, _plugin.ActiveClockMs);
        WriteDebug($"stash click attempt {_sequence.OpenAttempts}: {result}");
    }

    private void BeginFirstTabClick(DateTime now)
    {
        if (!TryGetFirstTabButtonCenter(out var center))
        {
            WriteDebug("first tab button not found");
            return;
        }

        _clicker.ClickAt(center, holdCtrl: false, TabCursorSettleMs, now);
        WriteDebug($"tab {GetVisibleTabIndex()} is showing, clicking the first tab header at screen position ({center.X:0},{center.Y:0})");
    }

    private void BeginItemClick((int X, int Y) slot, DateTime now)
    {
        if (!_slotCenters.TryGetValue(slot, out var center))
        {
            WriteDebug($"item at slot ({slot.X},{slot.Y}) has no screen position");
            return;
        }

        _clicker.ClickAt(center, holdCtrl: true, CursorSettleMs, now);
        WriteDebug($"Ctrl+click item at slot ({slot.X},{slot.Y}), screen position ({center.X:0},{center.Y:0})");
    }

    /// <summary>Occupied inventory slots that may be dumped, with the screen centre of each item for the click.</summary>
    private List<(int X, int Y)> ReadDumpableSlots()
    {
        var slots = new List<(int X, int Y)>();
        var centers = new Dictionary<(int X, int Y), Vector2>();
        try
        {
            var settings = _plugin.Settings;
            settings.guildStashIgnoredCells = InventoryGridIgnoreHelper.Normalize(settings.guildStashIgnoredCells);
            var ignoredCells = settings.guildStashIgnoredCells;

            var items = _plugin.GameController.IngameState.ServerData.PlayerInventories[0].Inventory.InventorySlotItems;
            foreach (var item in items)
            {
                if (item?.Item == null || InventoryGridIgnoreHelper.IsIgnored(item, ignoredCells))
                    continue;

                var slot = (item.PosX, item.PosY);
                slots.Add(slot);
                try
                {
                    var rect = item.GetClientRect();
                    centers[slot] = _plugin.ClientToScreen(rect.Center);
                }
                catch
                {
                }
            }
        }
        catch (Exception ex)
        {
            WriteDebug("inventory read failed: " + ex.Message);
        }

        _slotCenters = centers;
        return slots;
    }

    private Entity FindGuildStash()
    {
        try
        {
            var playerPos = _plugin.GameController.Player.Pos;
            Entity best = null;
            var bestDistance = float.MaxValue;

            foreach (var entity in _plugin.GameController.Entities.ToArray())
            {
                if (entity == null || entity.Type != EntityType.GuildStash || !entity.IsValid)
                    continue;

                // Stick with the stash chosen first instead of flipping between two.
                if (_stashEntityId != 0 && entity.Id == _stashEntityId)
                    return entity;

                var distance = Vector3.Distance(playerPos, entity.Pos);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = entity;
                }
            }

            if (best != null)
                _stashEntityId = best.Id;

            return best;
        }
        catch
        {
            return null;
        }
    }

    private StashElement GuildStashWindow
    {
        get
        {
            try { return _plugin.GameController.IngameState?.IngameUi?.GuildStashElement; }
            catch { return null; }
        }
    }

    private Element PersonalStashWindow
    {
        get
        {
            try { return _plugin.GameController.IngameState?.IngameUi?.StashElement; }
            catch { return null; }
        }
    }

    private Element InventoryPanel
    {
        get
        {
            try { return _plugin.GameController.IngameState?.IngameUi?.InventoryPanel; }
            catch { return null; }
        }
    }

    private static bool IsVisible(Element element)
    {
        try { return element != null && element.Address != 0 && element.IsVisible; }
        catch { return false; }
    }

    /// <summary>The window can show before the server has sent the tabs.</summary>
    private bool AreTabsLoaded()
    {
        try { return (GuildStashWindow?.Inventories?.Count ?? 0) > 0; }
        catch { return false; }
    }

    private int GetVisibleTabIndex()
    {
        try { return GuildStashWindow?.IndexVisibleStash ?? -1; }
        catch { return -1; }
    }

    private string GetFirstTabName()
    {
        try
        {
            var name = GuildStashWindow?.Inventories?.FirstOrDefault()?.TabName;
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }
        catch
        {
        }

        try
        {
            var tabs = _plugin.GameController.IngameState?.ServerData?.GuildStashTabs;
            return tabs?.Where(tab => tab != null && !tab.IsHidden).OrderBy(tab => tab.VisibleIndex).FirstOrDefault()?.Name;
        }
        catch
        {
            return null;
        }
    }

    private bool TryGetFirstTabButtonCenter(out Vector2 screenCenter)
    {
        screenCenter = Vector2.Zero;
        try
        {
            var stash = GuildStashWindow;
            Element button = null;
            try { button = stash?.Inventories?.FirstOrDefault()?.TabButton; } catch { button = null; }
            if (!IsClickable(button))
            {
                // The pinned tab list on the right side, if it is showing.
                try { button = stash?.TabListButtons?.FirstOrDefault(); } catch { button = null; }
            }

            if (!IsClickable(button))
                return false;

            var rect = button.GetClientRect();
            screenCenter = _plugin.ClientToScreen(rect.Center);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsClickable(Element element)
    {
        try
        {
            if (element == null || !element.IsVisible)
                return false;

            var rect = element.GetClientRect();
            return rect.Width > 2 && rect.Height > 2;
        }
        catch
        {
            return false;
        }
    }

    private float DistanceTo(Entity entity)
    {
        try { return Vector3.Distance(_plugin.GameController.Player.Pos, entity.Pos); }
        catch { return float.MaxValue; }
    }

    private void WriteDebug(string line)
    {
        if (_plugin.Settings.debugGuildStashDumpToTxt.Value)
            _plugin.AppendDebugLine(DebugFileName, line);
    }
}
