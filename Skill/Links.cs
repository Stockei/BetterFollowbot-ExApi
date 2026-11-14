using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using BetterFollowbot;
using BetterFollowbot.Interfaces;
using BetterFollowbot.Core.Skills;
using BetterFollowbot.Core.TaskManagement;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using SharpDX;

namespace BetterFollowbot.Skills
{
    internal class Links : ISkill
    {
        private readonly BetterFollowbot _instance;
        private readonly BetterFollowbotSettings _settings;
        private readonly System.Collections.Generic.Dictionary<string, DateTime> _lastLinkTime;

        public Links(BetterFollowbot instance, BetterFollowbotSettings settings)
        {
            _instance = instance;
            _settings = settings;
            _lastLinkTime = new System.Collections.Generic.Dictionary<string, DateTime>();
        }

        public bool IsEnabled => _settings.linksEnabled || _settings.flameLinkEnabled || _settings.protectiveLinkEnabled || _settings.destructiveLinkEnabled;

        public string SkillName => "Links";

        /// <summary>
        /// Checks if any blocking UI elements are open that should prevent skill execution
        /// </summary>
        private bool IsBlockingUiOpen()
        {
            return UIBlockingUtility.IsAnyBlockingUIOpen();
        }

        // The link buffs carry no countdown (their Timer reads Infinity), so each ally's link is timed here from the
        // last cast on them and the skill's own duration (SkillEffectDuration), and renewed once this share of it has
        // passed: early enough that one renewal lost to the wrong ally still leaves time for the next one.
        private const double RenewAfterShare = 0.4;
        // A renewal that is not urgent waits while another party member stands this close to the ally on screen.
        private const double UrgentShare = 0.75;
        private const float OverlapPx = 70f;
        private const double FallbackDurationMs = 4000;
        // The cursor rests on the ally until the game marks them as targeted, at most this long, before the key press.
        private const double MaxAimMs = 150;
        // After the key press a new link must show up on the ally within this time.
        private const double ConfirmTimeoutMs = 1500;
        // An ally whose link did not land, or who was covered by another ally, waits this long; the others go first.
        private const double RetryAfterFailMs = 1000;
        private const double RetryAfterCoveredMs = 400;

        /// <summary>One link cast on one ally, spread over a few frames: aim, press, confirm.</summary>
        private sealed class LinkAttempt
        {
            public Core.Skills.Skill LinkSkill;
            public string LinkType;
            public string TargetBuffName;
            public string PlayerName;
            public uint EntityId;
            public Keys Key;
            public bool HadLink;               // the ally already carried this character's link (a renewal)
            public DateTime AimedAt;
            public DateTime PressedAt = DateTime.MinValue;
        }

        private LinkAttempt _attempt;
        // Last cast on an ally that the game showed as landed ("<name>_<linkType>").
        private readonly Dictionary<string, DateTime> _linkedAt = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, DateTime> _retryAfter = new Dictionary<string, DateTime>();
        private readonly HashSet<string> _missingKeyLogged = new HashSet<string>();

        public void Execute()
        {
            if (IsBlockingUiOpen() || !_instance.GameController.IsForeGroundCache ||
                _instance.GameController?.Area?.CurrentArea?.IsTown == true ||
                (_instance.GameController?.Area?.CurrentArea?.IsHideout == true && _settings.disableSkillsInHideout))
            {
                _attempt = null;
                return;
            }

            // A running cast is finished first, even inside the skill cooldown it started itself.
            if (_attempt != null)
            {
                ContinueAttempt();
                return;
            }

            if (!_instance.CanUseSkill("Links")) return;

            // Skills normally wait while the bot catches up with the leader. A link about to run out (or gone) is
            // renewed anyway: catching up can take longer than the link lasts.
            var onlyUrgent = !_instance.IsWithinFollowRange();

            if (_settings.flameLinkEnabled &&
                TryStartAttempt(SkillInfo.flameLink, "flame_link_target", "flame_link", onlyUrgent))
                return;

            if (_settings.protectiveLinkEnabled &&
                TryStartAttempt(SkillInfo.protectiveLink, "bulwark_link_target", "protective_link", onlyUrgent))
                return;

            // Destructive Link's buffs are named critical_link_* in the game data.
            if (_settings.destructiveLinkEnabled)
                TryStartAttempt(SkillInfo.destructiveLink, "critical_link_target", "destructive_link", onlyUrgent);
        }

