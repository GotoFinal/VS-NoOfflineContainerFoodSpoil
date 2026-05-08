using HarmonyLib;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace NoOfflineContainerFoodSpoil
{
    public class NoOfflineContainerFoodSpoilModSystem : ModSystem
    {
        private const string ConfigFileName = "NoOfflineContainerFoodSpoil.json";
        private const string PlayerSessionHistoryDataKey = "NoOfflineContainerFoodSpoil.PlayerSessionHistory";
        private const double SecondsPerDay = 86400;

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

            if (GetOfflinePreserveBehavior(blockSel.Position) is not { Blockentity: BlockEntityContainer container } behavior)
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

            BlockEntityBehaviorOfflinePreserve? behavior = GetOfflinePreserveBehavior(blockSel.Position);
            if (behavior == null || !WasModifiedThisTick.Remove(behavior))
            {
                return;
            }

            behavior.ProcessMeaningfulInteraction(player);
        }

        private BlockEntityBehaviorOfflinePreserve? GetOfflinePreserveBehavior(BlockPos position)
        {
            BlockEntity? blockEntity = ServerApi?.World.BlockAccessor.GetBlockEntity(position);
            return blockEntity is BlockEntityContainer
                ? blockEntity.GetBehavior<BlockEntityBehaviorOfflinePreserve>()
                : null;
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

            history.Sessions.Add(CreateSessionEntry(activeSession, nowUnixSeconds, nowWorldHours));

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
                    snapshotSessions.Add(CreateSessionEntry(activeSession, nowUnixSeconds, nowWorldHours));
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

        private static PlayerSessionEntry CreateSessionEntry(ActivePlayerSession session, double endUnixSeconds, double endWorldHours)
        {
            return new PlayerSessionEntry
            {
                StartUnixSeconds = session.StartUnixSeconds,
                EndUnixSeconds = endUnixSeconds,
                StartWorldHours = session.StartWorldHours,
                EndWorldHours = endWorldHours
            };
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
                sessions.Add(CreateSessionEntry(activeSession, nowUnixSeconds, nowWorldHours));
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
    }
}
