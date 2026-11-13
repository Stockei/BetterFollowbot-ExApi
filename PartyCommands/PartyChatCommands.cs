using System;
using System.Collections.Generic;
using ExileCore.PoEMemory;

namespace BetterFollowbot.PartyCommands;

/// <summary>
/// Reads the newest party chat lines and acts on the leader's commands: start following, stop following,
/// dump the inventory into the guild stash. Ported from the PoE2 Follower fork (PartyChatCommands.cs).
/// </summary>
internal sealed class PartyChatCommands
{
    private const int MinPollMs = 1000;
    private const int ArmDelayMs = 2500;
    private const int TailLines = 30;
    private const int MaxLineTextDepth = 3;
    private const int DebugHeartbeatMs = 5000;
    internal const string DebugFileName = "PartyChatCommandsDebug.txt";

    // Fallback when ChatPanel.ChatBox does not resolve: the message list through the UI tree
    // (ChatPanel -> child 1 -> child 2 -> child 1, one child element per chat line), as in the PoE2 HUD.
    private static readonly int[] ChatLineListPath = { 1, 2, 1 };

    private readonly BetterFollowbot _plugin;
    private DateTime _lastScan = DateTime.UtcNow.AddSeconds(-5);
    private DateTime _armAt = DateTime.MinValue;
    private bool _initialized;
    private bool _armed;
    private bool _wasEnabled;
    private long _lastLineCount = -1;
    private List<string> _lastTail = new List<string>();
    private DateTime _nextDebugHeartbeatAt = DateTime.MinValue;

    public PartyChatCommands(BetterFollowbot plugin)
    {
        _plugin = plugin;
    }

    public void Tick()
    {
        try
        {
            var s = _plugin.Settings;
            if (s == null || !s.Enable.Value || !s.partyChatCommandsEnabled.Value)
            {
                ResetScannerState();
                return;
            }

            if (!_wasEnabled)
            {
                ResetScannerState();
                _wasEnabled = true;
            }

            // Runs from Render(), so it only reads the newest chat lines once per poll interval.
            var pollMs = Math.Max(MinPollMs, s.partyChatPollMs.Value);
            var now = DateTime.UtcNow;
            if ((now - _lastScan).TotalMilliseconds < pollMs)
                return;
            _lastScan = now;

            var leaderName = (s.autoPilotLeader.Value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(leaderName))
                return;

            // During loading and right after a zone change the list can be missing. The last snapshot stays as it is,
            // so lines written in the meantime are still processed once the list is readable again.
            if (!TryReadNewestChatLines(out var lineCount, out var tail, out var source))
            {
                WriteDebugHeartbeat(now, "chat line list not found");
                return;
            }

            WriteDebugHeartbeat(now, $"source={source} lines={lineCount} armed={_armed}");

            if (!_initialized)
            {
                // Plugin start or reload: everything already in the chat is history and must not trigger a command.
                // Zone changes do not reset this state, so nothing written during a loading screen is skipped.
                _lastLineCount = lineCount;
                _lastTail = tail;
                _armAt = now.AddMilliseconds(ArmDelayMs);
                _initialized = true;
                _armed = false;
                return;
            }

            if (!_armed)
            {
                // Keep absorbing lines that are still being populated right after the reload.
                _lastLineCount = lineCount;
                _lastTail = tail;
                if (now < _armAt)
                    return;

                _armed = true;
                WriteDebug($"armed: watching party chat for commands from leader '{leaderName}'");
                return;
            }

            var newLines = NewLinesSince(_lastLineCount, _lastTail, lineCount, tail);
            _lastLineCount = lineCount;
            _lastTail = tail;

            foreach (var line in newLines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    ProcessEntry(line, leaderName);
            }
        }
        catch (Exception ex)
        {
            try { _plugin.LogMessage("PartyChatCommands error: " + ex.Message); } catch { }
        }
    }

