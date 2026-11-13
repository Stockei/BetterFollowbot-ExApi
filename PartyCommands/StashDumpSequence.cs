using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterFollowbot.PartyCommands;

internal enum StashDumpAction
{
    None,
    Walk,
    ClickStash,
    ClickFirstTab,
    CtrlClickItem,
    PressEscape,
    Finished
}

internal enum StashDumpResult
{
    Dumped,
    NothingToDump,
    NoStashInSight,
    CouldNotOpen,
    WrongStashOpened,
    WrongTab,
    StashClosed,
    Stopped,
    TimedOut
}

/// <summary>What the plugin sees in the game this tick.</summary>
internal sealed class StashDumpObservation
{
    public double NowMs;
    public bool StashInSight;
    public bool StashInReach;
    public bool GuildStashOpen;
    public bool PersonalStashOpen;
    public bool InventoryOpen;
    public bool TabsLoaded;
    public int VisibleTabIndex = -1;
    public string FirstTabName;
    public IReadOnlyCollection<(int X, int Y)> DumpableSlots = Array.Empty<(int X, int Y)>();
    public bool InputBusy;
}

internal readonly struct StashDumpStep
{
    public StashDumpStep(StashDumpAction action, (int X, int Y) slot = default, StashDumpResult result = default, string note = "")
    {
        Action = action;
        Slot = slot;
        Result = result;
        Note = note;
    }

    public StashDumpAction Action { get; }
    public (int X, int Y) Slot { get; }
    public StashDumpResult Result { get; }
    public string Note { get; }
}

/// <summary>
/// The guild stash dump as a sequence: walk to the stash, open it (up to 3 confirmed clicks), make sure the first tab
/// is showing and carries the expected name, Ctrl+click item by item and wait until the game shows each item gone
/// before touching the next, then wait a moment and close with Escape. Items that did not move get another pass after
/// a pause, because several characters may be stashing into the same tab at the same moment.
/// Kept free of game access: the plugin feeds what it observes each tick and executes the returned action.
/// </summary>
internal sealed class StashDumpSequence
{
    internal const double WalkTimeoutMs = 15000;
    internal const int MaxOpenAttempts = 3;
    internal const double OpenWaitMs = 5000;          // after a click: the character may still walk to the stash
    internal const double OpenRetryPauseMs = 1000;
    internal const double InventoryWaitMs = 2000;
    internal const double TabsLoadWaitMs = 8000;      // the window can show before the server has sent the tabs
    internal const int MaxTabClicks = 3;
    internal const double TabWaitMs = 800;
    internal const int MaxItemTries = 2;
    internal const int MaxSkippedInARow = 3;          // nothing moves any more (tab full?): end this pass instead of clicking on
    internal const int MaxPasses = 3;                 // left-over items get another pass, as long as the last pass moved something
    internal const double SweepPauseMs = 5000;        // pause before the next pass
    internal const int MaxEscapes = 2;
    internal const double CloseWaitMs = 2500;         // be patient: a second Escape with everything closed opens the game menu
    internal const double MaxTotalMs = 300000;

    private enum State
    {
        Idle,
        Approach,
        Open,
        OpenWait,
        OpenPause,
        Tab,
        Dump,
        Sweep,
        Close
    }

    private readonly string _wantedTabName;
    private readonly double _itemConfirmMs;
    private readonly double _itemPauseMs;
    private readonly double _closeDelayMs;

    private State _state = State.Idle;
    private double _startedMs;
    private double _stateSinceMs;
    private int _openAttempts;
    private int _tabClicks;
    private double _tabClickedMs = double.NaN;
    private List<(int X, int Y)> _plan = new List<(int X, int Y)>();
    private int _index;
    private int _tries;
    private double _itemClickedMs = double.NaN;
    private double _nextItemMs;
    private int _skippedInARow;
    private int _pass;
    private int _movedThisPass;
    private bool _awaitingInput;
    private int _escapes;
    private double _escapeMs;
    private StashDumpResult _result;
    private string _note = string.Empty;

