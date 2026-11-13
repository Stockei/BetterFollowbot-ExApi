using SharpDX;

namespace BetterFollowbot.PartyCommands;

internal enum ClickAction
{
    None,
    StopMoving,
    MoveCursor,
    Click
}

internal enum ClickResult
{
    Pending,
    Clicked,
    Failed
}

internal readonly struct ClickStep
{
    public ClickStep(ClickAction action, ClickResult result, Vector2 cursor = default, string note = "")
    {
        Action = action;
        Result = result;
        Cursor = cursor;
        Note = note;
    }

    public ClickAction Action { get; }
    public ClickResult Result { get; }
    public Vector2 Cursor { get; }
    public string Note { get; }
}

/// <summary>
/// One click on a world object (portal, arena entrance, stairs, guild stash) that cannot land next to it:
/// stop moving, wait until the character stands still, put the cursor on the label centre, wait until the game
/// itself reports the cursor on that object, then click without moving the cursor again.
/// Kept free of game access: the plugin feeds what it observes each tick and executes the returned action.
/// </summary>
internal sealed class VerifiedClick
{
    internal const double StillMs = 250;                // position unchanged this long = standing still
    internal const double StillFlagOverrideMs = 1000;   // position unchanged this long counts even if the game's moving flag is stuck
    internal const double MaxWaitStillMs = 8000;                // a click from far away makes the character walk for a while
    internal const double NoAimTimeoutMs = 1000;        // standing, but no label or object on screen to aim at
    internal const double MinHoverMs = 100;             // the cursor rests at least this long before the click
    internal const double ConfirmTimeoutMs = 1000;      // wait this long for the game to confirm one aim point
    internal const double MaxAttemptMs = 15000;
    internal const int MaxConfirmTimeouts = 3;          // aim points tried per attempt: label, object itself, label
    internal const int MaxPlacements = 6;               // cursor placements per attempt, including re-aims after drift
    internal const float AimDriftPx = 4f;               // the aim point moved further than this: aim again
    internal const float MoveEpsilon = 2f;              // world units; smaller position changes count as standing

    private enum Phase
    {
        Idle,
        WaitStill,
        Confirm
    }

    private Phase _phase = Phase.Idle;
    private double _startedMs;
    private double _phaseStartedMs;
    private bool _stopSent;
    private bool _hasPosition;
    private Vector3 _lastPosition;
    private double _positionStableSinceMs;
    private Vector2 _aimPoint;
    private double _aimAtMs;
    private bool _useAlternate;
    private int _confirmTimeouts;

    internal bool IsActive => _phase != Phase.Idle;
    internal uint TargetId { get; private set; }
    internal int Placements { get; private set; }

    internal void Start(uint targetId, double nowMs)
    {
        TargetId = targetId;
        _phase = Phase.WaitStill;
        _startedMs = nowMs;
        _phaseStartedMs = nowMs;
        _stopSent = false;
        _hasPosition = false;
        _aimPoint = Vector2.Zero;
        _useAlternate = false;
        _confirmTimeouts = 0;
        Placements = 0;
    }

    internal void Reset()
    {
        _phase = Phase.Idle;
        TargetId = 0;
    }