    private void ResetScannerState()
    {
        _initialized = false;
        _armed = false;
        _wasEnabled = false;
        _lastLineCount = -1;
        _lastTail = new List<string>();
        _armAt = DateTime.MinValue;
        _lastScan = DateTime.UtcNow.AddSeconds(-5);
    }

    /// <summary>
    /// Reads the texts of the newest chat lines, oldest first. Lines without text stay in the list as empty
    /// strings so positions keep matching the line count.
    /// </summary>
    private bool TryReadNewestChatLines(out long lineCount, out List<string> tail, out string source)
    {
        lineCount = 0;
        tail = new List<string>();

        var list = FindChatLineList(out source);
        if (list == null)
            return false;

        lineCount = list.ChildCount;

        // Element.Children refuses very long lists, so the newest lines are fetched one by one.
        for (var i = Math.Max(0, lineCount - TailLines); i < lineCount; i++)
        {
            Element line;
            try { line = list.GetChildAtIndex((int)i); }
            catch { line = null; }

            tail.Add(NormalizeText(LineText(line, 0)));
        }

        return true;
    }

    private Element FindChatLineList(out string source)
    {
        source = string.Empty;
        try
        {
            var chatPanel = _plugin.GameController.IngameState?.IngameUi?.ChatPanel;
            if (chatPanel == null)
                return null;

            var chatBox = chatPanel.ChatBox;
            if (chatBox != null && chatBox.Address != 0 && chatBox.ChildCount > 0)
            {
                source = "ChatPanel.ChatBox";
                return chatBox;
            }

            var list = chatPanel.GetChildFromIndices(ChatLineListPath);
            if (list != null && list.Address != 0 && list.ChildCount > 0)
            {
                source = "ChatPanel[1][2][1]";
                return list;
            }
        }
        catch
        {
        }

        return null;
    }