        /// <summary>
        /// Picks the party member who needs this link most (no link from this character first, then the oldest link)
        /// and puts the cursor on them. Every party member in sight is linked in turn.
        /// </summary>
        private bool TryStartAttempt(Core.Skills.Skill linkSkill, string targetBuffName, string linkType, bool onlyUrgent)
        {
            var skill = _instance.skills?.FirstOrDefault(s => s.Id == linkSkill.Id);
            if (skill == null) return false;

            var key = _instance.GetSkillInputKey(skill.SkillSlotIndex);
            if (key == default(Keys))
            {
                if (_missingKeyLogged.Add(linkType))
                    _instance.LogMessage($"{linkType.ToUpper()}: skill bar slot {skill.SkillSlotIndex} has no key in Input Keys - not linking");
                return false;
            }

            if (!SkillInfo.ManageCooldown(linkSkill, skill))
                return false;

            var durationMs = LinkDurationMs(skill);
            var localId = _instance.localPlayer?.Id ?? 0;
            var allies = PartyAlliesOnScreen(localId);

            Entity bestEntity = null;
            string bestName = null;
            var bestNeed = 0.0;
            var bestHadLink = false;
            var now = DateTime.Now;

            foreach (var (name, entity) in allies)
            {
                var timerKey = $"{name}_{linkType}";
                if (_retryAfter.TryGetValue(timerKey, out var retryAt) && now < retryAt)
                    continue;

                // Need: 2 = no link from this character; else the share of the duration that has passed since our
                // last landed cast (a link we did not time ourselves, e.g. after a reload, counts as due now).
                var hasLink = OurLinkOn(entity, targetBuffName, localId) != null;
                var need = !hasLink ? 2.0
                    : _linkedAt.TryGetValue(timerKey, out var linkedAt) ? (now - linkedAt).TotalMilliseconds / durationMs
                    : 1.0;
                if (need < (onlyUrgent ? UrgentShare : RenewAfterShare))
                    continue;

                // Another party member right next to this one on screen may get the link instead: a renewal that is
                // not urgent waits until they stand apart.
                if (need < UrgentShare && IsCoveredByAnother(entity, allies))
                    continue;

                if (need > bestNeed)
                {
                    bestNeed = need;
                    bestEntity = entity;
                    bestName = name;
                    bestHadLink = hasLink;
                }
            }

            if (bestEntity == null)
                return false;

            _attempt = new LinkAttempt
            {
                LinkSkill = linkSkill,
                LinkType = linkType,
                TargetBuffName = targetBuffName,
                PlayerName = bestName,
                EntityId = bestEntity.Id,
                Key = key,
                HadLink = bestHadLink,
                AimedAt = now
            };
            Mouse.SetCursorPos(AimPoint(bestEntity));
            return true;
        }

        private void ContinueAttempt()
        {
            var a = _attempt;
            var localId = _instance.localPlayer?.Id ?? 0;
            var allies = PartyAlliesOnScreen(localId);
            var entity = allies.Select(x => x.Entity).FirstOrDefault(x => x.Id == a.EntityId);
            if (entity == null)
            {
                _attempt = null;
                return;
            }

            var timerKey = $"{a.PlayerName}_{a.LinkType}";
            var now = DateTime.Now;
            if (a.PressedAt == DateTime.MinValue)
            {
                // Keep the cursor on the ally (the follow logic may move it) until the game marks them as targeted.
                Mouse.SetCursorPos(AimPoint(entity));
                if (!IsTargeted(entity) && (now - a.AimedAt).TotalMilliseconds < MaxAimMs)
                    return;

                // The game marks another party member as the target: the cast would land on them. Try again shortly.
                if (allies.Any(x => x.Entity.Id != a.EntityId && IsTargeted(x.Entity)))
                {
                    _retryAfter[timerKey] = now.AddMilliseconds(RetryAfterCoveredMs);
                    _attempt = null;
                    return;
                }

                Keyboard.KeyPress(a.Key);
                a.PressedAt = DateTime.Now;
                _instance.RecordSkillUse("Links");
                a.LinkSkill.Cooldown = 100;
                return;
            }

            var hasLink = OurLinkOn(entity, a.TargetBuffName, localId) != null;
            if (a.HadLink)
            {
                // A renewal leaves no trace in the buff (no countdown to reset): give it a moment, then count it as
                // renewed while the link is still there. Should it have gone to the wrong ally, the next renewal is
                // due well before this link runs out.
                if ((now - a.PressedAt).TotalMilliseconds < 300)
                    return;

                if (hasLink)
                {
                    _linkedAt[timerKey] = now;
                    _retryAfter.Remove(timerKey);
                    _instance.LogMessage($"{a.LinkType.ToUpper()}: Renewed link on {a.PlayerName}");
                }

                _attempt = null;
                return;
            }

            if (hasLink)
            {
                _linkedAt[timerKey] = now;
                _lastLinkTime[timerKey] = now;
                _retryAfter.Remove(timerKey);
                _instance.LogMessage($"{a.LinkType.ToUpper()}: Linked to {a.PlayerName}");
                _attempt = null;
                return;
            }

            if ((now - a.PressedAt).TotalMilliseconds < ConfirmTimeoutMs)
                return;

            _retryAfter[timerKey] = now.AddMilliseconds(RetryAfterFailMs);
            _instance.LogMessage($"{a.LinkType.ToUpper()}: link to {a.PlayerName} did not land (key {a.Key}, out of range or not hovered?)");
            _attempt = null;
        }