    internal StashDumpSequence(string wantedTabName, double itemConfirmMs, double itemPauseMs, double closeDelayMs)
    {
        _wantedTabName = (wantedTabName ?? string.Empty).Trim();
        _itemConfirmMs = Math.Max(1000, itemConfirmMs);
        _itemPauseMs = Math.Max(100, itemPauseMs);
        _closeDelayMs = Math.Max(0, closeDelayMs);
    }

    internal bool IsActive => _state != State.Idle;
    internal int Moved { get; private set; }
    internal int Skipped { get; private set; }
    internal int OpenAttempts => _openAttempts;
    internal int Pass => _pass;
    internal string Phase => _state.ToString();

    /// <summary>True while the next stash click would be the last allowed attempt.</summary>
    internal bool IsLastOpenAttempt => _openAttempts + 1 >= MaxOpenAttempts;

    internal void Start(double nowMs)
    {
        _state = State.Approach;
        _startedMs = nowMs;
        _stateSinceMs = nowMs;
        _openAttempts = 0;
        _tabClicks = 0;
        _tabClickedMs = double.NaN;
        _plan = new List<(int X, int Y)>();
        _index = 0;
        _tries = 0;
        _itemClickedMs = double.NaN;
        _nextItemMs = 0;
        _skippedInARow = 0;
        _pass = 1;
        _movedThisPass = 0;
        _awaitingInput = false;
        _escapes = 0;
        _result = StashDumpResult.Dumped;
        _note = string.Empty;
        Moved = 0;
        Skipped = 0;
    }

    /// <summary>Stops immediately without touching the game (zone change, panic key).</summary>
    internal void Abort()
    {
        _state = State.Idle;
    }

    /// <summary>Ends the dump in an orderly way: closes the stash if it is open.</summary>
    internal void RequestStop(double nowMs, string note)
    {
        if (_state == State.Idle || _state == State.Close)
            return;

        BeginClose(nowMs, StashDumpResult.Stopped, note);
    }

    /// <summary>The plugin reports the outcome of the confirmed click on the stash.</summary>
    internal void StashClickFinished(bool clicked, double nowMs)
    {
        if (_state != State.Open)
            return;

        _openAttempts++;
        Enter(clicked ? State.OpenWait : State.OpenPause, nowMs);
    }

    internal StashDumpStep Update(StashDumpObservation o)
    {
        if (_state == State.Idle)
            return new StashDumpStep(StashDumpAction.None);

        var now = o.NowMs;
        if (_state != State.Close && now - _startedMs >= MaxTotalMs)
            BeginClose(now, StashDumpResult.TimedOut, "the dump took too long");

        switch (_state)
        {
            case State.Approach:
                if (o.GuildStashOpen)
                    return Go(State.Tab, now);
                if (!o.StashInSight)
                    return Finish(StashDumpResult.NoStashInSight, "no guild stash in sight");
                if (o.DumpableSlots.Count == 0)
                    return Finish(StashDumpResult.NothingToDump, "nothing to dump");
                if (o.StashInReach)
                    return Go(State.Open, now);
                if (now - _stateSinceMs >= WalkTimeoutMs)
                    return Finish(StashDumpResult.CouldNotOpen, "could not reach the guild stash");
                return new StashDumpStep(StashDumpAction.Walk);

            case State.Open:
                if (o.GuildStashOpen)
                    return Go(State.Tab, now);
                if (o.PersonalStashOpen)
                    return CloseWith(now, StashDumpResult.WrongStashOpened, "the personal stash opened instead of the guild stash");
                return new StashDumpStep(StashDumpAction.ClickStash);

            case State.OpenWait:
                if (o.GuildStashOpen)
                    return Go(State.Tab, now);
                if (o.PersonalStashOpen)
                    return CloseWith(now, StashDumpResult.WrongStashOpened, "the personal stash opened instead of the guild stash");
                if (now - _stateSinceMs < OpenWaitMs)
                    return Wait();
                return RetryOpenOrGiveUp(now);

            case State.OpenPause:
                if (o.GuildStashOpen)
                    return Go(State.Tab, now);
                if (now - _stateSinceMs < OpenRetryPauseMs)
                    return Wait();
                return RetryOpenOrGiveUp(now);

            case State.Tab:
                return UpdateTab(o, now);

            case State.Dump:
                return UpdateDump(o, now);

            case State.Sweep:
                return UpdateSweep(o, now);

            default:
                return UpdateClose(o, now);
        }
    }