    private static string LineText(Element element, int depth)
    {
        if (element == null)
            return string.Empty;

        try
        {
            var text = element.TextNoTags;
            if (string.IsNullOrWhiteSpace(text))
                text = element.Text;
            if (!string.IsNullOrWhiteSpace(text))
                return text;

            // Some lines keep their text in child elements (channel, name, message); join them in order.
            var childCount = element.ChildCount;
            if (depth >= MaxLineTextDepth || childCount <= 0 || childCount > 16)
                return string.Empty;

            var parts = new List<string>();
            foreach (var child in element.Children)
            {
                var part = LineText(child, depth + 1);
                if (!string.IsNullOrWhiteSpace(part))
                    parts.Add(part);
            }

            return string.Concat(parts);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// New lines since the last poll. The line count grows by one per message. If it did not grow (a full,
    /// rotating chat history or a rebuilt list), the newest lines are aligned with the previous snapshot instead.
    /// </summary>
    private static List<string> NewLinesSince(long previousCount, List<string> previousTail, long currentCount, List<string> currentTail)
    {
        var result = new List<string>();
        if (currentTail == null || currentTail.Count == 0)
            return result;

        if (previousCount >= 0 && currentCount > previousCount)
        {
            var added = (int)Math.Min(currentCount - previousCount, currentTail.Count);
            result.AddRange(currentTail.GetRange(currentTail.Count - added, added));
            return result;
        }

        if (previousTail == null || previousTail.Count == 0)
            return result;

        for (var overlap = Math.Min(previousTail.Count, currentTail.Count); overlap > 0; overlap--)
        {
            var matches = true;
            for (var i = 0; i < overlap && matches; i++)
                matches = string.Equals(previousTail[previousTail.Count - overlap + i], currentTail[i], StringComparison.Ordinal);

            if (!matches)
                continue;

            for (var i = overlap; i < currentTail.Count; i++)
                result.Add(currentTail[i]);
            return result;
        }

        // No overlap at all: process nothing rather than replaying old chat history.
        return result;
    }

    private void ProcessEntry(string text, string leaderName)
    {
        var s = _plugin.Settings;

        if (IsDebugEnabled())
        {
            var fromLeader = TryExtractPartyMessage(text, leaderName, out var debugMessage, out var debugSender, out var debugIsParty);
            WriteDebug($"line={DescribeLineForDebug(text, leaderName)} party={debugIsParty} fromLeader={fromLeader}" +
                       (fromLeader ? $" message='{debugMessage}'" : string.Empty) +
                       (debugIsParty && !fromLeader ? $" sender='{debugSender}'" : string.Empty));
        }

        if (s.guildStashDumpEnabled.Value &&
            TryParseLeaderSimpleCommand(text, leaderName, s.partyChatStashCommand.Value, "lets stash", out var stashCommandText))
        {
            _plugin.StartGuildStashDumpFromPartyChat(leaderName, stashCommandText);
            return;
        }

        if (TryParseLeaderToggleCommand(
                text,
                leaderName,
                s.partyChatStopCommand.Value,
                s.partyChatStartCommand.Value,
                "-p",
                "-s",
                out var followEnabled,
                out var commandText))
        {
            _plugin.SetFollowEnabledFromPartyChat(followEnabled, leaderName, commandText);
        }
    }

    private static bool TryParseLeaderToggleCommand(
        string rawText,
        string leaderName,
        string disableCommand,
        string enableCommand,
        string defaultDisableCommand,
        string defaultEnableCommand,
        out bool enabled,
        out string commandText)
    {
        enabled = false;
        commandText = string.Empty;

        disableCommand = NormalizeCommandOrDefault(disableCommand, defaultDisableCommand);
        enableCommand = NormalizeCommandOrDefault(enableCommand, defaultEnableCommand);

        if (!TryExtractPartyMessage(rawText, leaderName, out var message, out _, out _))
            return false;

        if (string.Equals(message, disableCommand, StringComparison.OrdinalIgnoreCase))
        {
            enabled = false;
            commandText = disableCommand;
            return true;
        }

        if (string.Equals(message, enableCommand, StringComparison.OrdinalIgnoreCase))
        {
            enabled = true;
            commandText = enableCommand;
            return true;
        }

        return false;
    }

    private static string NormalizeCommandOrDefault(string command, string defaultCommand)
    {
        return string.IsNullOrWhiteSpace(command) ? defaultCommand : command.Trim();
    }

    private static bool TryParseLeaderSimpleCommand(
        string rawText,
        string leaderName,
        string command,
        string defaultCommand,
        out string commandText)
    {
        commandText = string.Empty;
        command = NormalizeCommandOrDefault(command, defaultCommand);

        if (!TryExtractPartyMessage(rawText, leaderName, out var message, out _, out _))
            return false;

        if (!string.Equals(message, command, StringComparison.OrdinalIgnoreCase))
            return false;

        commandText = command;
        return true;
    }

    private static bool TryExtractPartyMessage(
        string rawText,
        string leaderName,
        out string message,
        out string sender,
        out bool isParty)
    {
        message = string.Empty;
        sender = string.Empty;
        isParty = false;

        if (string.IsNullOrWhiteSpace(rawText) || string.IsNullOrWhiteSpace(leaderName))
            return false;

        var text = NormalizeText(StripSimpleTags(rawText));
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // Some chat layouts prepend timestamps before the channel marker. Keep the party marker and following text.
        // A timestamp such as "[12:34]" contains colons itself, so a prefix without letters also counts as one.
        var percentIndex = text.IndexOf('%');
        var firstColon = text.IndexOf(':');
        if (percentIndex > 0 && (firstColon < 0 || percentIndex < firstColon || !ContainsLetter(text.Substring(0, percentIndex))))
            text = text.Substring(percentIndex).Trim();
        else
            text = SkipTimestampBefore(text, "[Party]");

        if (text.StartsWith("%", StringComparison.Ordinal))
        {
            isParty = true;
            text = text.Substring(1).TrimStart();
        }
        else if (text.StartsWith("[Party]", StringComparison.OrdinalIgnoreCase))
        {
            isParty = true;
            text = text.Substring("[Party]".Length).TrimStart();
        }
        else if (text.StartsWith("Party", StringComparison.OrdinalIgnoreCase))
        {
            var markerEnd = text.IndexOf(']');
            if (markerEnd >= 0)
            {
                isParty = true;
                text = text.Substring(markerEnd + 1).TrimStart();
            }
        }

        var colon = text.IndexOf(':');
        if (colon <= 0)
            return false;

        sender = text.Substring(0, colon).Trim();
        message = text.Substring(colon + 1).Trim();

        // Leader commands are intentionally limited to party chat.
        return isParty &&
               SenderIsLeader(sender, leaderName) &&
               !string.IsNullOrWhiteSpace(message);
    }

    private static string SkipTimestampBefore(string text, string marker)
    {
        var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index <= 0 || ContainsLetter(text.Substring(0, index)))
            return text;

        return text.Substring(index).Trim();
    }

    private static bool ContainsLetter(string text)
    {
        foreach (var ch in text)
        {
            if (char.IsLetter(ch))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Accepts the plain character name and names with a guild tag in front, e.g. "&lt;TAG&gt; Name" or "[TAG]Name".
    /// Character names never contain spaces, so the last word of the sender is the name.
    /// </summary>
    private static bool SenderIsLeader(string sender, string leaderName)
    {
        sender = (sender ?? string.Empty).Trim();
        leaderName = (leaderName ?? string.Empty).Trim();
        if (sender.Length == 0 || leaderName.Length == 0)
            return false;

        if (string.Equals(sender, leaderName, StringComparison.OrdinalIgnoreCase))
            return true;

        var name = sender;
        var lastSpace = name.LastIndexOf(' ');
        if (lastSpace >= 0)
            name = name.Substring(lastSpace + 1);

        var tagEnd = name.LastIndexOfAny(new[] { ']', '>', ')' });
        if (tagEnd >= 0 && tagEnd < name.Length - 1)
            name = name.Substring(tagEnd + 1);

        return string.Equals(name, leaderName, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var normalized = text.Replace('\0', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (normalized.Contains("  "))
            normalized = normalized.Replace("  ", " ");
        return normalized;
    }

    private static string StripSimpleTags(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var chars = new List<char>(text.Length);
        var insideTag = false;
        foreach (var ch in text)
        {
            if (ch == '<')
            {
                insideTag = true;
                continue;
            }

            if (ch == '>' && insideTag)
            {
                insideTag = false;
                continue;
            }

            if (!insideTag)
                chars.Add(ch);
        }

        return new string(chars.ToArray());
    }

    private bool IsDebugEnabled()
    {
        try { return _plugin.Settings.debugPartyChatCommandsToTxt.Value; }
        catch { return false; }
    }

    /// <summary>
    /// Full text only for lines that mention the leader or a command word; other players' messages are masked.
    /// </summary>
    private string DescribeLineForDebug(string text, string leaderName)
    {
        var relevant = text.IndexOf(leaderName, StringComparison.OrdinalIgnoreCase) >= 0;
        try
        {
            var s = _plugin.Settings;
            foreach (var command in new[] { s.partyChatStopCommand.Value, s.partyChatStartCommand.Value, s.partyChatStashCommand.Value })
            {
                if (!string.IsNullOrWhiteSpace(command) && text.IndexOf(command.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                    relevant = true;
            }
        }
        catch
        {
        }

        return relevant
            ? $"'{text}'"
            : $"<other: starts with '{(text.Length > 0 ? text.Substring(0, 1) : string.Empty)}', {text.Length} chars>";
    }

    private void WriteDebugHeartbeat(DateTime now, string state)
    {
        if (!IsDebugEnabled() || now < _nextDebugHeartbeatAt)
            return;

        _nextDebugHeartbeatAt = now.AddMilliseconds(DebugHeartbeatMs);
        WriteDebug("state: " + state);
    }

    private void WriteDebug(string line)
    {
        if (IsDebugEnabled())
            _plugin.AppendDebugLine(DebugFileName, line);
    }
}
