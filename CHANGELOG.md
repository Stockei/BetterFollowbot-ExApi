# Changelog

Changes in this fork of BetterFollowbot, newest first. Each version is a git tag.

## Unreleased

### Added
- **Debug logging** switch under *General Settings*, off by default. While it is off the plugin writes only errors to the HUD log; the status messages (AutoPilot, area changes, portals, skills, automations) are only logged with it on. The txt debug logs keep their own switches.
- **Entrance labels before the party teleport**, like the Arena label in the PoE2 follower. Before the follower clicks the teleport button on the leader's portrait, it looks for a visible area transition label with a common entrance name (Arena, Stairs, The Pit, Warden's Quarters, Ascendancy Chamber, the act boss arenas, ...) and clicks that instead. Names match as whole words; *Extra entrance labels* adds your own (comma separated). Each label gets up to 3 clicks per zone, with a 4 s wait for the zone change after each; then, or when no entrance is visible, the party teleport goes ahead. A label matching the leader's zone name still comes first. Switch: *Auto Pilot > Entrance labels before party teleport* (on by default).

## v1.0.0 - 2026-11-13

### Added
- **Leader commands in party chat**, ported from the PoE2 Follower fork (FollowerFork-ExCore2). The plugin reads the newest chat lines once per poll interval (default 1 s); chat history from before the plugin started is ignored. Only party messages from the AutoPilot leader count, with or without a guild tag in front of the name.
  - `-s` starts following and skills, `-p` pauses both. The command words are settings.
  - **No autostart:** after loading, the follower is paused until the start command. Joining the party, respawn and gem levelling keep running while paused, so the follower can join the party and see the command.
- **Guild stash dump on a party chat command** (default `lets stash`, setting *Guild stash dump command*). The follower pauses, walks to the guild stash, opens it (up to 3 clicks, each confirmed by the game's hover state), makes sure the first tab is showing and has the expected name (default `dump tab`), then Ctrl+clicks item by item and waits until each item has left the inventory before it touches the next one. Items that did not move get up to 3 further passes after a 5 s pause, as long as the last pass moved something. After the last item it waits 2.5 s, closes the stash with Escape and stays paused until the start command.
  - An empty inventory ends the command at once.
  - Slots checked under *Ignored inventory slots* are never dumped (the top-left cell of an item counts).
  - The dump ends early when the stash is closed by hand, on a zone change or death, on the stop and start commands and on the AutoPilot hotkey.
  - Nothing else acts while the dump runs: no following, no skills, no other automation.
  - Switch *Dump to guild stash on leader command* off on the leader's own PC.
- Menu sections *Party Chat Commands* and *Guild Stash Dump*, with debug logs to `%TEMP%\BetterFollowbotDebug`.

### Changed
- The AutoPilot toggle hotkey is handled together with the chat pause: switching AutoPilot on also ends the pause.

### Fixed
- Builds on ExileApi 329.x: Hexa.NET.ImGui instead of ImGui.NET, `StdVector.Size` and `IngameUIElements.SynthesisWindow` removed from ExileCore.
- The settings menu no longer leaves pushed IDs and colours on the ImGui stack ("Missing PopID()" / "Missing PopStyleColor()").
