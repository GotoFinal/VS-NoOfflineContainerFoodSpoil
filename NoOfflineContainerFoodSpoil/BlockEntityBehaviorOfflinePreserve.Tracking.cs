using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;

namespace NoOfflineContainerFoodSpoil
{
    public sealed partial class BlockEntityBehaviorOfflinePreserve
    {
        private void RefreshTrackingCache(bool forcePersist)
        {
            double nowUnixSeconds = NoOfflineContainerFoodSpoilModSystem.GetUnixTimeSeconds();
            bool changed = forcePersist;
            bool hasOnlineTrackedUser = false;
            NoOfflineContainerFoodSpoilConfig config = GetConfig();
            TrackedUserEntry? pinnedResident = GetPinnedResident();
            List<TrackedUserEntry>? entriesToRemove = null;
            int trackedUsersBeforeRefresh = TrackedUsers.Count;

            foreach (TrackedUserAnalysis analysis in TrackedUsers.Select(entry => AnalyzeTrackedUser(entry, nowUnixSeconds, config)))
            {
                if (analysis.RefreshResidentPresence)
                {
                    analysis.Entry.LastResidentPresenceUnixSeconds = nowUnixSeconds;
                    changed = true;
                }

                if (analysis.CountsForSpoilage)
                {
                    hasOnlineTrackedUser = true;
                }

                if (analysis.RemoveFromTracking && !ReferenceEquals(analysis.Entry, pinnedResident))
                {
                    entriesToRemove ??= [];
                    entriesToRemove.Add(analysis.Entry);
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
                    : string.Join(", ", entriesToRemove.Select(DescribeEntry));
                LogDebug($"Tracking cache refreshed. ForcePersist={forcePersist}, TrackedUsersBefore={trackedUsersBeforeRefresh}, TrackedUsersAfter={TrackedUsers.Count}, HasOnlineTrackedUser={cachedHasOnlineTrackedUser}, CachedPerishMultiplier={cachedPerishMultiplier:0.###}, RemovedUsers={removedUsers}, PinnedResident={(pinnedResident == null ? "(none)" : DescribeEntry(pinnedResident))}.");
            }

            if (changed)
            {
                Blockentity.MarkDirty();
            }
        }

        private TrackedUserEntry? GetPinnedResident()
        {
            return TrackedUsers
                .Where(entry => entry.State == TrackedUserState.Resident)
                .OrderByDescending(GetEntryReferenceTime)
                .FirstOrDefault();
        }

        private ContainerTrackingState EvaluateTrackedUsersForDebug()
        {
            double nowUnixSeconds = NoOfflineContainerFoodSpoilModSystem.GetUnixTimeSeconds();
            NoOfflineContainerFoodSpoilConfig config = GetConfig();
            List<TrackedUserStatus> states = new List<TrackedUserStatus>(TrackedUsers.Count);
            bool hasValidTrackedUser = false;

            foreach (TrackedUserAnalysis analysis in TrackedUsers.Select(entry => AnalyzeTrackedUser(entry, nowUnixSeconds, config)))
            {
                states.Add(analysis.ToStatus());
                hasValidTrackedUser |= analysis.CountsForSpoilage;
            }

            return new ContainerTrackingState
            {
                UserStates = states,
                HasValidTrackedUser = hasValidTrackedUser
            };
        }

        private TrackedUserAnalysis AnalyzeTrackedUser(TrackedUserEntry entry, double nowUnixSeconds, NoOfflineContainerFoodSpoilConfig config)
        {
            return entry.State == TrackedUserState.Provisional
                ? AnalyzeProvisionalState(entry, nowUnixSeconds, config)
                : AnalyzeResidentState(entry, nowUnixSeconds, config);
        }

        private TrackedUserAnalysis AnalyzeResidentState(TrackedUserEntry entry, double nowUnixSeconds, NoOfflineContainerFoodSpoilConfig config)
        {
            IPlayer? player = GetOnlinePlayer(entry.PlayerUid);
            double? distance = GetDistanceToContainer(player);
            bool online = player != null;
            bool withinKeepaliveRadius = distance.HasValue && distance.Value <= config.ResidentKeepaliveRadiusBlocks;
            bool beyondFarAwayRadius = distance.HasValue && distance.Value > config.FarAwayRadiusBlocks;
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

            double referenceTime = withinKeepaliveRadius
                ? nowUnixSeconds
                : GetEntryReferenceTime(entry);
            double ageSeconds = Math.Max(0, nowUnixSeconds - referenceTime);
            bool withinTrackingWindow = ageSeconds <= thresholdSeconds;
            bool refreshResidentPresence = withinKeepaliveRadius
                && nowUnixSeconds - entry.LastResidentPresenceUnixSeconds >= PresencePersistIntervalSeconds;
            bool countsForSpoilage = online && withinTrackingWindow;
            bool removeFromTracking = !withinTrackingWindow;

            return new TrackedUserAnalysis
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
                RefreshResidentPresence = refreshResidentPresence,
                Reason = countsForSpoilage
                    ? reason
                    : removeFromTracking ? $"{reason}-expired" : $"{reason}-not-counting"
            };
        }

        private TrackedUserAnalysis AnalyzeProvisionalState(TrackedUserEntry entry, double nowUnixSeconds, NoOfflineContainerFoodSpoilConfig config)
        {
            IPlayer? player = GetOnlinePlayer(entry.PlayerUid);
            double? distance = GetDistanceToContainer(player);
            bool online = player != null;
            double thresholdSeconds = config.ProvisionalExpiryRealHours * SecondsPerHour;
            double ageSeconds = Math.Max(0, nowUnixSeconds - entry.LastProvisionalInteractionUnixSeconds);
            bool withinTimeWindow = ageSeconds <= thresholdSeconds;

            return new TrackedUserAnalysis
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
                RefreshResidentPresence = false,
                Reason = online
                    ? withinTimeWindow ? "provisional-online-grace" : "provisional-expired"
                    : withinTimeWindow ? "provisional-offline-not-counting" : "provisional-expired"
            };
        }
    }
}
