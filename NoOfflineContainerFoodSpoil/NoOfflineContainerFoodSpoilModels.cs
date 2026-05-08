using System.Collections.Generic;

namespace NoOfflineContainerFoodSpoil
{
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

    internal sealed class TrackedUserAnalysis
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
        public bool RefreshResidentPresence;
        public string Reason = string.Empty;

        public TrackedUserStatus ToStatus()
        {
            return new TrackedUserStatus
            {
                Entry = Entry,
                Online = Online,
                DistanceBlocks = DistanceBlocks,
                WithinKeepaliveRadius = WithinKeepaliveRadius,
                BeyondFarAwayRadius = BeyondFarAwayRadius,
                AgeSeconds = AgeSeconds,
                ThresholdSeconds = ThresholdSeconds,
                CountsForSpoilage = CountsForSpoilage,
                RemoveFromTracking = RemoveFromTracking,
                Reason = Reason
            };
        }
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
}
