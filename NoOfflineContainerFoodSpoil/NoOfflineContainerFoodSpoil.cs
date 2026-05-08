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

        // this feels risky, maybe some soft reference would be worth it? But maybe unsubscribe is good enough
        public HashSet<BlockEntityBehaviorOfflinePreserve> LoadedContainers { get; } = [];
        public NoOfflineContainerFoodSpoilConfig Config { get; private set; } = NoOfflineContainerFoodSpoilConfig.CreateDefault();
        internal ICoreServerAPI? ServerApi { get; private set; }
        internal HashSet<BlockEntityBehaviorOfflinePreserve> WasModifiedThisTick = [];

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
            api.Event.RegisterGameTickListener(_ => OnTick(), 1);
        }

        private void OnTick()
        {
            WasModifiedThisTick.Clear();
        }

        public override void AssetsFinalize(ICoreAPI api)
        {
            base.AssetsFinalize(api);

            foreach (var block in api.World.Blocks)
            {
                if (block?.Code == null || block.EntityClass == null) continue;

                Type entityType = api.ClassRegistry.GetBlockEntity(block.EntityClass);
                bool isContainer = entityType != null && typeof(IBlockEntityContainer).IsAssignableFrom(entityType);
                if (!isContainer) continue;

                BlockEntityBehaviorType[] existingBehaviors = block.BlockEntityBehaviors ?? Array.Empty<BlockEntityBehaviorType>();
                if (existingBehaviors.Any(behavior => behavior.Name == "OfflinePreserve")) continue;

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
        }

        private void OnDidPlaceBlock(IServerPlayer player, int oldBlockId, BlockSelection blockSel, ItemStack stack)
        {
            if (ServerApi is not { Side: EnumAppSide.Server }) return;
            BlockEntity? blockEntity = ServerApi.World.BlockAccessor.GetBlockEntity(blockSel.Position);
            if (blockEntity is not BlockEntityContainer container) return;
            BlockEntityBehaviorOfflinePreserve? behavior = blockEntity.GetBehavior<BlockEntityBehaviorOfflinePreserve>();
            if (behavior == null) return;
            behavior.SeedResident(player.PlayerUID);
            behavior.Init(container, ServerApi);
        }

        private void OnDidUseBlock(IServerPlayer player, BlockSelection blockSel)
        {
            if (ServerApi is not { Side: EnumAppSide.Server }) return;
            BlockEntity? blockEntity = ServerApi.World.BlockAccessor.GetBlockEntity(blockSel.Position);
            if (blockEntity is not BlockEntityContainer) return;
            BlockEntityBehaviorOfflinePreserve? behavior = blockEntity.GetBehavior<BlockEntityBehaviorOfflinePreserve>();
            if (behavior == null || !WasModifiedThisTick.Remove(behavior)) return;
            behavior.ProcessMeaningfulInteraction(player);
        }
    }

    public sealed class BlockEntityBehaviorOfflinePreserve(BlockEntity blockEntity) : BlockEntityBehavior(blockEntity)
    {
        private const double TrackingCacheWindowSeconds = 30;
        private const string TrackedUsersTreeKey = "TrackedUsersJson";
        private const double PresencePersistIntervalSeconds = 300;

        private float cachedPerishMultiplier = 1f;
        private bool cachedHasOnlineTrackedUser;
        private double cachedNextRefreshUnixSeconds;
        private bool trackingDirty = true;

        private NoOfflineContainerFoodSpoilModSystem? modSys;
        private string? legacyOwnerUid;

        private List<TrackedUserEntry> TrackedUsers { get; set; } = [];

        public override void ToTreeAttributes(ITreeAttribute tree)
        {
            base.ToTreeAttributes(tree);
            tree.SetString(TrackedUsersTreeKey, JsonConvert.SerializeObject(TrackedUsers));
        }

        public override void FromTreeAttributes(ITreeAttribute tree, IWorldAccessor worldAccessor)
        {
            base.FromTreeAttributes(tree, worldAccessor);

            legacyOwnerUid = tree.GetString("OwnerUID");

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

            modSys.LoadedContainers.Add(this);
            MigrateLegacyOwnerIfNeeded();
            RefreshTrackingCache(forcePersist: true);
        }

        public override void OnBlockRemoved()
        {
            base.OnBlockRemoved();
            Unsubscribe();
        }

        public override void OnBlockUnloaded()
        {
            base.OnBlockUnloaded();
            Unsubscribe();
        }

        public void SeedResident(string playerUid)
        {
            if (string.IsNullOrWhiteSpace(playerUid))
            {
                return;
            }

            double now = GetUnixTimeSeconds();
            TrackedUserEntry entry = GetOrCreateEntry(playerUid);
            entry.State = TrackedUserState.Resident;
            entry.LastMeaningfulInteractionUnixSeconds = now;
            entry.LastResidentPresenceUnixSeconds = now;
            entry.LastProvisionalInteractionUnixSeconds = 0;

            MarkTrackingDirty();
            RefreshTrackingCache(forcePersist: true);
        }

        private void OnInventoryOpened(IPlayer player)
        {
            TrackedUserEntry? entry = FindEntry(player.PlayerUID);
            if (entry?.State == TrackedUserState.Resident)
            {
                entry.LastResidentPresenceUnixSeconds = GetUnixTimeSeconds();
                MarkTrackingDirty();
                Blockentity.MarkDirty();
            }
        }

        private void OnSlotModified(int slotId)
        {
            if (AttributeMeaningfulInteractionToOpenViewers()) return;
            modSys?.WasModifiedThisTick.Add(this);
        }

        private float OnAcquireTransitionSpeed(EnumTransitionType transType, ItemStack stack, float baseMul)
        {
            // TODO: this method is heavily cached as the game seems to execute it basically every tick?
            //   currently the cache is 30 seconds, but maybe its worth randomizing it a bit to avoid all chests expiring at the same time?
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

            double now = GetUnixTimeSeconds();
            foreach (string playerUid in openViewerUids)
            {
                ProcessMeaningfulInteraction(playerUid, now);
            }

            MarkTrackingDirty();
            RefreshTrackingCache(forcePersist: true);
            return true;
        }

        public void ProcessMeaningfulInteraction(IPlayer player)
        {
            double now = GetUnixTimeSeconds();
            ProcessMeaningfulInteraction(player.PlayerUID, now);
            
            MarkTrackingDirty();
            RefreshTrackingCache(forcePersist: true);
        }

        private void ProcessMeaningfulInteraction(string playerUid, double now)
        {
            TrackedUserEntry entry = GetOrCreateEntry(playerUid);

            if (entry.State == TrackedUserState.Resident)
            {
                entry.LastMeaningfulInteractionUnixSeconds = now;
                entry.LastResidentPresenceUnixSeconds = now;
                return;
            }

            double promotionWindowSeconds = GetConfig().PromotionWindowRealDays * SecondsPerDay;
            bool withinPromotionWindow = entry.LastProvisionalInteractionUnixSeconds > 0
                && now - entry.LastProvisionalInteractionUnixSeconds <= promotionWindowSeconds;

            if (withinPromotionWindow)
            {
                entry.State = TrackedUserState.Resident;
                entry.LastMeaningfulInteractionUnixSeconds = now;
                entry.LastResidentPresenceUnixSeconds = now;
                entry.LastProvisionalInteractionUnixSeconds = 0;
                return;
            }

            entry.State = TrackedUserState.Provisional;
            entry.LastMeaningfulInteractionUnixSeconds = now;
            entry.LastProvisionalInteractionUnixSeconds = now;
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
            double now = GetUnixTimeSeconds();
            if (!trackingDirty && now < cachedNextRefreshUnixSeconds)
            {
                return;
            }

            RefreshTrackingCache(forcePersist: false);
        }

        private void RefreshTrackingCache(bool forcePersist)
        {
            double now = GetUnixTimeSeconds();
            bool changed = forcePersist;
            bool hasOnlineTrackedUser = false;
            NoOfflineContainerFoodSpoilConfig config = GetConfig();
            TrackedUserEntry? pinnedResident = GetPinnedResident();
            List<TrackedUserEntry>? entriesToRemove = null;

            foreach (var entry in TrackedUsers)
            {
                RuntimeTrackedUserState state = EvaluateRuntimeState(entry, now, config);

                if (state.RefreshResidentPresence)
                {
                    entry.LastResidentPresenceUnixSeconds = now;
                    changed = true;
                }

                if (state.CountsForSpoilage)
                {
                    hasOnlineTrackedUser = true;
                }

                if (state.RemoveFromTracking && !ReferenceEquals(entry, pinnedResident))
                {
                    entriesToRemove ??= new List<TrackedUserEntry>();
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
            cachedNextRefreshUnixSeconds = now + TrackingCacheWindowSeconds;
            trackingDirty = false;

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

        private ContainerTrackingState EvaluateTrackedUsersForDebug()
        {
            double now = GetUnixTimeSeconds();
            NoOfflineContainerFoodSpoilConfig config = GetConfig();
            List<TrackedUserStatus> states = new List<TrackedUserStatus>(TrackedUsers.Count);
            bool hasValidTrackedUser = false;

            foreach (TrackedUserEntry entry in TrackedUsers)
            {
                TrackedUserStatus state = BuildDebugState(entry, now, config);
                states.Add(state);
                hasValidTrackedUser |= state.CountsForSpoilage;
            }

            return new ContainerTrackingState
            {
                UserStates = states,
                HasValidTrackedUser = hasValidTrackedUser
            };
        }

        private RuntimeTrackedUserState EvaluateRuntimeState(TrackedUserEntry entry, double now, NoOfflineContainerFoodSpoilConfig config)
        {
            if (entry.State == TrackedUserState.Provisional)
            {
                IPlayer? player = GetOnlinePlayer(entry.PlayerUid);
                bool online = player != null;
                double provisionalAgeSeconds = Math.Max(0, now - entry.LastProvisionalInteractionUnixSeconds);
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
                if (now - previousPresence >= PresencePersistIntervalSeconds)
                {
                    refreshResidentPresence = true;
                }

                referenceTime = now;
            }

            double residentAgeSeconds = Math.Max(0, now - referenceTime);
            bool withinTrackingWindow = residentAgeSeconds <= thresholdSeconds;

            return new RuntimeTrackedUserState(
                countsForSpoilage: residentOnline && withinTrackingWindow,
                removeFromTracking: !withinTrackingWindow,
                refreshResidentPresence: refreshResidentPresence
            );
        }

        private TrackedUserStatus BuildDebugState(TrackedUserEntry entry, double now, NoOfflineContainerFoodSpoilConfig config)
        {
            if (entry.State == TrackedUserState.Provisional)
            {
                return BuildProvisionalDebugState(entry, now, config);
            }

            return BuildResidentDebugState(entry, now, config);
        }

        private TrackedUserStatus BuildResidentDebugState(TrackedUserEntry entry, double now, NoOfflineContainerFoodSpoilConfig config)
        {
            IPlayer? player = GetOnlinePlayer(entry.PlayerUid);
            double? distance = GetDistanceToContainer(player);
            bool online = player != null;
            bool withinKeepaliveRadius = distance.HasValue && distance.Value <= config.ResidentKeepaliveRadiusBlocks;
            bool beyondFarAwayRadius = distance.HasValue && distance.Value > config.FarAwayRadiusBlocks;
            double referenceTime = GetEntryReferenceTime(entry);

            if (withinKeepaliveRadius)
            {
                referenceTime = now;
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

            double ageSeconds = Math.Max(0, now - referenceTime);
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
                    : (removeFromTracking ? $"{reason}-expired" : $"{reason}-not-counting")
            };
        }

        private TrackedUserStatus BuildProvisionalDebugState(TrackedUserEntry entry, double now, NoOfflineContainerFoodSpoilConfig config)
        {
            IPlayer? player = GetOnlinePlayer(entry.PlayerUid);
            double? distance = GetDistanceToContainer(player);
            bool online = player != null;
            double thresholdSeconds = config.ProvisionalExpiryRealHours * SecondsPerHour;
            double ageSeconds = Math.Max(0, now - entry.LastProvisionalInteractionUnixSeconds);
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

        private List<string> GetOpenViewerUids()
        {
            if (Blockentity is not BlockEntityContainer container)
            {
                return new List<string>();
            }

            return container.Inventory.openedByPlayerGUIds?
                .Where(uid => !string.IsNullOrWhiteSpace(uid))
                .Distinct()
                .OrderBy(uid => uid)
                .ToList() ?? new List<string>();
        }

        private IPlayer? GetOnlinePlayer(string playerUid)
        {
            ICoreServerAPI? api = modSys?.ServerApi;
            if (api == null || string.IsNullOrWhiteSpace(playerUid))
            {
                return null;
            }

            return api.World.PlayerByUid(playerUid) is IServerPlayer { Entity: not null, ConnectionState: EnumClientState.Playing } player ? player : null;
        }

        private double? GetDistanceToContainer(IPlayer? player)
        {
            if (player?.Entity?.Pos == null) return null;

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
                container.Inventory.SlotModified -=  OnSlotModified;
            }

            modSys?.LoadedContainers.Remove(this);
        }

        private static double GetEntryReferenceTime(TrackedUserEntry entry)
        {
            return Math.Max(entry.LastMeaningfulInteractionUnixSeconds, entry.LastResidentPresenceUnixSeconds);
        }

        private static double GetUnixTimeSeconds()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
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

    internal readonly struct RuntimeTrackedUserState(bool countsForSpoilage, bool removeFromTracking, bool refreshResidentPresence)
    {
        public bool CountsForSpoilage { get; } = countsForSpoilage;
        public bool RemoveFromTracking { get; } = removeFromTracking;
        public bool RefreshResidentPresence { get; } = refreshResidentPresence;
    }
}
