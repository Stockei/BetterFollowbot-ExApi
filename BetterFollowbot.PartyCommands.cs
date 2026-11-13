using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using BetterFollowbot.PartyCommands;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using SharpDX;

namespace BetterFollowbot;

// Leader commands in party chat (start, stop, guild stash dump) and the confirmed click on the guild stash.
// Ported from the PoE2 Follower fork; the follow and skill logic of this plugin stays as it is and only asks
// IsFollowActive / IsFollowPausedByChat whether it may run.
public partial class BetterFollowbot
{
    private const int MoveKeyTapMs = 40;
    private const int EscapeTapMs = 45;
    private const int EscapeSettleMs = 110;

    private readonly Stopwatch _activeClock = Stopwatch.StartNew();
    private readonly VerifiedClick _verifiedClick = new VerifiedClick();
    private PartyChatCommands _partyChatCommands;
    private GuildStashDump _guildStashDump;

    // Paused until the leader's start command: the follower never starts on its own after loading.
    private volatile bool _followPausedByChat = true;

    private DateTime _moveKeyReleaseAt = DateTime.MinValue;
    private bool _moveKeyDownByCommands;
    private DateTime _escapeReleaseAt = DateTime.MinValue;
    private DateTime _escapeSettledAt = DateTime.MinValue;
    private bool _escapeDown;

    internal double ActiveClockMs => _activeClock.Elapsed.TotalMilliseconds;

    /// <summary>Following and skills wait for the leader's start command (after loading, the stop command and a stash dump).</summary>
    internal bool IsFollowPausedByChat => Settings.partyChatCommandsEnabled.Value && _followPausedByChat;

    internal bool IsStashDumpActive => _guildStashDump?.IsActive == true;

    /// <summary>The follow loop and the path planning may run.</summary>
    public bool IsFollowActive => Settings.Enable.Value && Settings.autoPilotEnabled.Value && !IsFollowPausedByChat && !IsStashDumpActive;

    internal bool IsEscapeTapDown => _escapeDown || DateTime.Now < _escapeSettledAt;

    private void InitialisePartyCommands()
    {
        _partyChatCommands = new PartyChatCommands(this);
        _guildStashDump = new GuildStashDump(this);
        _followPausedByChat = true;
    }

    /// <summary>Reads the party chat and runs the stash dump. True while the dump runs: nothing else may act then.</summary>
    private bool TickPartyCommands()
    {
        _partyChatCommands?.Tick();

        if (IsStashDumpActive && GameController.Player?.IsAlive == false)
            _guildStashDump.Abort("the character is dead");

        var dumping = _guildStashDump?.Tick() == true;
        DrawPartyCommandStatus();
        return dumping;
    }

    /// <summary>The toggle hotkey switches AutoPilot by hand; switching it on also ends the chat pause.</summary>
    private void HandleFollowToggleHotkey()
    {
        if (!Settings.autoPilotToggleKey.PressedOnce())
            return;

        var enabled = !Settings.autoPilotEnabled.Value;
        Settings.autoPilotEnabled.SetValueNoEvent(enabled);
        if (enabled)
            _followPausedByChat = false;

        _guildStashDump?.Abort("AutoPilot toggled by hotkey");
        taskManager?.ClearTasks();
        ReleaseCommandInputs();
    }

    internal void SetFollowEnabledFromPartyChat(bool enabled, string leaderName, string commandText)
    {
        _followPausedByChat = !enabled;
        _guildStashDump?.RequestStop(enabled ? "start command" : "stop command");

        if (enabled && !Settings.autoPilotEnabled.Value)
            Settings.autoPilotEnabled.SetValueNoEvent(true);

        taskManager?.ClearTasks();
        ReleaseCommandInputs();

        LogMessage($"PARTY CHAT: leader {leaderName} sent '{commandText}' - follow and skills {(enabled ? "started" : "paused")}");
    }

    /// <summary>
    /// The stash command pauses following and skills: during the dump and afterwards, until the start command.
    /// Every character whose plugin reads the command acts on it, the leader's own one included; the menu switch
    /// "Dump to guild stash on leader command" is the guard and is switched off on the leader's PC.
    /// </summary>
    internal void StartGuildStashDumpFromPartyChat(string leaderName, string commandText)
    {
        if (IsStashDumpActive)
        {
            LogMessage("StashDump: already running.");
            return;
        }

        SetFollowEnabledFromPartyChat(false, leaderName, commandText);
        _guildStashDump?.Start(leaderName, commandText);
    }

    private void OnAreaChangePartyCommands()
    {
        _guildStashDump?.Abort("zone change");
        ResetVerifiedClick();
    }

