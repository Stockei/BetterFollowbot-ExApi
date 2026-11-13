using System;
using SharpDX;
using System.Windows.Forms;

namespace BetterFollowbot.PartyCommands;

/// <summary>
/// One left click carried out over a few frames instead of in a single instant, optionally with Ctrl held:
/// (Ctrl down) - (cursor to the position) - press - release - (Ctrl up).
/// Used for clicks on windows (stash tab, inventory and shop items, a dialogue line) and for the Ctrl+click on an NPC.
/// Call Advance every frame until IsBusy is false.
/// </summary>
internal sealed class UiClicker
{
    private const int CtrlDownMs = 50;
    private const int MouseHoldMs = 50;
    private const int AfterClickMs = 50;

    private enum Phase
    {
        None,
        CtrlDown,
        CursorPlaced,
        MouseDown,
        MouseUp
    }

    private Phase _phase = Phase.None;
    private Vector2 _position;
    private bool _moveCursor;
    private int _settleMs;
    private DateTime _nextAt = DateTime.MinValue;
    private bool _ctrlHeld;
    private bool _mouseHeld;

    internal bool IsBusy => _phase != Phase.None;

    /// <summary>Click at a screen position. The cursor rests there for settleMs before the button goes down.</summary>
    internal void ClickAt(Vector2 screenPosition, bool holdCtrl, int settleMs, DateTime now)
    {
        Begin(screenPosition, moveCursor: true, holdCtrl, settleMs, now);
    }

    /// <summary>Click where the cursor already rests, without moving it.</summary>
    internal void ClickInPlace(bool holdCtrl, DateTime now)
    {
        Begin(Vector2.Zero, moveCursor: false, holdCtrl, 0, now);
    }

    private void Begin(Vector2 screenPosition, bool moveCursor, bool holdCtrl, int settleMs, DateTime now)
    {
        ReleaseAll();
        _position = screenPosition;
        _moveCursor = moveCursor;
        _settleMs = Math.Max(0, settleMs);

        if (holdCtrl)
        {
            Keyboard.KeyDown(Keys.LControlKey);
            _ctrlHeld = true;
            _phase = Phase.CtrlDown;
            _nextAt = now.AddMilliseconds(CtrlDownMs);
            return;
        }

        PlaceCursor(now);
    }

    internal void Advance(DateTime now)
    {
        if (_phase == Phase.None || now < _nextAt)
            return;

        switch (_phase)
        {
            case Phase.CtrlDown:
                PlaceCursor(now);
                break;

            case Phase.CursorPlaced:
                Mouse.LeftMouseDown();
                _mouseHeld = true;
                _phase = Phase.MouseDown;
                _nextAt = now.AddMilliseconds(MouseHoldMs);
                break;

            case Phase.MouseDown:
                Mouse.LeftMouseUp();
                _mouseHeld = false;
                _phase = Phase.MouseUp;
                _nextAt = now.AddMilliseconds(AfterClickMs);
                break;

            default:
                ReleaseAll();
                break;
        }
    }

    /// <summary>Ends a running click and lets go of whatever is still held.</summary>
    internal void ReleaseAll()
    {
        if (_mouseHeld)
        {
            Mouse.LeftMouseUp();
            _mouseHeld = false;
        }

        if (_ctrlHeld)
        {
            Keyboard.KeyUp(Keys.LControlKey);
            _ctrlHeld = false;
        }

        _phase = Phase.None;
    }

    private void PlaceCursor(DateTime now)
    {
        if (_moveCursor)
            Mouse.SetCursorPos(_position);

        _phase = Phase.CursorPlaced;
        _nextAt = now.AddMilliseconds(_settleMs);
    }
}
