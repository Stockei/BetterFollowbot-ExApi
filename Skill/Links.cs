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

        // A link is renewed once less than this share of its duration is left (at least MinRefreshSeconds).
        private const float RefreshShare = 0.4f;
        private const float MinRefreshSeconds = 1.5f;
        // The cursor rests on the ally until the game marks them as targeted, at most this long, before the key press.
        private const double MaxAimMs = 150;
        // After the key press the ally must show a fresh link buff from this character within this time.
        private const double ConfirmTimeoutMs = 1500;
        // An ally whose link did not land is not tried again for this long (the other allies get their turn).
        private const double RetryAfterFailMs = 2000;

        /// <summary>One link cast on one ally, spread over a few frames: aim, press, confirm.</summary>
        private sealed class LinkAttempt
        {
            public Core.Skills.Skill LinkSkill;
            public string LinkType;
            public string TargetBuffName;
            public string PlayerName;
            public uint EntityId;
            public Keys Key;
            public float TimerBefore;          // remaining time of our link on the ally before the cast; -1 = none
            public DateTime AimedAt;
            public DateTime PressedAt = DateTime.MinValue;
        }

        private LinkAttempt _attempt;
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

            // Only use skills when within follow range of the leader
            if (!_instance.IsWithinFollowRange()) return;

            if (_settings.flameLinkEnabled &&
                TryStartAttempt(SkillInfo.flameLink, "flame_link_target", "flame_link"))
                return;

            if (_settings.protectiveLinkEnabled &&
                TryStartAttempt(SkillInfo.protectiveLink, "bulwark_link_target", "protective_link"))
                return;

            // Destructive Link's buffs are named critical_link_* in the game data.
            if (_settings.destructiveLinkEnabled)
                TryStartAttempt(SkillInfo.destructiveLink, "critical_link_target", "destructive_link");
        }

        /// <summary>
        /// Picks the party member who needs this link most (none from this character first, then the least time left)
        /// and puts the cursor on them. Every party member in sight is linked in turn.
        /// </summary>
        private bool TryStartAttempt(Core.Skills.Skill linkSkill, string targetBuffName, string linkType)
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

            var localId = _instance.localPlayer?.Id ?? 0;
            var playerEntities = _instance.GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Player]
                .Where(x => x != null && x.IsValid && !x.IsHostile && x.Id != localId)
                .ToList();

            Entity bestEntity = null;
            string bestName = null;
            var bestTimer = float.MaxValue;
            var now = DateTime.Now;

            foreach (var partyElement in PartyElements.GetPlayerInfoElementList())
            {
                var name = partyElement?.PlayerName;
                if (string.IsNullOrEmpty(name)) continue;

                if (_retryAfter.TryGetValue($"{name}_{linkType}", out var retryAt) && now < retryAt)
                    continue;

                var entity = playerEntities.FirstOrDefault(x =>
                    string.Equals(x.GetComponent<Player>()?.PlayerName, name, StringComparison.OrdinalIgnoreCase));
                if (entity == null || !IsOnScreen(entity)) continue;

                var ourLink = OurLinkOn(entity, targetBuffName, localId);
                var timer = ourLink?.Timer ?? -1f;
                if (ourLink != null && timer >= RefreshThreshold(ourLink))
                    continue;

                if (timer < bestTimer)
                {
                    bestTimer = timer;
                    bestEntity = entity;
                    bestName = name;
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
                TimerBefore = bestTimer,
                AimedAt = now
            };
            Mouse.SetCursorPos(AimPoint(bestEntity));
            return true;
        }

        private void ContinueAttempt()
        {
            var a = _attempt;
            var entity = _instance.GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Player]
                .FirstOrDefault(x => x != null && x.IsValid && x.Id == a.EntityId);
            if (entity == null)
            {
                _attempt = null;
                return;
            }

            var now = DateTime.Now;
            if (a.PressedAt == DateTime.MinValue)
            {
                // Keep the cursor on the ally (the follow logic may move it) until the game marks them as targeted.
                Mouse.SetCursorPos(AimPoint(entity));
                var targeted = entity.GetComponent<Targetable>()?.isTargeted == true;
                if (!targeted && (now - a.AimedAt).TotalMilliseconds < MaxAimMs)
                    return;

                Keyboard.KeyPress(a.Key);
                a.PressedAt = DateTime.Now;
                _instance.RecordSkillUse("Links");
                a.LinkSkill.Cooldown = 100;
                return;
            }

            // Landed: the ally carries this character's link buff, new or renewed.
            var ourLink = OurLinkOn(entity, a.TargetBuffName, _instance.localPlayer?.Id ?? 0);
            if (ourLink != null && (a.TimerBefore < 0 || ourLink.Timer > a.TimerBefore + 0.5f))
            {
                _lastLinkTime[$"{a.PlayerName}_{a.LinkType}"] = now;
                _retryAfter.Remove($"{a.PlayerName}_{a.LinkType}");
                _instance.LogMessage($"{a.LinkType.ToUpper()}: Linked to {a.PlayerName} ({ourLink.Timer:F1}s)");
                _attempt = null;
                return;
            }

            if ((now - a.PressedAt).TotalMilliseconds < ConfirmTimeoutMs)
                return;

            _retryAfter[$"{a.PlayerName}_{a.LinkType}"] = now.AddMilliseconds(RetryAfterFailMs);
            _instance.LogMessage($"{a.LinkType.ToUpper()}: link to {a.PlayerName} did not land (key {a.Key}, out of range or not hovered?)");
            _attempt = null;
        }

        /// <summary>The ally's link buff of this type cast by this character (another linker's buff does not count).</summary>
        private static Buff OurLinkOn(Entity entity, string targetBuffName, uint localId)
        {
            var buffs = entity.GetComponent<Buffs>()?.BuffsList;
            return buffs?.FirstOrDefault(b => b != null && b.Name == targetBuffName &&
                                              (b.SourceEntityId == localId || b.SourceEntityId == 0));
        }

        private static float RefreshThreshold(Buff link) =>
            Math.Max(MinRefreshSeconds, link.MaxTime > 0 ? link.MaxTime * RefreshShare : MinRefreshSeconds);

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