    public override void OnClose()
    {
        _guildStashDump?.Abort("plugin closed");
        ReleaseCommandInputs();
        base.OnClose();
    }

    private void DrawPartyCommandStatus()
    {
        if (!Settings.partyChatCommandsEnabled.Value)
            return;

        try
        {
            if (IsStashDumpActive)
                Graphics.DrawText("Stash dump: " + _guildStashDump.Status, new System.Numerics.Vector2(350, 100), Color.Orange);
            else if (IsFollowPausedByChat)
                Graphics.DrawText($"Followbot paused - waiting for '{Settings.partyChatStartCommand.Value}' from {Settings.autoPilotLeader.Value}",
                    new System.Numerics.Vector2(350, 100), Color.Yellow);
        }
        catch
        {
        }
    }

    #region Inputs for the stash dump

    /// <summary>One step towards a world position with the movement key, like following does.</summary>
    internal void WalkTowards(Vector3 worldPosition)
    {
        Mouse.SetCursorPos(Helper.WorldToValidScreenPosition(worldPosition));
        Input.KeyDown(Settings.autoPilotMoveKey);
        _moveKeyDownByCommands = true;
        _moveKeyReleaseAt = DateTime.Now.AddMilliseconds(MoveKeyTapMs);
    }

    internal void TapEscape()
    {
        Keyboard.KeyDown(Keys.Escape);
        _escapeDown = true;
        _escapeReleaseAt = DateTime.Now.AddMilliseconds(EscapeTapMs);
        _escapeSettledAt = DateTime.Now.AddMilliseconds(EscapeSettleMs);
    }

    /// <summary>Releases the key taps started above once their hold time is over. Runs every frame.</summary>
    private void ProcessPendingKeyReleases()
    {
        var now = DateTime.Now;
        if (_moveKeyDownByCommands && now >= _moveKeyReleaseAt)
        {
            Input.KeyUp(Settings.autoPilotMoveKey);
            _moveKeyDownByCommands = false;
        }

        if (_escapeDown && now >= _escapeReleaseAt)
        {
            Keyboard.KeyUp(Keys.Escape);
            _escapeDown = false;
        }
    }

    private void ReleaseCommandInputs()
    {
        try
        {
            Input.KeyUp(Settings.autoPilotMoveKey);
            _moveKeyDownByCommands = false;
            if (_escapeDown)
            {
                Keyboard.KeyUp(Keys.Escape);
                _escapeDown = false;
            }

            Keyboard.KeyUp(Keys.LControlKey);
            ResetVerifiedClick();
        }
        catch
        {
        }
    }

    /// <summary>UI element rects are relative to the game window; the cursor needs screen coordinates.</summary>
    internal Vector2 ClientToScreen(Vector2 clientPosition) => clientPosition + GameController.Window.GetWindowRectangleTimeCache.TopLeft;

    #endregion

    #region Confirmed click on a world object

    /// <summary>
    /// Stand still, aim at the target's label, wait until the game reports the cursor on the target, then click.
    /// Call it every frame until the result is no longer Pending. No click happens without the game's confirmation.
    /// </summary>
    internal ClickResult TryVerifiedClick(Entity target, string what)
    {
        uint targetId = 0;
        try { targetId = target?.Id ?? 0; } catch { }
        if (targetId == 0)
        {
            ResetVerifiedClick();
            return ClickResult.Failed;
        }

        if (!_verifiedClick.IsActive || _verifiedClick.TargetId != targetId)
            _verifiedClick.Start(targetId, ActiveClockMs);

        ResolveClickAim(target, targetId, out var labelCenter, out var labelAddress, out var objectPoint);
        var aimPoint = labelCenter != Vector2.Zero ? labelCenter : objectPoint;
        var alternateAimPoint = labelCenter != Vector2.Zero ? objectPoint : Vector2.Zero;

        var step = _verifiedClick.Update(ActiveClockMs, GameController.Player.Pos, IsPlayerMovingFlag(), aimPoint,
            alternateAimPoint, IsHoverConfirmed(target, targetId, labelAddress), allowUnconfirmed: false);

        switch (step.Action)
        {
            case ClickAction.StopMoving:
                Input.KeyUp(Settings.autoPilotMoveKey);
                _moveKeyDownByCommands = false;
                break;

            case ClickAction.MoveCursor:
                Mouse.SetCursorPos(step.Cursor);
                break;

            case ClickAction.Click:
                // The cursor already rests on the target: press and release without moving it again.
                Mouse.LeftMouseDown();
                Mouse.LeftMouseUp();
                break;
        }

        if (step.Result == ClickResult.Pending)
            return ClickResult.Pending;

        if (Settings.debugGuildStashDumpToTxt.Value)
            AppendDebugLine(GuildStashDump.DebugFileName, $"click {what}: {step.Result} - {step.Note}");
        if (step.Result == ClickResult.Failed)
            LogMessage($"StashDump: {what} not clicked - {step.Note}");

        return step.Result;
    }