    /// <param name="aimPoint">Screen position of the label centre (or of the object when it has no label); Zero = none.</param>
    /// <param name="alternateAimPoint">Screen position of the object itself, tried when the label is not confirmed; Zero = none.</param>
    /// <param name="hoverConfirmed">The game reports the cursor on the target.</param>
    /// <param name="allowUnconfirmed">Emergency switch: click after the last aim point even without confirmation.</param>
    internal ClickStep Update(double nowMs, Vector3 playerPosition, bool movingFlag, Vector2 aimPoint,
        Vector2 alternateAimPoint, bool hoverConfirmed, bool allowUnconfirmed)
    {
        if (_phase == Phase.Idle)
            return new ClickStep(ClickAction.None, ClickResult.Failed, note: "not started");

        if (!_hasPosition || Distance2D(playerPosition, _lastPosition) > MoveEpsilon)
        {
            _hasPosition = true;
            _lastPosition = playerPosition;
            _positionStableSinceMs = nowMs;
        }

        var stableForMs = nowMs - _positionStableSinceMs;
        var standing = (stableForMs >= StillMs && !movingFlag) || stableForMs >= StillFlagOverrideMs;

        if (nowMs - _startedMs >= MaxAttemptMs)
            return Fail("attempt timed out");

        var target = CurrentAim(aimPoint, alternateAimPoint);

        if (_phase == Phase.WaitStill)
        {
            if (!_stopSent)
            {
                _stopSent = true;
                return new ClickStep(ClickAction.StopMoving, ClickResult.Pending);
            }

            if (!standing)
            {
                return nowMs - _phaseStartedMs >= MaxWaitStillMs
                    ? Fail("the character did not stop")
                    : Pending();
            }

            if (target == Vector2.Zero)
            {
                return stableForMs >= StillMs + NoAimTimeoutMs
                    ? Fail("nothing to aim at (no label or object on screen)")
                    : Pending();
            }

            return Placements >= MaxPlacements
                ? Fail("the character was moved again and again while the cursor was on the target")
                : Aim(target, nowMs);
        }

        // Phase.Confirm
        if (stableForMs < StillMs)
        {
            // Pushed or still sliding: stand still again, then aim again.
            _phase = Phase.WaitStill;
            _phaseStartedMs = nowMs;
            return Pending();
        }

        if (target != Vector2.Zero && Vector2.Distance(target, _aimPoint) > AimDriftPx)
            return Placements >= MaxPlacements ? Fail("the label kept moving") : Aim(target, nowMs);

        var hoveredForMs = nowMs - _aimAtMs;
        if (hoverConfirmed && hoveredForMs >= MinHoverMs)
            return Done(ClickAction.Click, ClickResult.Clicked, $"confirmed after {hoveredForMs:0} ms on aim point {_confirmTimeouts + 1}");

        if (hoveredForMs < ConfirmTimeoutMs)
            return Pending();

        _confirmTimeouts++;
        if (_confirmTimeouts < MaxConfirmTimeouts && Placements < MaxPlacements)
        {
            // Not confirmed on this point: switch between the label and the object itself.
            _useAlternate = !_useAlternate;
            var next = CurrentAim(aimPoint, alternateAimPoint);
            if (next != Vector2.Zero)
                return Aim(next, nowMs);
        }

        if (allowUnconfirmed && _aimPoint != Vector2.Zero)
            return Done(ClickAction.Click, ClickResult.Clicked, $"UNCONFIRMED click after {_confirmTimeouts} aim point(s) (emergency switch)");

        return Fail($"the game did not confirm the hover on {_confirmTimeouts} aim point(s)");
    }

    private Vector2 CurrentAim(Vector2 aimPoint, Vector2 alternateAimPoint)
    {
        if (_useAlternate && alternateAimPoint != Vector2.Zero)
            return alternateAimPoint;

        return aimPoint != Vector2.Zero ? aimPoint : alternateAimPoint;
    }

    private ClickStep Aim(Vector2 point, double nowMs)
    {
        Placements++;
        _aimPoint = point;
        _aimAtMs = nowMs;
        _phase = Phase.Confirm;
        return new ClickStep(ClickAction.MoveCursor, ClickResult.Pending, point);
    }

    private static ClickStep Pending() => new ClickStep(ClickAction.None, ClickResult.Pending);

    private ClickStep Fail(string note) => Done(ClickAction.None, ClickResult.Failed, note);

    private ClickStep Done(ClickAction action, ClickResult result, string note)
    {
        var step = new ClickStep(action, result, _aimPoint, note);
        _phase = Phase.Idle;
        return step;
    }

    private static float Distance2D(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y));
}
