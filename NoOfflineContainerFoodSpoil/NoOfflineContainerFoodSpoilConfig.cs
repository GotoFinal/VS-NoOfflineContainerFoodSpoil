using System;

namespace NoOfflineContainerFoodSpoil
{
    public class NoOfflineContainerFoodSpoilConfig
    {
        // Max number of players that are tracked for spoilage
        public int TrackedPlayerLimit = 5;
        // How quickly food spoils when a player is offline, 0 to just disable fully
        public float OfflineSpoilageMultiplier = 0.05f;
        // After how many real-time days of inactivity a player considered one of chest owners (resident) will be forgotten. (the last player is never removed)
        public double ResidentExpiryRealDays = 3;
        // If (owner of container) player is this close to the container, they will be counted as active user, stopping expiry timer
        public double ResidentKeepaliveRadiusBlocks = 100;
        // After how many real-time hours of inactivity a temporary owner/user of a chest will be forgotten - aka provisional user, someone who interacted with a chest just once so far.
        public double ProvisionalExpiryRealHours = 6;
        // Time window in which a player can be promoted to resident status if they interact (take/move/remove items, opening is ignored) with a storage again.
        public double PromotionWindowRealDays = 3;
        // If (owner of container) player is this far away from the container, they will be considered outside their house, starting eviction timer.
        public double FarAwayRadiusBlocks = 250;
        // After how many real-time hours of inactivity a player considered one of chest owners (resident) will be forgotten if they are outside their house. (the last player is never removed)
        public double FarAwayExpiryRealHours = 24;
        // How much recent player online history is kept to reconstruct spoilage catch-up for unloaded containers.
        public double SessionHistoryPruneRealDays = 30;

        public static NoOfflineContainerFoodSpoilConfig CreateDefault()
        {
            return new NoOfflineContainerFoodSpoilConfig();
        }

        public void ClampToSafeMinimums()
        {
            TrackedPlayerLimit = Math.Max(1, TrackedPlayerLimit);
            OfflineSpoilageMultiplier = Math.Max(0f, OfflineSpoilageMultiplier);
            ResidentExpiryRealDays = Math.Max(0.01, ResidentExpiryRealDays);
            ResidentKeepaliveRadiusBlocks = Math.Max(0, ResidentKeepaliveRadiusBlocks);
            ProvisionalExpiryRealHours = Math.Max(0.01, ProvisionalExpiryRealHours);
            PromotionWindowRealDays = Math.Max(0.01, PromotionWindowRealDays);
            FarAwayRadiusBlocks = Math.Max(ResidentKeepaliveRadiusBlocks, FarAwayRadiusBlocks);
            FarAwayExpiryRealHours = Math.Max(0.01, FarAwayExpiryRealHours);
            SessionHistoryPruneRealDays = Math.Max(0.01, SessionHistoryPruneRealDays);
        }
    }
}