    internal void ResetVerifiedClick() => _verifiedClick.Reset();

    /// <summary>True when the target's label or the target itself is on screen, so a click can aim at it.</summary>
    internal bool HasClickAim(Entity target)
    {
        uint targetId = 0;
        try { targetId = target?.Id ?? 0; } catch { }
        if (targetId == 0)
            return false;

        ResolveClickAim(target, targetId, out var labelCenter, out _, out var objectPoint);
        return labelCenter != Vector2.Zero || objectPoint != Vector2.Zero;
    }

    /// <summary>The centre of the object's ground label and the object itself, in screen coordinates; Zero when not on screen.</summary>
    private void ResolveClickAim(Entity target, uint targetId, out Vector2 labelCenter, out long labelAddress, out Vector2 objectPoint)
    {
        labelCenter = Vector2.Zero;
        labelAddress = 0;
        objectPoint = Vector2.Zero;

        try
        {
            var label = FindVisibleLabelOf(targetId);
            if (label != null)
            {
                // The game lists a label as visible even while it sits outside the window: only aim at one on screen.
                var center = label.GetClientRect().Center;
                if (center != Vector2.Zero && IsClientPointOnScreen(center))
                {
                    labelCenter = ClientToScreen(center);
                    labelAddress = label.Address;
                }
            }
        }
        catch
        {
            // UI memory can change while it is read. Fall back to the object itself.
        }

        try
        {
            var point = GameController.IngameState.Camera.WorldToScreen(target.Pos);
            if (IsClientPointClickable(point))
                objectPoint = ClientToScreen(point);
        }
        catch
        {
        }
    }

    private Element FindVisibleLabelOf(uint targetId)
    {
        try
        {
            var labels = GameController.IngameState?.IngameUi?.ItemsOnGroundLabelsVisible;
            if (labels == null)
                return null;

            var inspected = 0;
            foreach (var groundLabel in labels)
            {
                if (groundLabel == null)
                    continue;

                if (++inspected > 64)
                    break;

                if ((groundLabel.ItemOnGround?.Id ?? 0) != targetId)
                    continue;

                var element = groundLabel.Label;
                return element != null && element.IsVisible ? element : null;
            }
        }
        catch
        {
            // UI memory can change while it is read.
        }

        return null;
    }

    /// <summary>The game's own view of what is under the cursor: the hovered ground label and the object's "targeted" mark.</summary>
    private bool IsHoverConfirmed(Entity target, uint targetId, long labelAddress)
    {
        try
        {
            var labels = GameController.IngameState?.IngameUi?.ItemsOnGroundLabelElement;
            if (labels != null)
            {
                if ((labels.ItemOnHover?.Id ?? 0) == targetId)
                    return true;

                if (labelAddress != 0 && (labels.LabelOnHover?.Address ?? 0) == labelAddress)
                    return true;
            }
        }
        catch
        {
        }

        try { return target.GetComponent<Targetable>()?.isTargeted == true; }
        catch { return false; }
    }

    private bool IsPlayerMovingFlag()
    {
        try { return GameController.Player.GetComponent<Actor>()?.isMoving == true; }
        catch { return false; }
    }

    private bool IsClientPointOnScreen(Vector2 clientPoint)
    {
        try
        {
            var window = GameController.Window.GetWindowRectangle();
            const float margin = 5f;
            return clientPoint.X >= margin && clientPoint.Y >= margin &&
                   clientPoint.X <= window.Width - margin && clientPoint.Y <= window.Height - margin;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>On screen and clear of the edges and the skill bar.</summary>
    private bool IsClientPointClickable(Vector2 point)
    {
        try
        {
            var window = GameController.Window.GetWindowRectangle();
            const float sideMargin = 60f;
            var bottomMargin = Math.Max(60f, window.Height * 0.18f);
            return point.X >= sideMargin && point.Y >= sideMargin &&
                   point.X <= window.Width - sideMargin && point.Y <= window.Height - bottomMargin;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    /// <summary>Appends one time-stamped line to a debug txt file in %TEMP%\BetterFollowbotDebug. Never throws.</summary>
    internal void AppendDebugLine(string fileName, string line)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "BetterFollowbotDebug");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, fileName), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
