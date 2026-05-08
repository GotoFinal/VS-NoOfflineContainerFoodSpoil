using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace NoOfflineContainerFoodSpoil
{
    public sealed partial class BlockEntityBehaviorOfflinePreserve(BlockEntity blockEntity) : BlockEntityBehavior(blockEntity)
    {
        private const string TrackedUsersTreeKey = "TrackedUsersJson";
        private const string HasPendingUnloadCatchupTreeKey = "HasPendingUnloadCatchup";
        private const string LastUnloadCheckpointUnixSecondsTreeKey = "LastUnloadCheckpointUnixSeconds";
        private const string LastUnloadCheckpointWorldHoursTreeKey = "LastUnloadCheckpointWorldHours";
        private const double TrackingCacheWindowSeconds = 30;
        private const double PresencePersistIntervalSeconds = 300;
        private const double SecondsPerHour = 3600;
        private const double SecondsPerDay = 86400;
        private const int InitialPendingCatchupDelayMs = 1;
        private const int PendingCatchupRetryDelayMs = 50;
        private const int MaxPendingCatchupInitAttempts = 10;

        private float cachedPerishMultiplier = 1f;
        private bool cachedHasOnlineTrackedUser;
        private double cachedNextRefreshUnixSeconds;
        private bool trackingDirty = true;

        private NoOfflineContainerFoodSpoilModSystem? modSys;
        private string? legacyOwnerUid;
        private bool isReconcilingPendingUnloadCatchup;
        private bool suppressCustomTransitionSpeed;
        private bool suppressPendingUnloadCatchupWhileLoaded;
        private bool pendingUnloadCatchupReconcileScheduled;
        private int pendingUnloadCatchupInitAttempts;

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
            SchedulePendingUnloadCatchupReconcile(InitialPendingCatchupDelayMs, isInitRetry: true);
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
            ReconcilePendingUnloadCatchupNow();

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
            if (suppressCustomTransitionSpeed || transType != EnumTransitionType.Perish)
            {
                return 1f;
            }

            EnsureTrackingCacheCurrent();
            return cachedHasOnlineTrackedUser ? 1f : cachedPerishMultiplier;
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

        private void MarkTrackingDirty()
        {
            trackingDirty = true;
            cachedNextRefreshUnixSeconds = 0;
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

        private T RunWithoutCustomTransitionSpeed<T>(Func<T> action)
        {
            bool previousSuppressState = suppressCustomTransitionSpeed;
            suppressCustomTransitionSpeed = true;

            try
            {
                return action();
            }
            finally
            {
                suppressCustomTransitionSpeed = previousSuppressState;
            }
        }
    }
}