    private StashDumpStep RetryOpenOrGiveUp(double now)
    {
        if (_openAttempts >= MaxOpenAttempts)
            return CloseWith(now, StashDumpResult.CouldNotOpen, $"the guild stash did not open after {_openAttempts} attempts");

        // Walk closer again if needed, then click again.
        return Go(State.Approach, now);
    }

    private StashDumpStep UpdateTab(StashDumpObservation o, double now)
    {
        if (!o.GuildStashOpen)
            return Finish(StashDumpResult.StashClosed, "the guild stash closed before the dump");

        if (InputStillRunning(o, now, ref _tabClickedMs))
            return Wait();

        if (!o.InventoryOpen)
        {
            return now - _stateSinceMs < InventoryWaitMs
                ? Wait()
                : CloseWith(now, StashDumpResult.CouldNotOpen, "the inventory did not open together with the guild stash");
        }

        // Slow connection: give the tabs time to arrive. Afterwards the checks below decide either way.
        if (!o.TabsLoaded && now - _stateSinceMs < TabsLoadWaitMs)
            return Wait();

        if (o.VisibleTabIndex != 0)
        {
            if (!double.IsNaN(_tabClickedMs) && now - _tabClickedMs < TabWaitMs)
                return Wait();

            if (_tabClicks >= MaxTabClicks)
                return CloseWith(now, StashDumpResult.WrongTab, $"could not switch to the first tab (tab {o.VisibleTabIndex} is showing)");

            _tabClicks++;
            _awaitingInput = true;
            return new StashDumpStep(StashDumpAction.ClickFirstTab);
        }

        // The first tab is showing: a later re-selection gets its own attempts.
        _tabClicks = 0;

        if (_wantedTabName.Length > 0)
        {
            var name = (o.FirstTabName ?? string.Empty).Trim();
            if (!string.Equals(name, _wantedTabName, StringComparison.OrdinalIgnoreCase))
            {
                return CloseWith(now, StashDumpResult.WrongTab, name.Length == 0
                    ? "the name of the first tab could not be read"
                    : $"the first tab is named '{name}', expected '{_wantedTabName}'");
            }
        }

        if (_plan.Count == 0)
        {
            _plan = o.DumpableSlots.OrderBy(slot => slot.Y).ThenBy(slot => slot.X).ToList();
            _index = 0;
            _tries = 0;
        }

        if (_plan.Count == 0)
        {
            // In a later pass this means the left-over items moved in the meantime.
            return _pass > 1
                ? CloseWith(now, StashDumpResult.Dumped, $"{Moved} moved, {Skipped} skipped")
                : CloseWith(now, StashDumpResult.NothingToDump, "nothing to dump");
        }

        return Go(State.Dump, now);
    }

    private StashDumpStep UpdateDump(StashDumpObservation o, double now)
    {
        if (!o.GuildStashOpen)
            return Finish(StashDumpResult.StashClosed, $"the guild stash closed during the dump ({Moved} moved)");

        if (InputStillRunning(o, now, ref _itemClickedMs))
            return Wait();

        if (o.VisibleTabIndex != 0)
        {
            // Another tab came to the front: select the first tab again before the next item.
            _itemClickedMs = double.NaN;
            return Go(State.Tab, now);
        }

        while (_index < _plan.Count)
        {
            var slot = _plan[_index];
            var stillThere = o.DumpableSlots.Contains(slot);

            if (!double.IsNaN(_itemClickedMs))
            {
                if (!stillThere)
                {
                    CountMoved();
                    NextItem(now);
                    continue;
                }

                if (now - _itemClickedMs < _itemConfirmMs)
                    return Wait();

                _itemClickedMs = double.NaN;
                if (_tries >= MaxItemTries)
                {
                    Skipped++;
                    _skippedInARow++;
                    NextItem(now);
                    if (_skippedInARow >= MaxSkippedInARow)
                        return EndPass(now, stoppedEarly: true);
                    continue;
                }
            }

            if (!stillThere)
            {
                // Gone without a pending confirmation: count it if this sequence clicked it.
                if (_tries > 0)
                    CountMoved();

                NextItem(now);
                continue;
            }

            if (now < _nextItemMs)
                return Wait();

            _tries++;
            _awaitingInput = true;
            return new StashDumpStep(StashDumpAction.CtrlClickItem, slot);
        }

        return EndPass(now, stoppedEarly: false);
    }

