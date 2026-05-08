using HarmonyLib;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace NoOfflineContainerFoodSpoil
{
    public class NoOfflineContainerFoodSpoilModSystem : ModSystem
    {
        private const string ConfigFileName = "NoOfflineContainerFoodSpoil.json";
        private const string PlayerSessionHistoryDataKey = "NoOfflineContainerFoodSpoil.PlayerSessionHistory";
        private const double SessionHistoryFlushIntervalSeconds = 300;

        private static bool harmonyPatched;

        private readonly Dictionary<string, PersistedPlayerSessionHistory> playerSessionHistoriesByUid = [];
        private readonly Dictionary<string, ActivePlayerSession> activePlayerSessionsByUid = [];
        private readonly HashSet<string> dirtyPlayerSessionHistories = [];

        public HashSet<BlockEntityBehaviorOfflinePreserve> LoadedContainers { get; } = [];
        internal Dictionary<InventoryBase, BlockEntityBehaviorOfflinePreserve> LoadedInventories { get; } = [];
        public NoOfflineContainerFoodSpoilConfig Config { get; private set; } = NoOfflineContainerFoodSpoilConfig.CreateDefault();
        internal ICoreServerAPI? ServerApi { get; private set; }
        internal HashSet<BlockEntityBehaviorOfflinePreserve> WasModifiedThisTick { get; } = [];
        internal bool IsDebugLoggingEnabled => Config.EnableDebugLogging;

        public override void Start(ICoreAPI api)
        {
            base.Start(api);
            api.RegisterBlockEntityBehaviorClass("OfflinePreserve", typeof(BlockEntityBehaviorOfflinePreserve));
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            base.StartServerSide(api);

            ServerApi = api;
            LoadConfig(api);
            NoOfflineContainerFoodSpoilCommands.Register(this, api);
            api.Event.DidPlaceBlock += OnDidPlaceBlock;
            api.Event.DidUseBlock += OnDidUseBlock;
            api.Event.PlayerNowPlaying += OnPlayerNowPlaying;
            api.Event.PlayerDisconnect += OnPlayerDisconnect;
            api.Event.GameWorldSave += OnGameWorldSave;
            api.Event.RegisterGameTickListener(_ => OnTick(), 1);

            PatchHarmony();
        }

        private void PatchHarmony()
        {
            if (harmonyPatched)
            {
                return;
            }

            new Harmony("NoOfflineContainerFoodSpoil.Catchup").PatchAll(typeof(NoOfflineContainerFoodSpoilHarmonyPatches).Assembly);
            harmonyPatched = true;
            LogDebug("Harmony patches applied.");
        }

        private void OnTick()
        {
            WasModifiedThisTick.Clear();
        }

        public override void AssetsFinalize(ICoreAPI api)
        {
            base.AssetsFinalize(api);

            foreach (Block block in api.World.Blocks)
            {
                if (block?.Code == null || block.EntityClass == null)
                {
                    continue;
                }

                Type entityType = api.ClassRegistry.GetBlockEntity(block.EntityClass);
                bool isContainer = entityType != null && typeof(IBlockEntityContainer).IsAssignableFrom(entityType);
                if (!isContainer)
                {
                    continue;
                }

                BlockEntityBehaviorType[] existingBehaviors = block.BlockEntityBehaviors ?? Array.Empty<BlockEntityBehaviorType>();
                if (existingBehaviors.Any(behavior => behavior.Name == "OfflinePreserve"))
                {
                    continue;
                }

                block.BlockEntityBehaviors = existingBehaviors
                    .Append(new BlockEntityBehaviorType { Name = "OfflinePreserve" })
                    .ToArray();
            }
        }

        private void LoadConfig(ICoreAPI api)
        {
            try
            {
                Config = api.LoadModConfig<NoOfflineContainerFoodSpoilConfig>(ConfigFileName) ?? NoOfflineContainerFoodSpoilConfig.CreateDefault();
            }
            catch (Exception ex)
            {
                api.Logger.Error($"[NoOfflineFoodSpoil] Failed to load config. Using defaults. Error: {ex.Message}");
                Config = NoOfflineContainerFoodSpoilConfig.CreateDefault();
            }

            Config.ClampToSafeMinimums();
            api.StoreModConfig(Config, ConfigFileName);
            LogDebug($"Config loaded. DebugLogging={Config.EnableDebugLogging}, OfflineSpoilageMultiplier={Config.OfflineSpoilageMultiplier:0.###}, ResidentExpiryRealDays={Config.ResidentExpiryRealDays:0.###}, ProvisionalExpiryRealHours={Config.ProvisionalExpiryRealHours:0.###}, SessionHistoryPruneRealDays={Config.SessionHistoryPruneRealDays:0.###}");
        }

        internal void LogDebug(string message)
        {
            if (!IsDebugLoggingEnabled)
            {
                return;
            }

            ServerApi?.Logger.Notification($"[NoOfflineFoodSpoil] {message}");
        }

        internal static string FormatUnixSecondsForLog(double unixSeconds)
        {
            if (unixSeconds <= 0)
            {
                return "n/a";
            }

            long roundedSeconds = Convert.ToInt64(Math.Round(unixSeconds, MidpointRounding.AwayFromZero));
            return DateTimeOffset.FromUnixTimeSeconds(roundedSeconds).ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
        }

        internal void SaveConfigAndRefreshLoadedContainers()
        {
            if (ServerApi == null)
            {
                return;
            }

            Config.ClampToSafeMinimums();
            ServerApi.StoreModConfig(Config, ConfigFileName);

            foreach (BlockEntityBehaviorOfflinePreserve behavior in LoadedContainers.ToArray())
            {
                behavior.OnConfigChanged();
            }

            LogDebug($"Config saved and applied to {LoadedContainers.Count} loaded containers.");
        }

        internal void RegisterLoadedContainer(BlockEntityBehaviorOfflinePreserve behavior, InventoryBase inventory)
        {
            LoadedContainers.Add(behavior);
            LoadedInventories[inventory] = behavior;
        }

        internal void UnregisterLoadedContainer(BlockEntityBehaviorOfflinePreserve behavior, InventoryBase inventory)
        {
            LoadedContainers.Remove(behavior);

            if (LoadedInventories.TryGetValue(inventory, out BlockEntityBehaviorOfflinePreserve? mappedBehavior) && ReferenceEquals(mappedBehavior, behavior))
            {
                LoadedInventories.Remove(inventory);
            }
        }

        internal bool TryGetLoadedBehavior(ItemSlot slot, out BlockEntityBehaviorOfflinePreserve? behavior)
        {
            behavior = null;
            InventoryBase? inventory = slot.Inventory;
            return inventory != null && LoadedInventories.TryGetValue(inventory, out behavior);
        }

        internal void TryReconcilePendingUnloadCatchup(IWorldAccessor world, ItemSlot slot)
        {
            if (world.Side != EnumAppSide.Server)
            {
                return;
            }

            if (TryGetLoadedBehavior(slot, out BlockEntityBehaviorOfflinePreserve? behavior) && behavior?.HasTrackedUsersForCatchup == true)
            {
                behavior.TryReconcilePendingUnloadCatchup(world);
            }
        }

        private void OnDidPlaceBlock(IServerPlayer player, int oldBlockId, BlockSelection blockSel, ItemStack stack)
        {
            if (ServerApi is not { Side: EnumAppSide.Server })
            {
                return;
            }

            BlockEntity? blockEntity = ServerApi.World.BlockAccessor.GetBlockEntity(blockSel.Position);
            if (blockEntity is not BlockEntityContainer container)
            {
                return;
            }

            BlockEntityBehaviorOfflinePreserve? behavior = blockEntity.GetBehavior<BlockEntityBehaviorOfflinePreserve>();
            if (behavior == null)
            {
                return;
            }

            behavior.SeedResident(player.PlayerUID);
            behavior.Init(container, ServerApi);
        }

        private void OnDidUseBlock(IServerPlayer player, BlockSelection blockSel)
        {
            if (ServerApi is not { Side: EnumAppSide.Server })
            {
                return;
            }

            BlockEntity? blockEntity = ServerApi.World.BlockAccessor.GetBlockEntity(blockSel.Position);
            if (blockEntity is not BlockEntityContainer)
            {
                return;
            }

            BlockEntityBehaviorOfflinePreserve? behavior = blockEntity.GetBehavior<BlockEntityBehaviorOfflinePreserve>();
            if (behavior == null || !WasModifiedThisTick.Remove(behavior))
            {
                return;
            }

            behavior.ProcessMeaningfulInteraction(player);
        }

        private void OnPlayerNowPlaying(IServerPlayer player)
        {
            double nowUnixSeconds = GetUnixTimeSeconds();
            double nowWorldHours = ServerApi?.World.Calendar.TotalHours ?? 0;

            activePlayerSessionsByUid[player.PlayerUID] = new ActivePlayerSession
            {
                StartUnixSeconds = nowUnixSeconds,
                StartWorldHours = nowWorldHours
            };

            MarkPlayerSessionHistoryDirty(player.PlayerUID);
            LogDebug($"Session started for {player.PlayerName} ({player.PlayerUID}) at unix={FormatUnixSecondsForLog(nowUnixSeconds)}, worldHours={nowWorldHours:0.###}.");
        }

        private void OnPlayerDisconnect(IServerPlayer player)
        {
            if (!activePlayerSessionsByUid.TryGetValue(player.PlayerUID, out ActivePlayerSession? activeSession))
            {
                return;
            }

            double nowUnixSeconds = GetUnixTimeSeconds();
            double nowWorldHours = ServerApi?.World.Calendar.TotalHours ?? 0;
            PersistedPlayerSessionHistory history = GetOrLoadPersistedPlayerSessionHistory(player.PlayerUID);

            history.Sessions.Add(new PlayerSessionEntry
            {
                StartUnixSeconds = activeSession.StartUnixSeconds,
                EndUnixSeconds = nowUnixSeconds,
                StartWorldHours = activeSession.StartWorldHours,
                EndWorldHours = nowWorldHours
            });

            activePlayerSessionsByUid.Remove(player.PlayerUID);
            MarkPlayerSessionHistoryDirty(player.PlayerUID);
            LogDebug($"Session closed for {player.PlayerName} ({player.PlayerUID}). Start={FormatUnixSecondsForLog(activeSession.StartUnixSeconds)}, End={FormatUnixSecondsForLog(nowUnixSeconds)}, StartWorldHours={activeSession.StartWorldHours:0.###}, EndWorldHours={nowWorldHours:0.###}, DurationSeconds={Math.Max(0, nowUnixSeconds - activeSession.StartUnixSeconds):0}.");
            FlushDirtyPlayerSessionHistories(includeActiveSessions: false);
        }

        private void OnGameWorldSave()
        {
            LogDebug($"World save started. DirtySessionHistories={dirtyPlayerSessionHistories.Count}, ActiveSessions={activePlayerSessionsByUid.Count}, LoadedContainers={LoadedContainers.Count}.");
            FlushDirtyPlayerSessionHistories(includeActiveSessions: true);

            foreach (BlockEntityBehaviorOfflinePreserve behavior in LoadedContainers.ToArray())
            {
                behavior.SaveUnloadCheckpointIfNeeded();
            }

            LogDebug("World save finished.");
        }

        private void MarkPlayerSessionHistoryDirty(string playerUid)
        {
            dirtyPlayerSessionHistories.Add(playerUid);
        }

        private PersistedPlayerSessionHistory GetOrLoadPersistedPlayerSessionHistory(string playerUid)
        {
            if (playerSessionHistoriesByUid.TryGetValue(playerUid, out PersistedPlayerSessionHistory? history))
            {
                return history;
            }

            PersistedPlayerSessionHistory loadedHistory = new PersistedPlayerSessionHistory();
            IServerPlayerData? playerData = ServerApi?.PlayerData.GetPlayerDataByUid(playerUid);
            if (playerData?.CustomPlayerData != null && playerData.CustomPlayerData.TryGetValue(PlayerSessionHistoryDataKey, out string? json) && !string.IsNullOrWhiteSpace(json))
            {
                try
                {
                    loadedHistory = JsonConvert.DeserializeObject<PersistedPlayerSessionHistory>(json) ?? new PersistedPlayerSessionHistory();
                    LogDebug($"Loaded persisted session history for {playerUid}. Sessions={loadedHistory.Sessions.Count}.");
                }
                catch
                {
                    loadedHistory = new PersistedPlayerSessionHistory();
                    LogDebug($"Failed to deserialize persisted session history for {playerUid}. Starting with an empty history.");
                }
            }

            PrunePlayerSessionHistory(loadedHistory, GetUnixTimeSeconds());
            playerSessionHistoriesByUid[playerUid] = loadedHistory;
            return loadedHistory;
        }

        private void FlushDirtyPlayerSessionHistories(bool includeActiveSessions)
        {
            if (ServerApi == null)
            {
                return;
            }

            if (includeActiveSessions)
            {
                foreach (string playerUid in activePlayerSessionsByUid.Keys)
                {
                    dirtyPlayerSessionHistories.Add(playerUid);
                }
            }

            double nowUnixSeconds = GetUnixTimeSeconds();
            double nowWorldHours = ServerApi.World.Calendar.TotalHours;
            LogDebug($"Flushing player session histories. DirtyPlayers={dirtyPlayerSessionHistories.Count}, IncludeActiveSessions={includeActiveSessions}, NowUnix={FormatUnixSecondsForLog(nowUnixSeconds)}, NowWorldHours={nowWorldHours:0.###}.");

            foreach (string playerUid in dirtyPlayerSessionHistories.ToArray())
            {
                PersistedPlayerSessionHistory history = GetOrLoadPersistedPlayerSessionHistory(playerUid);
                int sessionCountBeforePrune = history.Sessions.Count;
                PrunePlayerSessionHistory(history, nowUnixSeconds);
                int sessionCountAfterPrune = history.Sessions.Count;

                List<PlayerSessionEntry> snapshotSessions = new List<PlayerSessionEntry>(history.Sessions);
                bool includedActiveSession = false;
                if (activePlayerSessionsByUid.TryGetValue(playerUid, out ActivePlayerSession? activeSession))
                {
                    snapshotSessions.Add(new PlayerSessionEntry
                    {
                        StartUnixSeconds = activeSession.StartUnixSeconds,
                        EndUnixSeconds = nowUnixSeconds,
                        StartWorldHours = activeSession.StartWorldHours,
                        EndWorldHours = nowWorldHours
                    });
                    includedActiveSession = true;
                }

                IServerPlayerData? playerData = ServerApi.PlayerData.GetPlayerDataByUid(playerUid);
                if (playerData?.CustomPlayerData == null)
                {
                    LogDebug($"Skipping session history flush for {playerUid} because player data is unavailable.");
                    continue;
                }

                string json = JsonConvert.SerializeObject(new PersistedPlayerSessionHistory { Sessions = snapshotSessions });
                playerData.CustomPlayerData[PlayerSessionHistoryDataKey] = json;
                LogDebug($"Persisted session history for {playerUid}. StoredSessions={snapshotSessions.Count}, PersistedOnlySessions={history.Sessions.Count}, SessionsBeforePrune={sessionCountBeforePrune}, SessionsAfterPrune={sessionCountAfterPrune}, IncludedActiveSession={includedActiveSession}.");
            }

            dirtyPlayerSessionHistories.Clear();
            LogDebug("Player session history flush finished.");
        }

        private void PrunePlayerSessionHistory(PersistedPlayerSessionHistory history, double nowUnixSeconds)
        {
            double cutoffUnixSeconds = nowUnixSeconds - Config.SessionHistoryPruneRealDays * SecondsPerDay;
            int removedSessions = history.Sessions.RemoveAll(session => session.EndUnixSeconds < cutoffUnixSeconds);
            if (removedSessions > 0)
            {
                LogDebug($"Pruned {removedSessions} old session entries older than unix={FormatUnixSecondsForLog(cutoffUnixSeconds)}.");
            }
        }

        internal List<PlayerSessionCoverageSegment> GetPlayerSessionCoverageSegments(string playerUid, double clipStartUnixSeconds, double clipEndUnixSeconds, double nowUnixSeconds, double nowWorldHours)
        {
            if (clipEndUnixSeconds <= clipStartUnixSeconds)
            {
                return [];
            }

            PersistedPlayerSessionHistory history = GetOrLoadPersistedPlayerSessionHistory(playerUid);
            List<PlayerSessionEntry> sessions = new List<PlayerSessionEntry>(history.Sessions);

            if (activePlayerSessionsByUid.TryGetValue(playerUid, out ActivePlayerSession? activeSession))
            {
                sessions.Add(new PlayerSessionEntry
                {
                    StartUnixSeconds = activeSession.StartUnixSeconds,
                    EndUnixSeconds = nowUnixSeconds,
                    StartWorldHours = activeSession.StartWorldHours,
                    EndWorldHours = nowWorldHours
                });
            }

            List<PlayerSessionCoverageSegment> coverageSegments = [];
            foreach (PlayerSessionEntry session in sessions)
            {
                if (session.EndUnixSeconds <= clipStartUnixSeconds || session.StartUnixSeconds >= clipEndUnixSeconds)
                {
                    continue;
                }

                double overlapStartUnixSeconds = Math.Max(clipStartUnixSeconds, session.StartUnixSeconds);
                double overlapEndUnixSeconds = Math.Min(clipEndUnixSeconds, session.EndUnixSeconds);
                if (overlapEndUnixSeconds <= overlapStartUnixSeconds)
                {
                    continue;
                }

                coverageSegments.Add(new PlayerSessionCoverageSegment(
                    overlapStartUnixSeconds,
                    overlapEndUnixSeconds,
                    InterpolateWorldHours(session, overlapStartUnixSeconds),
                    InterpolateWorldHours(session, overlapEndUnixSeconds)
                ));
            }

            LogDebug($"Computed session coverage for {playerUid}. ClipStart={FormatUnixSecondsForLog(clipStartUnixSeconds)}, ClipEnd={FormatUnixSecondsForLog(clipEndUnixSeconds)}, SourceSessions={sessions.Count}, CoverageSegments={coverageSegments.Count}.");
            return coverageSegments;
        }

        private static double InterpolateWorldHours(PlayerSessionEntry session, double unixSeconds)
        {
            double durationUnixSeconds = session.EndUnixSeconds - session.StartUnixSeconds;
            if (durationUnixSeconds <= 0)
            {
                return session.EndWorldHours;
            }

            double ratio = (unixSeconds - session.StartUnixSeconds) / durationUnixSeconds;
            return session.StartWorldHours + (session.EndWorldHours - session.StartWorldHours) * ratio;
        }

        internal static double GetUnixTimeSeconds()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        private const double SecondsPerDay = 86400;
    }

    public sealed class BlockEntityBehaviorOfflinePreserve(BlockEntity blockEntity) : BlockEntityBehavior(blockEntity)
    {
        private const double TrackingCacheWindowSeconds = 30;
        private const string TrackedUsersTreeKey = "TrackedUsersJson";
        private const string HasPendingUnloadCatchupTreeKey = "HasPendingUnloadCatchup";
        private const string LastUnloadCheckpointUnixSecondsTreeKey = "LastUnloadCheckpointUnixSeconds";
        private const string LastUnloadCheckpointWorldHoursTreeKey = "LastUnloadCheckpointWorldHours";
        private const double PresencePersistIntervalSeconds = 300;
        private float cachedPerishMultiplier = 1f;
        private bool cachedHasOnlineTrackedUser;
        private double cachedNextRefreshUnixSeconds;
        private bool trackingDirty = true;

        private NoOfflineContainerFoodSpoilModSystem? modSys;
        private string? legacyOwnerUid;
        private bool isReconcilingPendingUnloadCatchup;

        private List<TrackedUserEntry> TrackedUsers { get; set; } = [];

        private bool HasPendingUnloadCatchup { get; set; }
        private double LastUnloadCheckpointUnixSeconds { get; set; }
        private double LastUnloadCheckpointWorldHours { get; set; }

        internal bool HasPendingUnloadCatchupForCommand => HasPendingUnloadCatchup;
        internal double LastUnloadCheckpointUnixSecondsForCommand => LastUnloadCheckpointUnixSeconds;
        internal double LastUnloadCheckpointWorldHoursForCommand => LastUnloadCheckpointWorldHours;
        internal bool HasTrackedUsersForCatchup => TrackedUsers.Count > 0;

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetString(TrackedUsersTreeKey, JsonConvert.SerializeObject(TrackedUsers));
            tree.SetBool(HasPendingUnloadCatchupTreeKey, HasPendingUnloadCatchup);
            tree.SetDouble(LastUnloadCheckpointUnixSecondsTreeKey, LastUnloadCheckpointUnixSeconds);
            tree.SetDouble(LastUnloadCheckpointWorldHoursTreeKey, LastUnloadCheckpointWorldHours);
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessor)
        {
            base.FromTreeAttributes(tree, worldAccessor);

            legacyOwnerUid = tree.GetString("OwnerUID");
            HasPendingUnloadCatchup = tree.GetBool(HasPendingUnloadCatchupTreeKey);
            LastUnloadCheckpointUnixSeconds = tree.GetDouble(LastUnloadCheckpointUnixSecondsTreeKey);
            LastUnloadCheckpointWorldHours = tree.GetDouble(LastUnloadCheckpointWorldHoursTreeKey);

            string trackedUsersJson = tree.GetString(TrackedUsersTreeKey);
            if (string.IsNullOrWhiteSpace(trackedUsersJson))
            {
                TrackedUsers = [];
                return;
            }

            try
            {
                TrackedUsers = JsonConvert.DeserializeObject<List<TrackedUserEntry>>(trackedUsersJson) ?? [];
            }
            catch
            {
                TrackedUsers = [];
            }
        }

        public override void Initialize(ICoreAPI api, JsonObject properties)
        {
            base.Initialize(api, properties);

            if (Blockentity is not BlockEntityContainer container || api.Side != EnumAppSide.Server)
            {
                return;
            }

            Init(container, api);
        }

        internal void Init(BlockEntityContainer container, ICoreAPI api)
        {
            modSys = api.ModLoader.GetModSystem<NoOfflineContainerFoodSpoilModSystem>();

            container.Inventory.OnAcquireTransitionSpeed -= OnAcquireTransitionSpeed;
            container.Inventory.OnAcquireTransitionSpeed += OnAcquireTransitionSpeed;

            container.Inventory.OnInventoryOpened -= OnInventoryOpened;
            container.Inventory.OnInventoryOpened += OnInventoryOpened;

            container.Inventory.SlotModified -= OnSlotModified;
            container.Inventory.SlotModified += OnSlotModified;

            modSys.RegisterLoadedContainer(this, container.Inventory);
            MigrateLegacyOwnerIfNeeded();
            RefreshTrackingCache(forcePersist: true);
            LogDebug($"Initialized. TrackedUsers={TrackedUsers.Count}, PendingUnloadCatchup={HasPendingUnloadCatchup}, LastUnloadCheckpoint={NoOfflineContainerFoodSpoilModSystem.FormatUnixSecondsForLog(LastUnloadCheckpointUnixSeconds)}, LastUnloadCheckpointWorldHours={LastUnloadCheckpointWorldHours:0.###}.");
        }

        public override void OnBlockRemoved()
        {
            base.OnBlockRemoved();
            Unsubscribe();
        }

        public override void OnBlockUnloaded()
        {
            SaveUnloadCheckpointIfNeeded();
            base.OnBlockUnloaded();
            Unsubscribe();
        }

        public void SeedResident(string playerUid)
        {
            if (string.IsNullOrWhiteSpace(playerUid))
            {
                return;
            }

            double nowUnixSeconds = NoOfflineContainerFoodSpoilModSystem.GetUnixTimeSeconds();
            TrackedUserEntry entry = GetOrCreateEntry(playerUid);
            entry.State = TrackedUserState.Resident;
            entry.LastMeaningfulInteractionUnixSeconds = nowUnixSeconds;
            entry.LastResidentPresenceUnixSeconds = nowUnixSeconds;
            entry.LastProvisionalInteractionUnixSeconds = 0;

            MarkTrackingDirty();
            RefreshTrackingCache(forcePersist: true);
            LogDebug($"Seeded resident {DescribePlayer(playerUid)}. ReferenceUnix={NoOfflineContainerFoodSpoilModSystem.FormatUnixSecondsForLog(nowUnixSeconds)}.");
        }

        private void OnInventoryOpened(IPlayer player)
        {
            TrackedUserEntry? entry = FindEntry(player.PlayerUID);
            if (entry?.State == TrackedUserState.Resident)
            {
                entry.LastResidentPresenceUnixSeconds = NoOfflineContainerFoodSpoilModSystem.GetUnixTimeSeconds();
                MarkTrackingDirty();
                Blockentity.MarkDirty();
                LogDebug($"Resident opened inventory: {DescribePlayer(player.PlayerUID)}. Presence refreshed.");
            }
        }

        private void OnSlotModified(int slotId)
        {
            if (AttributeMeaningfulInteractionToOpenViewers())
            {
                return;
            }

            modSys?.WasModifiedThisTick.Add(this);
        }

        private float OnAcquireTransitionSpeed(EnumTransitionType transType, ItemStack stack, float baseMul)
        {
            if (transType != EnumTransitionType.Perish)
            {
                return baseMul;
            }

            EnsureTrackingCacheCurrent();
            return cachedHasOnlineTrackedUser ? baseMul : baseMul * cachedPerishMultiplier;
        }

        private bool AttributeMeaningfulInteractionToOpenViewers()
        {
            List<string> openViewerUids = GetOpenViewerUids();
            if (openViewerUids.Count == 0)
            {
                return false;
            }

            double nowUnixSeconds = NoOfflineContainerFoodSpoilModSystem.GetUnixTimeSeconds();
            foreach (string playerUid in openViewerUids)
            {
                ProcessMeaningfulInteraction(playerUid, nowUnixSeconds);
            }

            MarkTrackingDirty();
            RefreshTrackingCache(forcePersist: true);
            return true;
        }

        public void ProcessMeaningfulInteraction(IPlayer player)
        {
            double nowUnixSeconds = NoOfflineContainerFoodSpoilModSystem.GetUnixTimeSeconds();
            ProcessMeaningfulInteraction(player.PlayerUID, nowUnixSeconds);

            MarkTrackingDirty();
            RefreshTrackingCache(forcePersist: true);
            LogDebug($"Meaningful interaction attributed to {DescribePlayer(player.PlayerUID)} at unix={NoOfflineContainerFoodSpoilModSystem.FormatUnixSecondsForLog(nowUnixSeconds)}.");
        }

        private void ProcessMeaningfulInteraction(string playerUid, double nowUnixSeconds)
        {
            TrackedUserEntry entry = GetOrCreateEntry(playerUid);

            if (entry.State == TrackedUserState.Resident)
            {
                entry.LastMeaningfulInteractionUnixSeconds = nowUnixSeconds;
                entry.LastResidentPresenceUnixSeconds = nowUnixSeconds;
                LogDebug($"Tracked resident interaction for {DescribePlayer(playerUid)}. State remains Resident.");
                return;
            }

            double promotionWindowSeconds = GetConfig().PromotionWindowRealDays * SecondsPerDay;
            bool withinPromotionWindow = entry.LastProvisionalInteractionUnixSeconds > 0
                && nowUnixSeconds - entry.LastProvisionalInteractionUnixSeconds <= promotionWindowSeconds;

            if (withinPromotionWindow)
            {
                entry.State = TrackedUserState.Resident;
                entry.LastMeaningfulInteractionUnixSeconds = nowUnixSeconds;
                entry.LastResidentPresenceUnixSeconds = nowUnixSeconds;
                entry.LastProvisionalInteractionUnixSeconds = 0;
                LogDebug($"Promoted {DescribePlayer(playerUid)} from Provisional to Resident. PromotionWindowSeconds={promotionWindowSeconds:0}.");
                return;
            }

            entry.State = TrackedUserState.Provisional;
            entry.LastMeaningfulInteractionUnixSeconds = nowUnixSeconds;
            entry.LastProvisionalInteractionUnixSeconds = nowUnixSeconds;
            LogDebug($"Marked {DescribePlayer(playerUid)} as Provisional from meaningful interaction.");
        }

        private void MigrateLegacyOwnerIfNeeded()
        {
            if (TrackedUsers.Count > 0 || string.IsNullOrWhiteSpace(legacyOwnerUid))
            {
                return;
            }

            SeedResident(legacyOwnerUid);
            legacyOwnerUid = null;
        }

        private void EnsureTrackingCacheCurrent()
        {
            double nowUnixSeconds = NoOfflineContainerFoodSpoilModSystem.GetUnixTimeSeconds();
            if (!trackingDirty && nowUnixSeconds < cachedNextRefreshUnixSeconds)
            {
                return;
            }

            RefreshTrackingCache(forcePersist: false);
        }

        private void RefreshTrackingCache(bool forcePersist)
        {
            double nowUnixSeconds = NoOfflineContainerFoodSpoilModSystem.GetUnixTimeSeconds();
            bool changed = forcePersist;
            bool hasOnlineTrackedUser = false;
            NoOfflineContainerFoodSpoilConfig config = GetConfig();
            TrackedUserEntry? pinnedResident = GetPinnedResident();
            List<TrackedUserEntry>? entriesToRemove = null;
            int trackedUsersBeforeRefresh = TrackedUsers.Count;

            foreach (TrackedUserEntry entry in TrackedUsers)
            {
                RuntimeTrackedUserState state = EvaluateRuntimeState(entry, nowUnixSeconds, config);

                if (state.RefreshResidentPresence)
                {
                    entry.LastResidentPresenceUnixSeconds = nowUnixSeconds;
                    changed = true;
                }

                if (state.CountsForSpoilage)
                {
                    hasOnlineTrackedUser = true;
                }

                if (state.RemoveFromTracking && !ReferenceEquals(entry, pinnedResident))
                {
                    entriesToRemove ??= [];
                    entriesToRemove.Add(entry);
                }
            }

            if (entriesToRemove != null)
            {
                foreach (TrackedUserEntry entry in entriesToRemove)
                {
                    changed |= TrackedUsers.Remove(entry);
                }
            }

            if (TrackedUsers.Count > config.TrackedPlayerLimit)
            {
                List<TrackedUserEntry> orderedEntries = TrackedUsers
                    .OrderBy(entry => entry.State == TrackedUserState.Resident ? 1 : 0)
                    .ThenBy(GetEntryReferenceTime)
                    .ToList();

                while (orderedEntries.Count > config.TrackedPlayerLimit)
                {
                    int removeIndex = orderedEntries.FindIndex(entry => !ReferenceEquals(entry, pinnedResident));
                    if (removeIndex < 0)
                    {
                        break;
                    }

                    TrackedUserEntry entryToRemove = orderedEntries[removeIndex];
                    orderedEntries.RemoveAt(removeIndex);
                    changed |= TrackedUsers.Remove(entryToRemove);
                }
            }

            cachedHasOnlineTrackedUser = hasOnlineTrackedUser;
            cachedPerishMultiplier = config.OfflineSpoilageMultiplier;
            cachedNextRefreshUnixSeconds = nowUnixSeconds + TrackingCacheWindowSeconds;
            trackingDirty = false;

            if (changed || forcePersist)
            {
                string removedUsers = entriesToRemove == null || entriesToRemove.Count == 0
                    ? "(none)"
                    : string.Join(", ", entriesToRemove.Select(entry => DescribeEntry(entry)));
                LogDebug($"Tracking cache refreshed. ForcePersist={forcePersist}, TrackedUsersBefore={trackedUsersBeforeRefresh}, TrackedUsersAfter={TrackedUsers.Count}, HasOnlineTrackedUser={cachedHasOnlineTrackedUser}, CachedPerishMultiplier={cachedPerishMultiplier:0.###}, RemovedUsers={removedUsers}, PinnedResident={(pinnedResident == null ? "(none)" : DescribeEntry(pinnedResident))}.");
            }

            if (changed)
            {
                Blockentity.MarkDirty();
            }
        }

        private void MarkTrackingDirty()
        {
            trackingDirty = true;
            cachedNextRefreshUnixSeconds = 0;
        }

        private TrackedUserEntry? GetPinnedResident()
        {
            return TrackedUsers
                .Where(entry => entry.State == TrackedUserState.Resident)
                .OrderByDescending(GetEntryReferenceTime)
                .FirstOrDefault();
        }

        internal ContainerTrackingState EvaluateStateForCommand()
        {
            RefreshTrackingCache(forcePersist: true);
            return EvaluateTrackedUsersForDebug();
        }

        internal void OnConfigChanged()
        {
            MarkTrackingDirty();
            RefreshTrackingCache(forcePersist: true);
        }

        internal bool TryRemoveTrackedUserForCommand(string playerUid, out string error)
        {
            error = string.Empty;

            TrackedUserEntry? entry = FindEntry(playerUid);
            if (entry == null)
            {
                error = "That player is not tracked on this container.";
                return false;
            }

            if (entry.State == TrackedUserState.Resident && TrackedUsers.Count(other => other.State == TrackedUserState.Resident && other.PlayerUid != playerUid) == 0)
            {
                error = "Cannot remove the last resident from this container.";
                return false;
            }

            if (!TrackedUsers.Remove(entry))
            {
                error = "Failed to remove the tracked player from this container.";
                return false;
            }

            MarkTrackingDirty();
            RefreshTrackingCache(forcePersist: true);
            return true;
        }

        internal void SaveUnloadCheckpointIfNeeded()
        {
            if (isReconcilingPendingUnloadCatchup || modSys?.ServerApi == null || Blockentity is not BlockEntityContainer container)
            {
                return;
            }

            if (TrackedUsers.Count == 0)
            {
                if (HasPendingUnloadCatchup)
                {
                    HasPendingUnloadCatchup = false;
                    Blockentity.MarkDirty();
                }

                return;
            }

            bool hasPerishableStacks = false;
            int perishableSlotCount = 0;
            foreach (ItemSlot slot in container.Inventory)
            {
                if (!HasPerishTransition(slot, modSys.ServerApi.World))
                {
                    continue;
                }

                hasPerishableStacks = true;
                perishableSlotCount++;
                slot.Itemstack!.Collectible.UpdateAndGetTransitionState(modSys.ServerApi.World, slot, EnumTransitionType.Perish);
            }

            LastUnloadCheckpointUnixSeconds = NoOfflineContainerFoodSpoilModSystem.GetUnixTimeSeconds();
            LastUnloadCheckpointWorldHours = modSys.ServerApi.World.Calendar.TotalHours;
            HasPendingUnloadCatchup = hasPerishableStacks;
            Blockentity.MarkDirty();
            LogDebug($"Saved unload checkpoint. HasPerishableStacks={hasPerishableStacks}, PerishableSlots={perishableSlotCount}, CheckpointUnix={NoOfflineContainerFoodSpoilModSystem.FormatUnixSecondsForLog(LastUnloadCheckpointUnixSeconds)}, CheckpointWorldHours={LastUnloadCheckpointWorldHours:0.###}.");
        }

        internal void TryReconcilePendingUnloadCatchup(IWorldAccessor world)
        {
            if (TrackedUsers.Count == 0)
            {
                if (HasPendingUnloadCatchup)
                {
                    HasPendingUnloadCatchup = false;
                    Blockentity.MarkDirty();
                }

                return;
            }

            if (!HasPendingUnloadCatchup || isReconcilingPendingUnloadCatchup || modSys?.ServerApi == null || Blockentity is not BlockEntityContainer container)
            {
                return;
            }

            double nowUnixSeconds = NoOfflineContainerFoodSpoilModSystem.GetUnixTimeSeconds();
            double nowWorldHours = world.Calendar.TotalHours;
            if (LastUnloadCheckpointUnixSeconds <= 0 || nowUnixSeconds <= LastUnloadCheckpointUnixSeconds || nowWorldHours <= LastUnloadCheckpointWorldHours)
            {
                LogDebug($"Clearing invalid pending unload catch-up. CheckpointUnix={NoOfflineContainerFoodSpoilModSystem.FormatUnixSecondsForLog(LastUnloadCheckpointUnixSeconds)}, NowUnix={NoOfflineContainerFoodSpoilModSystem.FormatUnixSecondsForLog(nowUnixSeconds)}, CheckpointWorldHours={LastUnloadCheckpointWorldHours:0.###}, NowWorldHours={nowWorldHours:0.###}.");
                ClearPendingUnloadCatchup(nowUnixSeconds, nowWorldHours);
                return;
            }

            isReconcilingPendingUnloadCatchup = true;

            try
            {
                LogDebug($"Starting pending unload catch-up reconciliation. CheckpointUnix={NoOfflineContainerFoodSpoilModSystem.FormatUnixSecondsForLog(LastUnloadCheckpointUnixSeconds)}, NowUnix={NoOfflineContainerFoodSpoilModSystem.FormatUnixSecondsForLog(nowUnixSeconds)}, CheckpointWorldHours={LastUnloadCheckpointWorldHours:0.###}, NowWorldHours={nowWorldHours:0.###}, TrackedUsers={TrackedUsers.Count}.");
                float effectivePerishWorldHours = ComputeEffectivePerishWorldHours(nowUnixSeconds, nowWorldHours);
                foreach (ItemSlot slot in container.Inventory)
                {
                    ApplyPendingUnloadCatchupToSlot(slot, world, effectivePerishWorldHours);
                }

                ClearPendingUnloadCatchup(nowUnixSeconds, nowWorldHours);
                LogDebug($"Finished pending unload catch-up reconciliation. EffectivePerishWorldHours={effectivePerishWorldHours:0.###}.");
            }
            finally
            {
                isReconcilingPendingUnloadCatchup = false;
            }
        }

        private void ClearPendingUnloadCatchup(double nowUnixSeconds, double nowWorldHours)
        {
            HasPendingUnloadCatchup = false;
            LastUnloadCheckpointUnixSeconds = nowUnixSeconds;
            LastUnloadCheckpointWorldHours = nowWorldHours;
            Blockentity.MarkDirty();
            LogDebug($"Cleared pending unload catch-up. NewCheckpointUnix={NoOfflineContainerFoodSpoilModSystem.FormatUnixSecondsForLog(nowUnixSeconds)}, NewCheckpointWorldHours={nowWorldHours:0.###}.");
        }

        private float ComputeEffectivePerishWorldHours(double nowUnixSeconds, double nowWorldHours)
        {
            double totalGapWorldHours = Math.Max(0, nowWorldHours - LastUnloadCheckpointWorldHours);
            double clipStartUnixSeconds = LastUnloadCheckpointUnixSeconds;
            double clipEndUnixSeconds = nowUnixSeconds;
            NoOfflineContainerFoodSpoilConfig config = GetConfig();
            List<PlayerSessionCoverageSegment> coverageSegments = [];

            foreach (TrackedUserEntry entry in TrackedUsers)
            {
                double eligibilityEndUnixSeconds = GetUnloadCatchupEligibilityEndUnixSeconds(entry, config);
                double clippedEligibilityEndUnixSeconds = Math.Min(clipEndUnixSeconds, eligibilityEndUnixSeconds);
                if (clippedEligibilityEndUnixSeconds <= clipStartUnixSeconds)
                {
                    LogDebug($"Skipping catch-up coverage for {DescribeEntry(entry)} because eligibility ended before the checkpoint window. EligibilityEnd={NoOfflineContainerFoodSpoilModSystem.FormatUnixSecondsForLog(eligibilityEndUnixSeconds)}.");
                    continue;
                }

                List<PlayerSessionCoverageSegment> playerSegments = modSys!.GetPlayerSessionCoverageSegments(entry.PlayerUid, clipStartUnixSeconds, clippedEligibilityEndUnixSeconds, nowUnixSeconds, nowWorldHours);
                coverageSegments.AddRange(playerSegments);
                LogDebug($"Catch-up coverage for {DescribeEntry(entry)}. EligibilityEnd={NoOfflineContainerFoodSpoilModSystem.FormatUnixSecondsForLog(eligibilityEndUnixSeconds)}, ClippedEnd={NoOfflineContainerFoodSpoilModSystem.FormatUnixSecondsForLog(clippedEligibilityEndUnixSeconds)}, CoverageSegments={playerSegments.Count}.");
            }

            double countedOnlineWorldHours = Math.Min(totalGapWorldHours, CalculateUnionWorldHours(coverageSegments));
            double offlineWorldHours = Math.Max(0, totalGapWorldHours - countedOnlineWorldHours);
            float effectivePerishWorldHours = (float)(countedOnlineWorldHours + offlineWorldHours * config.OfflineSpoilageMultiplier);
            LogDebug($"Computed catch-up world hours. TotalGapWorldHours={totalGapWorldHours:0.###}, CountedOnlineWorldHours={countedOnlineWorldHours:0.###}, OfflineWorldHours={offlineWorldHours:0.###}, OfflineSpoilageMultiplier={config.OfflineSpoilageMultiplier:0.###}, EffectivePerishWorldHours={effectivePerishWorldHours:0.###}.");
            return effectivePerishWorldHours;
        }

        private double GetUnloadCatchupEligibilityEndUnixSeconds(TrackedUserEntry entry, NoOfflineContainerFoodSpoilConfig config)
        {
            if (entry.State == TrackedUserState.Provisional)
            {
                return entry.LastProvisionalInteractionUnixSeconds + config.ProvisionalExpiryRealHours * SecondsPerHour;
            }

            double referenceUnixSeconds = GetEntryReferenceTime(entry);
            double residentWindowSeconds = Math.Min(config.ResidentExpiryRealDays * SecondsPerDay, config.FarAwayExpiryRealHours * SecondsPerHour);
            return referenceUnixSeconds + residentWindowSeconds;
        }

        private static double CalculateUnionWorldHours(List<PlayerSessionCoverageSegment> coverageSegments)
        {
            if (coverageSegments.Count == 0)
            {
                return 0;
            }

            List<double> breakpoints = coverageSegments
                .SelectMany(segment => new[] { segment.StartUnixSeconds, segment.EndUnixSeconds })
                .Distinct()
                .OrderBy(value => value)
                .ToList();

            double unionWorldHours = 0;

            for (int index = 0; index < breakpoints.Count - 1; index++)
            {
                double segmentStartUnixSeconds = breakpoints[index];
                double segmentEndUnixSeconds = breakpoints[index + 1];
                if (segmentEndUnixSeconds <= segmentStartUnixSeconds)
                {
                    continue;
                }

                PlayerSessionCoverageSegment? coverage = coverageSegments.FirstOrDefault(segment =>
                    segment.StartUnixSeconds <= segmentStartUnixSeconds && segment.EndUnixSeconds >= segmentEndUnixSeconds);

                if (coverage == null)
                {
                    continue;
                }

                unionWorldHours += InterpolateWorldHours(coverage.Value, segmentEndUnixSeconds) - InterpolateWorldHours(coverage.Value, segmentStartUnixSeconds);
            }

            return unionWorldHours;
        }

        private static double InterpolateWorldHours(PlayerSessionCoverageSegment coverageSegment, double unixSeconds)
        {
            double durationUnixSeconds = coverageSegment.EndUnixSeconds - coverageSegment.StartUnixSeconds;
            if (durationUnixSeconds <= 0)
            {
                return coverageSegment.EndWorldHours;
            }

            double ratio = (unixSeconds - coverageSegment.StartUnixSeconds) / durationUnixSeconds;
            return coverageSegment.StartWorldHours + (coverageSegment.EndWorldHours - coverageSegment.StartWorldHours) * ratio;
        }

        private void ApplyPendingUnloadCatchupToSlot(ItemSlot slot, IWorldAccessor world, float effectivePerishWorldHours)
        {
            if (!HasPerishTransition(slot, world))
            {
                return;
            }

            ItemStack stack = slot.Itemstack!;
            TransitionableProperties[]? transitionProps = stack.Collectible.GetTransitionableProperties(world, stack, null);
            if (transitionProps == null || !TryGetPerishTransitionStateData(stack, transitionProps, out ITreeAttribute? attr, out float[]? transitionedHours, out int perishIndex))
            {
                return;
            }

            float previousTransitionedHours = transitionedHours![perishIndex];
            transitionedHours![perishIndex] += effectivePerishWorldHours;
            attr!.SetDouble("lastUpdatedTotalHours", world.Calendar.TotalHours);
            LogDebug($"Applied catch-up to slot. Item={stack.Collectible.Code}, StackSize={stack.StackSize}, TransitionedHoursBefore={previousTransitionedHours:0.###}, AddedHours={effectivePerishWorldHours:0.###}, TransitionedHoursAfter={transitionedHours[perishIndex]:0.###}, LastUpdatedWorldHours={world.Calendar.TotalHours:0.###}.");

            stack.Collectible.UpdateAndGetTransitionState(world, slot, EnumTransitionType.Perish);
        }

        private static bool TryGetPerishTransitionStateData(ItemStack stack, TransitionableProperties[] transitionProps, out ITreeAttribute? attr, out float[]? transitionedHours, out int perishIndex)
        {
            attr = stack.Attributes?["transitionstate"] as ITreeAttribute;
            transitionedHours = null;
            perishIndex = -1;

            if (attr == null)
            {
                return false;
            }

            for (int index = 0; index < transitionProps.Length; index++)
            {
                if (transitionProps[index]?.Type == EnumTransitionType.Perish)
                {
                    perishIndex = index;
                    break;
                }
            }

            if (perishIndex < 0)
            {
                return false;
            }

            transitionedHours = (attr["transitionedHours"] as FloatArrayAttribute)?.value;
            return transitionedHours != null && perishIndex < transitionedHours.Length;
        }

        private static bool HasPerishTransition(ItemSlot slot, IWorldAccessor world)
        {
            if (slot.Itemstack == null)
            {
                return false;
            }

            TransitionableProperties[]? transitionProps = slot.Itemstack.Collectible.GetTransitionableProperties(world, slot.Itemstack, null);
            return transitionProps != null && transitionProps.Any(prop => prop?.Type == EnumTransitionType.Perish);
        }

        private ContainerTrackingState EvaluateTrackedUsersForDebug()
        {
            double nowUnixSeconds = NoOfflineContainerFoodSpoilModSystem.GetUnixTimeSeconds();
            NoOfflineContainerFoodSpoilConfig config = GetConfig();
            List<TrackedUserStatus> states = new List<TrackedUserStatus>(TrackedUsers.Count);
            bool hasValidTrackedUser = false;

            foreach (TrackedUserEntry entry in TrackedUsers)
            {
                TrackedUserStatus state = BuildDebugState(entry, nowUnixSeconds, config);
                states.Add(state);
                hasValidTrackedUser |= state.CountsForSpoilage;
            }

            return new ContainerTrackingState
            {
                UserStates = states,
                HasValidTrackedUser = hasValidTrackedUser
            };
        }

        private RuntimeTrackedUserState EvaluateRuntimeState(TrackedUserEntry entry, double nowUnixSeconds, NoOfflineContainerFoodSpoilConfig config)
        {
            if (entry.State == TrackedUserState.Provisional)
            {
                IPlayer? player = GetOnlinePlayer(entry.PlayerUid);
                bool online = player != null;
                double provisionalAgeSeconds = Math.Max(0, nowUnixSeconds - entry.LastProvisionalInteractionUnixSeconds);
                bool withinTimeWindow = provisionalAgeSeconds <= config.ProvisionalExpiryRealHours * SecondsPerHour;

                return new RuntimeTrackedUserState(
                    countsForSpoilage: online && withinTimeWindow,
                    removeFromTracking: !withinTimeWindow,
                    refreshResidentPresence: false
                );
            }

            IPlayer? residentPlayer = GetOnlinePlayer(entry.PlayerUid);
            double? residentDistance = GetDistanceToContainer(residentPlayer);
            bool residentOnline = residentPlayer != null;
            bool withinKeepaliveRadius = residentDistance.HasValue && residentDistance.Value <= config.ResidentKeepaliveRadiusBlocks;
            bool beyondFarAwayRadius = residentDistance.HasValue && residentDistance.Value > config.FarAwayRadiusBlocks;
            double referenceTime = GetEntryReferenceTime(entry);
            double thresholdSeconds = config.ResidentExpiryRealDays * SecondsPerDay;

            if (residentOnline && beyondFarAwayRadius)
            {
                thresholdSeconds = config.FarAwayExpiryRealHours * SecondsPerHour;
            }

            bool refreshResidentPresence = false;
            if (withinKeepaliveRadius)
            {
                double previousPresence = entry.LastResidentPresenceUnixSeconds;
                if (nowUnixSeconds - previousPresence >= PresencePersistIntervalSeconds)
                {
                    refreshResidentPresence = true;
                }

                referenceTime = nowUnixSeconds;
            }

            double residentAgeSeconds = Math.Max(0, nowUnixSeconds - referenceTime);
            bool withinTrackingWindow = residentAgeSeconds <= thresholdSeconds;

            return new RuntimeTrackedUserState(
                countsForSpoilage: residentOnline && withinTrackingWindow,
                removeFromTracking: !withinTrackingWindow,
                refreshResidentPresence: refreshResidentPresence
            );
        }

        private TrackedUserStatus BuildDebugState(TrackedUserEntry entry, double nowUnixSeconds, NoOfflineContainerFoodSpoilConfig config)
        {
            return entry.State == TrackedUserState.Provisional
                ? BuildProvisionalDebugState(entry, nowUnixSeconds, config)
                : BuildResidentDebugState(entry, nowUnixSeconds, config);
        }

        private TrackedUserStatus BuildResidentDebugState(TrackedUserEntry entry, double nowUnixSeconds, NoOfflineContainerFoodSpoilConfig config)
        {
            IPlayer? player = GetOnlinePlayer(entry.PlayerUid);
            double? distance = GetDistanceToContainer(player);
            bool online = player != null;
            bool withinKeepaliveRadius = distance.HasValue && distance.Value <= config.ResidentKeepaliveRadiusBlocks;
            bool beyondFarAwayRadius = distance.HasValue && distance.Value > config.FarAwayRadiusBlocks;
            double referenceTime = GetEntryReferenceTime(entry);

            if (withinKeepaliveRadius)
            {
                referenceTime = nowUnixSeconds;
            }

            double thresholdSeconds = config.ResidentExpiryRealDays * SecondsPerDay;
            string reason = online ? "resident-online" : "resident-offline";

            if (withinKeepaliveRadius)
            {
                reason = "resident-nearby-keepalive";
            }
            else if (online && beyondFarAwayRadius)
            {
                thresholdSeconds = config.FarAwayExpiryRealHours * SecondsPerHour;
                reason = "resident-online-far-away";
            }

            double ageSeconds = Math.Max(0, nowUnixSeconds - referenceTime);
            bool withinTrackingWindow = ageSeconds <= thresholdSeconds;
            bool countsForSpoilage = online && withinTrackingWindow;
            bool removeFromTracking = !withinTrackingWindow;

            return new TrackedUserStatus
            {
                Entry = entry,
                Online = online,
                DistanceBlocks = distance,
                WithinKeepaliveRadius = withinKeepaliveRadius,
                BeyondFarAwayRadius = beyondFarAwayRadius,
                AgeSeconds = ageSeconds,
                ThresholdSeconds = thresholdSeconds,
                CountsForSpoilage = countsForSpoilage,
                RemoveFromTracking = removeFromTracking,
                Reason = countsForSpoilage
                    ? reason
                    : removeFromTracking ? $"{reason}-expired" : $"{reason}-not-counting"
            };
        }

        private TrackedUserStatus BuildProvisionalDebugState(TrackedUserEntry entry, double nowUnixSeconds, NoOfflineContainerFoodSpoilConfig config)
        {
            IPlayer? player = GetOnlinePlayer(entry.PlayerUid);
            double? distance = GetDistanceToContainer(player);
            bool online = player != null;
            double thresholdSeconds = config.ProvisionalExpiryRealHours * SecondsPerHour;
            double ageSeconds = Math.Max(0, nowUnixSeconds - entry.LastProvisionalInteractionUnixSeconds);
            bool withinTimeWindow = ageSeconds <= thresholdSeconds;

            return new TrackedUserStatus
            {
                Entry = entry,
                Online = online,
                DistanceBlocks = distance,
                WithinKeepaliveRadius = false,
                BeyondFarAwayRadius = distance.HasValue && distance.Value > config.FarAwayRadiusBlocks,
                AgeSeconds = ageSeconds,
                ThresholdSeconds = thresholdSeconds,
                CountsForSpoilage = online && withinTimeWindow,
                RemoveFromTracking = !withinTimeWindow,
                Reason = online
                    ? withinTimeWindow ? "provisional-online-grace" : "provisional-expired"
                    : withinTimeWindow ? "provisional-offline-not-counting" : "provisional-expired"
            };
        }

        private NoOfflineContainerFoodSpoilConfig GetConfig()
        {
            return modSys?.Config ?? NoOfflineContainerFoodSpoilConfig.CreateDefault();
        }

        internal List<string> GetOpenViewerUidsForCommand()
        {
            return GetOpenViewerUids();
        }

        private void LogDebug(string message)
        {
            if (TrackedUsers.Count == 0 && !HasPendingUnloadCatchup)
            {
                return;
            }

            modSys?.LogDebug($"[{Blockentity.Block.Code} @ {Blockentity.Pos}] {message}");
        }

        private string DescribePlayer(string playerUid)
        {
            IPlayer? player = GetOnlinePlayer(playerUid);
            return player == null ? playerUid : $"{player.PlayerName} ({playerUid})";
        }

        private string DescribeEntry(TrackedUserEntry entry)
        {
            return $"{DescribePlayer(entry.PlayerUid)}:{entry.State}";
        }

        private List<string> GetOpenViewerUids()
        {
            if (Blockentity is not BlockEntityContainer container)
            {
                return [];
            }

            return container.Inventory.openedByPlayerGUIds?
                .Where(uid => !string.IsNullOrWhiteSpace(uid))
                .Distinct()
                .OrderBy(uid => uid)
                .ToList() ?? [];
        }

        private IPlayer? GetOnlinePlayer(string playerUid)
        {
            ICoreServerAPI? api = modSys?.ServerApi;
            if (api == null || string.IsNullOrWhiteSpace(playerUid))
            {
                return null;
            }

            return api.World.PlayerByUid(playerUid) is IServerPlayer { Entity: not null, ConnectionState: EnumClientState.Playing } player
                ? player
                : null;
        }

        private double? GetDistanceToContainer(IPlayer? player)
        {
            if (player?.Entity?.Pos == null)
            {
                return null;
            }

            double dx = player.Entity.Pos.X - (Blockentity.Pos.X + 0.5);
            double dy = player.Entity.Pos.Y - (Blockentity.Pos.Y + 0.5);
            double dz = player.Entity.Pos.Z - (Blockentity.Pos.Z + 0.5);

            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private TrackedUserEntry? FindEntry(string playerUid)
        {
            return TrackedUsers.FirstOrDefault(entry => entry.PlayerUid == playerUid);
        }

        private TrackedUserEntry GetOrCreateEntry(string playerUid)
        {
            TrackedUserEntry? existingEntry = FindEntry(playerUid);
            if (existingEntry != null)
            {
                return existingEntry;
            }

            TrackedUserEntry newEntry = new TrackedUserEntry
            {
                PlayerUid = playerUid,
                State = TrackedUserState.Provisional
            };

            TrackedUsers.Add(newEntry);
            return newEntry;
        }

        private void Unsubscribe()
        {
            if (Blockentity is BlockEntityContainer container)
            {
                container.Inventory.OnAcquireTransitionSpeed -= OnAcquireTransitionSpeed;
                container.Inventory.OnInventoryOpened -= OnInventoryOpened;
                container.Inventory.SlotModified -= OnSlotModified;
                modSys?.UnregisterLoadedContainer(this, container.Inventory);
            }
        }

        private static double GetEntryReferenceTime(TrackedUserEntry entry)
        {
            return Math.Max(entry.LastMeaningfulInteractionUnixSeconds, entry.LastResidentPresenceUnixSeconds);
        }

        private const double SecondsPerHour = 3600;
        private const double SecondsPerDay = 86400;
    }

    public enum TrackedUserState
    {
        Resident,
        Provisional
    }

    public class TrackedUserEntry
    {
        public string PlayerUid = string.Empty;
        public TrackedUserState State;
        public double LastMeaningfulInteractionUnixSeconds;
        public double LastResidentPresenceUnixSeconds;
        public double LastProvisionalInteractionUnixSeconds;
    }

    internal sealed class TrackedUserStatus
    {
        public TrackedUserEntry Entry = null!;
        public bool Online;
        public double? DistanceBlocks;
        public bool WithinKeepaliveRadius;
        public bool BeyondFarAwayRadius;
        public double AgeSeconds;
        public double ThresholdSeconds;
        public bool CountsForSpoilage;
        public bool RemoveFromTracking;
        public string Reason = string.Empty;
    }

    internal sealed class ContainerTrackingState
    {
        public List<TrackedUserStatus> UserStates { get; init; } = [];
        public bool HasValidTrackedUser { get; init; }
    }

    internal sealed class PlayerSessionEntry
    {
        public double StartUnixSeconds;
        public double EndUnixSeconds;
        public double StartWorldHours;
        public double EndWorldHours;
    }

    internal sealed class PersistedPlayerSessionHistory
    {
        public List<PlayerSessionEntry> Sessions { get; set; } = [];
    }

    internal sealed class ActivePlayerSession
    {
        public double StartUnixSeconds;
        public double StartWorldHours;
    }

    internal readonly struct PlayerSessionCoverageSegment(double startUnixSeconds, double endUnixSeconds, double startWorldHours, double endWorldHours)
    {
        public double StartUnixSeconds { get; } = startUnixSeconds;
        public double EndUnixSeconds { get; } = endUnixSeconds;
        public double StartWorldHours { get; } = startWorldHours;
        public double EndWorldHours { get; } = endWorldHours;
    }

    internal readonly struct RuntimeTrackedUserState(bool countsForSpoilage, bool removeFromTracking, bool refreshResidentPresence)
    {
        public bool CountsForSpoilage { get; } = countsForSpoilage;
        public bool RemoveFromTracking { get; } = removeFromTracking;
        public bool RefreshResidentPresence { get; } = refreshResidentPresence;
    }
}