        /// <summary>Party members (not this character) whose entity is loaded and on screen.</summary>
        private List<(string Name, Entity Entity)> PartyAlliesOnScreen(uint localId)
        {
            var players = _instance.GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Player]
                .Where(x => x != null && x.IsValid && !x.IsHostile && x.Id != localId)
                .ToList();

            var allies = new List<(string Name, Entity Entity)>();
            foreach (var partyElement in PartyElements.GetPlayerInfoElementList())
            {
                var name = partyElement?.PlayerName;
                if (string.IsNullOrEmpty(name)) continue;

                var entity = players.FirstOrDefault(x =>
                    string.Equals(x.GetComponent<Player>()?.PlayerName, name, StringComparison.OrdinalIgnoreCase));
                if (entity != null && IsOnScreen(entity))
                    allies.Add((name, entity));
            }

            return allies;
        }

        /// <summary>The link's duration from this character's skill (gem level, quality and duration modifiers included).</summary>
        private static double LinkDurationMs(ActorSkill skill)
        {
            try
            {
                if (skill.Stats != null && skill.Stats.TryGetValue(GameStat.SkillEffectDuration, out var ms) && ms > 0)
                    return ms;
            }
            catch
            {
            }

            return FallbackDurationMs;
        }

        private bool IsCoveredByAnother(Entity entity, List<(string Name, Entity Entity)> allies)
        {
            var point = AimPoint(entity);
            return allies.Any(x => x.Entity.Id != entity.Id && Vector2.Distance(AimPoint(x.Entity), point) < OverlapPx);
        }

        private static bool IsTargeted(Entity entity)
        {
            try { return entity.GetComponent<Targetable>()?.isTargeted == true; }
            catch { return false; }
        }

        /// <summary>The ally's link buff of this type cast by this character (another linker's buff does not count).</summary>
        private static Buff OurLinkOn(Entity entity, string targetBuffName, uint localId)
        {
            var buffs = entity.GetComponent<Buffs>()?.BuffsList;
            return buffs?.FirstOrDefault(b => b != null && b.Name == targetBuffName &&
                                              (b.SourceEntityId == localId || b.SourceEntityId == 0));
        }

        /// <summary>The ally's body centre in screen coordinates: the feet point often misses their hitbox.</summary>
        private static Vector2 AimPoint(Entity entity) => Helper.WorldToValidScreenPosition(entity.BoundsCenterPos);

