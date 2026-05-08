using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.GameContent;

namespace NoOfflineContainerFoodSpoil
{
    public sealed partial class BlockEntityBehaviorOfflinePreserve
    {
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

            if (!IsReadyForPendingUnloadCatchup(container))
            {
                LogDebug("Pending unload catch-up is still waiting for inventory contents to finish loading.");
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

        private void ReconcilePendingUnloadCatchupNow()
        {
            if (modSys?.ServerApi == null || TrackedUsers.Count == 0 || !HasPendingUnloadCatchup)
            {
                return;
            }

            TryReconcilePendingUnloadCatchup(modSys.ServerApi.World);
        }

        private void SchedulePendingUnloadCatchupReconcile(int delayMs, bool isInitRetry)
        {
            if (pendingUnloadCatchupReconcileScheduled || modSys?.ServerApi == null || TrackedUsers.Count == 0 || !HasPendingUnloadCatchup)
            {
                return;
            }

            pendingUnloadCatchupReconcileScheduled = true;
            Blockentity.RegisterDelayedCallback(_ =>
            {
                pendingUnloadCatchupReconcileScheduled = false;

                if (!HasPendingUnloadCatchup)
                {
                    pendingUnloadCatchupInitAttempts = 0;
                    return;
                }

                TryReconcilePendingUnloadCatchup(modSys.ServerApi.World);
                if (HasPendingUnloadCatchup && isInitRetry && pendingUnloadCatchupInitAttempts < MaxPendingCatchupInitAttempts)
                {
                    pendingUnloadCatchupInitAttempts++;
                    LogDebug($"Retrying pending unload catch-up after delayed init attempt {pendingUnloadCatchupInitAttempts}/{MaxPendingCatchupInitAttempts}.");
                    SchedulePendingUnloadCatchupReconcile(PendingCatchupRetryDelayMs, isInitRetry: true);
                    return;
                }

                if (!HasPendingUnloadCatchup)
                {
                    pendingUnloadCatchupInitAttempts = 0;
                }
            }, delayMs);
        }

        private static bool IsReadyForPendingUnloadCatchup(BlockEntityContainer container)
        {
            foreach (ItemSlot slot in container.Inventory)
            {
                if (slot.Itemstack != null && slot.Itemstack.Collectible == null)
                {
                    return false;
                }
            }

            return true;
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
            CollectibleObject? collectible = stack.Collectible;
            if (collectible == null)
            {
                return;
            }

            TransitionableProperties[]? transitionProps = collectible.GetTransitionableProperties(world, stack, null);
            if (transitionProps == null || !TryGetPerishTransitionStateData(stack, transitionProps, out ITreeAttribute? attr, out float[]? transitionedHours, out int perishIndex))
            {
                return;
            }

            float previousTransitionedHours = transitionedHours![perishIndex];
            transitionedHours[perishIndex] += effectivePerishWorldHours;
            attr!.SetDouble("lastUpdatedTotalHours", world.Calendar.TotalHours);
            LogDebug($"Applied catch-up to slot. Item={collectible.Code}, StackSize={stack.StackSize}, TransitionedHoursBefore={previousTransitionedHours:0.###}, AddedHours={effectivePerishWorldHours:0.###}, TransitionedHoursAfter={transitionedHours[perishIndex]:0.###}, LastUpdatedWorldHours={world.Calendar.TotalHours:0.###}.");

            collectible.UpdateAndGetTransitionState(world, slot, EnumTransitionType.Perish);
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
            if (world == null || slot?.Itemstack?.Collectible == null)
            {
                return false;
            }

            TransitionableProperties[]? transitionProps = slot.Itemstack.Collectible.GetTransitionableProperties(world, slot.Itemstack, null);
            return transitionProps != null && transitionProps.Any(prop => prop?.Type == EnumTransitionType.Perish);
        }
    }
}