    private void CountMoved()
    {
        Moved++;
        _movedThisPass++;
        _skippedInARow = 0;
    }

    /// <summary>
    /// A pass is over. If items were left behind and this pass moved at least one item, wait and run another pass
    /// over what is still there. A pass that moves nothing ends the dump (the tab is full, or the items cannot be stashed).
    /// </summary>
    private StashDumpStep EndPass(double now, bool stoppedEarly)
    {
        var itemsLeft = stoppedEarly || Skipped > 0;
        if (itemsLeft && _movedThisPass > 0 && _pass < MaxPasses)
        {
            _pass++;
            _movedThisPass = 0;
            _skippedInARow = 0;
            Skipped = 0;
            _plan = new List<(int X, int Y)>();
            _itemClickedMs = double.NaN;
            return Go(State.Sweep, now);
        }

        return CloseWith(now, StashDumpResult.Dumped, stoppedEarly
            ? $"{_skippedInARow} items in a row did not move, stopping ({Moved} moved)"
            : $"{Moved} moved, {Skipped} skipped");
    }

    /// <summary>Pause before the next pass; the Tab step then plans it from what is still in the inventory.</summary>
    private StashDumpStep UpdateSweep(StashDumpObservation o, double now)
    {
        if (!o.GuildStashOpen)
            return Finish(StashDumpResult.StashClosed, $"the guild stash closed during the dump ({Moved} moved)");

        return now - _stateSinceMs < SweepPauseMs ? Wait() : Go(State.Tab, now);
    }

    private void NextItem(double now)
    {
        _index++;
        _tries = 0;
        _itemClickedMs = double.NaN;
        _nextItemMs = now + _itemPauseMs;
    }

    private StashDumpStep UpdateClose(StashDumpObservation o, double now)
    {
        if (o.InputBusy)
            return Wait();

        var stashOpen = o.GuildStashOpen || o.PersonalStashOpen;
        if (!stashOpen && !o.InventoryOpen)
            return Finish(_result, _note);

        // Nothing is rushed at the end: the window stays open for a moment after the last item.
        if (_escapes == 0 && now - _stateSinceMs < _closeDelayMs)
            return Wait();

        if (_escapes > 0 && now - _escapeMs < CloseWaitMs)
            return Wait();

        // A second Escape only while a stash window is still showing: with everything closed it would open the game menu.
        if (_escapes == 0 || (stashOpen && _escapes < MaxEscapes))
        {
            _escapes++;
            _escapeMs = now;
            return new StashDumpStep(StashDumpAction.PressEscape);
        }

        return Finish(_result, _note + " (a panel still reads as open after Escape)");
    }

    /// <summary>
    /// After an input action the plugin needs a few ticks to carry it out. The wait timer of that input starts when
    /// the input is finished.
    /// </summary>
    private bool InputStillRunning(StashDumpObservation o, double now, ref double inputFinishedMs)
    {
        if (o.InputBusy)
            return true;

        if (_awaitingInput)
        {
            _awaitingInput = false;
            inputFinishedMs = now;
        }

        return false;
    }

    private StashDumpStep CloseWith(double now, StashDumpResult result, string note)
    {
        BeginClose(now, result, note);
        return Wait();
    }

    private void BeginClose(double now, StashDumpResult result, string note)
    {
        _result = result;
        _note = note;
        _escapes = 0;
        _awaitingInput = false;
        Enter(State.Close, now);
    }

    private StashDumpStep Finish(StashDumpResult result, string note)
    {
        _state = State.Idle;
        return new StashDumpStep(StashDumpAction.Finished, result: result, note: note);
    }

    private StashDumpStep Go(State state, double now)
    {
        Enter(state, now);
        return Wait();
    }

    private void Enter(State state, double now)
    {
        _state = state;
        _stateSinceMs = now;
    }

    private static StashDumpStep Wait() => new StashDumpStep(StashDumpAction.None);
}