        private bool IsOnScreen(Entity entity)
        {
            try
            {
                var point = _instance.GameController.IngameState.Camera.WorldToScreen(entity.BoundsCenterPos);
                var window = _instance.GameController.Window.GetWindowRectangle();
                return point.X > 0 && point.Y > 0 && point.X < window.Width && point.Y < window.Height;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// NEW: Task-based skill execution
        /// Creates skill tasks for links instead of executing immediately
        /// </summary>
        public List<TaskNode> CreateSkillTasks()
        {
            var tasks = new List<TaskNode>();
            
            // Only create tasks if we're within follow range
            if (!_instance.IsWithinFollowRange())
                return tasks;
            
            // Create flame link tasks
            if (_settings.flameLinkEnabled)
            {
                var flameLinkTasks = CreateLinkTasks(SkillInfo.flameLink, "flame_link_target", "flame_link", TaskNodeType.FlameLink);
                tasks.AddRange(flameLinkTasks);
            }
            
            // Create protective link tasks
            if (_settings.protectiveLinkEnabled)
            {
                var protectiveLinkTasks = CreateLinkTasks(SkillInfo.protectiveLink, "bulwark_link_target", "protective_link", TaskNodeType.ProtectiveLink);
                tasks.AddRange(protectiveLinkTasks);
            }
            
            // Create destructive link tasks
            if (_settings.destructiveLinkEnabled)
            {
                var destructiveLinkTasks = CreateLinkTasks(SkillInfo.destructiveLink, "critical_link_target", "destructive_link", TaskNodeType.DestructiveLink);
                tasks.AddRange(destructiveLinkTasks);
            }
            
            return tasks;
        }

        /// <summary>
        /// Creates link tasks for a specific link type
        /// </summary>
        private List<TaskNode> CreateLinkTasks(Core.Skills.Skill linkSkill, string targetBuffName, string linkType, TaskNodeType taskType)
        {
            var tasks = new List<TaskNode>();
            
            var skill = _instance.skills.FirstOrDefault(s => s.Id == linkSkill.Id);
            if (skill == null) return tasks;

            if (!SkillInfo.ManageCooldown(linkSkill, skill))
                return tasks;

            var partyElements = PartyElements.GetPlayerInfoElementList();
            var playerEntities = _instance.GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Player]
                .Where(x => x != null && x.IsValid && !x.IsHostile)
                .ToList();

            var linkSourceBuff = _instance.Buffs.FirstOrDefault(x => x.Name == linkSkill.BuffName);
            var linkSourceTimeLeft = linkSourceBuff?.Timer ?? 0;

            foreach (var partyElement in partyElements)
            {
                if (partyElement?.PlayerName == null) continue;

                var playerEntity = playerEntities
                    .FirstOrDefault(x => string.Equals(x.GetComponent<Player>()?.PlayerName?.ToLower(),
                        partyElement.PlayerName.ToLower(), StringComparison.CurrentCultureIgnoreCase));

                if (playerEntity != null)
                {
                    var playerBuffs = playerEntity.GetComponent<Buffs>()?.BuffsList ?? new System.Collections.Generic.List<Buff>();
                    var linkTargetBuff = playerBuffs.FirstOrDefault(x => x.Name == targetBuffName);

                    bool needsLinking = false;
                    string reason = "";

                    var timerKey = $"{partyElement.PlayerName}_{linkType}";
                    if (!_lastLinkTime.ContainsKey(timerKey))
                    {
                        _lastLinkTime[timerKey] = DateTime.MinValue;
                    }

                    var timeSinceLastLink = (DateTime.Now - _lastLinkTime[timerKey]).TotalSeconds;

                    if (linkTargetBuff == null)
                    {
                        needsLinking = true;
                        reason = "no buff";
                    }
                    else if (linkTargetBuff.Timer < 5)
                    {
                        needsLinking = true;
                        reason = $"buff low ({linkTargetBuff.Timer:F1}s)";
                    }
                    else if (timeSinceLastLink >= 4)
                    {
                        needsLinking = true;
                        reason = $"refresh ({timeSinceLastLink:F1}s since last link)";
                    }

                    if (needsLinking)
                    {
                        var mouseScreenPos = _instance.GetMousePosition();
                        var targetScreenPos = Helper.WorldToValidScreenPosition(playerEntity.Pos);
                        var distanceToCursor = Vector2.Distance(mouseScreenPos, targetScreenPos);

                        var maxDistance = 150;
                        var canLink = (linkSourceBuff == null || distanceToCursor < maxDistance) &&
                                      (linkSourceTimeLeft > 2 || linkSourceBuff == null);

                        if (canLink)
                        {
                            // Create a task instead of executing immediately
                            var linkTask = new TaskNode(playerEntity.Pos, 0, taskType)
                            {
                                SkillName = linkType,
                                TargetEntity = playerEntity,
                                SkillSlotIndex = skill.SkillSlotIndex,
                                SkillKey = _instance.GetSkillInputKey(skill.SkillSlotIndex),
                                SkillData = new SkillExecutionData
                                {
                                    TargetPlayerName = partyElement.PlayerName,
                                    Reason = reason,
                                    DistanceToTarget = distanceToCursor,
                                    TimeSinceLastUse = (float)timeSinceLastLink
                                }
                            };
                            
                            tasks.Add(linkTask);
                            _instance.LogMessage($"SKILL TASK CREATED: {linkType.ToUpper()} task for {partyElement.PlayerName} ({reason}, Distance: {distanceToCursor:F1})");
                            
                            // Only create one link task per update to avoid flooding the queue
                            break;
                        }
                    }
                }
            }

            return tasks;
        }
    }
}
