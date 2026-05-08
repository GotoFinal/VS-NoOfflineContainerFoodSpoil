using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace NoOfflineContainerFoodSpoil
{
    // note: all of this command is AI slop, for debug purposed only.
    public static class NoOfflineContainerFoodSpoilCommands
    {
        private static readonly System.Collections.Generic.Dictionary<string, BlockPos> LastDebugPositionsByPlayerUid = new();

        public static void Register(NoOfflineContainerFoodSpoilModSystem modSystem, ICoreServerAPI api)
        {
            api.ChatCommands
                .Create("spoildebug")
                .WithDescription("No Offline Container Food Spoil commands.")
                .RequiresPrivilege(Privilege.buildblockseverywhere)
                .RequiresPlayer()
                .HandleWith(_ =>
                    TextCommandResult.Success(
                        "Use /spoildebug debug, /spoildebug last, /spoildebug at [x] [y] [z], /spoildebug add [player], /spoildebug remove [player], or /spoildebug config [setting] [value]."))
                .BeginSubCommand("add")
                .WithDescription("Add a resident to the looked-at container by uid or last known name.")
                .RequiresPlayer()
                .WithArgs(api.ChatCommands.Parsers.All("player"))
                .HandleWith(args =>
                    HandleForLookedAtContainer(modSystem, args, behavior => HandleMembershipCommand(modSystem, behavior, args[0] as string, addResident: true)))
                .EndSubCommand()
                .BeginSubCommand("remove")
                .WithDescription("Remove a tracked player from the looked-at container by uid or last known name.")
                .RequiresPlayer()
                .WithArgs(api.ChatCommands.Parsers.All("player"))
                .HandleWith(args =>
                    HandleForLookedAtContainer(modSystem, args,
                        behavior => HandleMembershipCommand(modSystem, behavior, args[0] as string, addResident: false)))
                .EndSubCommand()
                .BeginSubCommand("config")
                .WithDescription("Show or update config values.")
                .RequiresPlayer()
                .WithArgs(api.ChatCommands.Parsers.OptionalAll("args"))
                .HandleWith(args => HandleConfigCommand(modSystem, args))
                .EndSubCommand()
                .BeginSubCommand("debug")
                .WithDescription("Show debug information for the looked-at container.")
                .RequiresPlayer()
                .IgnoreAdditionalArgs()
                .HandleWith(args => HandleDebugForTarget(modSystem, args, ContainerTargetKind.LookedAt))
                .EndSubCommand()
                .BeginSubCommand("last")
                .WithDescription("Show debug information for the last inspected container.")
                .RequiresPlayer()
                .IgnoreAdditionalArgs()
                .HandleWith(args => HandleDebugForTarget(modSystem, args, ContainerTargetKind.Last))
                .EndSubCommand()
                .BeginSubCommand("at")
                .WithDescription("Show debug information for the container at a specific block position.")
                .RequiresPlayer()
                .WithArgs(api.ChatCommands.Parsers.WorldPosition("position"))
                .HandleWith(args => HandleDebugForTarget(modSystem, args, ContainerTargetKind.AtPosition))
                .EndSubCommand();
        }

        private enum ContainerTargetKind
        {
            LookedAt,
            Last,
            AtPosition
        }

        private static TextCommandResult HandleDebugForTarget(NoOfflineContainerFoodSpoilModSystem modSystem, TextCommandCallingArgs args, ContainerTargetKind targetKind)
        {
            return HandleForContainer(modSystem, args, targetKind, rememberAsLast: true, behavior => TextCommandResult.Success(BuildDebugDump(modSystem, behavior)));
        }

        private static TextCommandResult HandleForLookedAtContainer(NoOfflineContainerFoodSpoilModSystem modSystem, TextCommandCallingArgs args, System.Func<BlockEntityBehaviorOfflinePreserve, TextCommandResult> handler)
        {
            return HandleForContainer(modSystem, args, ContainerTargetKind.LookedAt, rememberAsLast: false, handler);
        }

        private static TextCommandResult HandleForContainer(NoOfflineContainerFoodSpoilModSystem modSystem, TextCommandCallingArgs args, ContainerTargetKind targetKind, bool rememberAsLast, System.Func<BlockEntityBehaviorOfflinePreserve, TextCommandResult> handler)
        {
            if (!TryGetContainerBehavior(modSystem, args, targetKind, out IServerPlayer? player, out BlockPos? position, out BlockEntityBehaviorOfflinePreserve? behavior, out string error))
            {
                return TextCommandResult.Error(error);
            }

            if (rememberAsLast)
            {
                RememberLastPosition(player!.PlayerUID, position!);
            }

            return handler(behavior!);
        }

        private static bool TryGetContainerBehavior(NoOfflineContainerFoodSpoilModSystem modSystem, TextCommandCallingArgs args, ContainerTargetKind targetKind, out IServerPlayer? player, out BlockPos? position, out BlockEntityBehaviorOfflinePreserve? behavior, out string error)
        {
            player = args.Caller.Player as IServerPlayer;
            position = null;
            behavior = null;
            error = string.Empty;

            if (player == null)
            {
                error = "This command requires a player caller.";
                return false;
            }

            switch (targetKind)
            {
                case ContainerTargetKind.LookedAt:
                    position = player.CurrentBlockSelection?.Position;
                    if (position == null)
                    {
                        error = "Look at a tracked container first.";
                        return false;
                    }
                    break;

                case ContainerTargetKind.Last:
                    if (!LastDebugPositionsByPlayerUid.TryGetValue(player.PlayerUID, out position))
                    {
                        error = "No previous container is stored for you yet.";
                        return false;
                    }
                    break;

                case ContainerTargetKind.AtPosition:
                    Vec3d? worldPosition = args[0] as Vec3d;
                    if (worldPosition == null)
                    {
                        error = "Provide a valid block position.";
                        return false;
                    }

                    position = new BlockPos((int)worldPosition.X, (int)worldPosition.Y, (int)worldPosition.Z);
                    break;
            }

            BlockEntity? blockEntity = modSystem.ServerApi?.World.BlockAccessor.GetBlockEntity(position);
            behavior = blockEntity?.GetBehavior<BlockEntityBehaviorOfflinePreserve>();
            if (behavior == null)
            {
                error = targetKind == ContainerTargetKind.LookedAt
                    ? "The looked-at block is not a tracked container."
                    : $"The block at {position} is not a tracked container.";
                return false;
            }

            return true;
        }

        private static TextCommandResult HandleMembershipCommand(NoOfflineContainerFoodSpoilModSystem modSystem, BlockEntityBehaviorOfflinePreserve behavior, string? playerSelector, bool addResident)
        {
            if (!TryResolvePlayerIdentity(modSystem, playerSelector, out string targetUid, out string targetName, out string error))
            {
                return TextCommandResult.Error(error);
            }

            if (addResident)
            {
                behavior.SeedResident(targetUid);
            }
            else if (!behavior.TryRemoveTrackedUserForCommand(targetUid, out error))
            {
                return TextCommandResult.Error(error);
            }

            string action = addResident ? "Added" : "Removed";
            string relation = addResident ? "as a resident on" : "from";
            return TextCommandResult.Success($"{action} {FormatPlayerIdentity(targetUid, targetName)} {relation} container at {behavior.Blockentity.Pos}.");
        }

        private static TextCommandResult HandleConfigCommand(NoOfflineContainerFoodSpoilModSystem modSystem, TextCommandCallingArgs args)
        {
            string rawArgs = (args[0] as string ?? string.Empty).Trim();
            if (rawArgs.Length == 0)
            {
                return TextCommandResult.Success(BuildConfigDump(modSystem.Config));
            }

            CmdArgs parsedArgs = new CmdArgs(rawArgs);
            string setting = parsedArgs.PopWord() ?? string.Empty;
            if (setting.Length == 0)
            {
                return TextCommandResult.Success(BuildConfigDump(modSystem.Config));
            }

            string? value = parsedArgs.PopWord();
            if (string.IsNullOrWhiteSpace(value))
            {
                return TryGetConfigValue(modSystem.Config, setting, out string configValue, out string lookupError)
                    ? TextCommandResult.Success($"{configValue}")
                    : TextCommandResult.Error($"{lookupError} Supported settings: {GetSupportedSettingsSummary()}");
            }

            if (!TryApplyConfigValue(modSystem.Config, setting, value, out string canonicalName, out string formattedValue, out string error))
            {
                return TextCommandResult.Error($"{error} Supported settings: {GetSupportedSettingsSummary()}");
            }

            modSystem.SaveConfigAndRefreshLoadedContainers();
            return TextCommandResult.Success($"Updated {canonicalName}={formattedValue}. Applied to loaded containers.");
        }

        private static bool TryResolvePlayerIdentity(NoOfflineContainerFoodSpoilModSystem modSystem, string? selector, out string playerUid, out string playerName, out string error)
        {
            playerUid = string.Empty;
            playerName = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(selector))
            {
                error = "Provide a player uid or last known name.";
                return false;
            }

            ICoreServerAPI? api = modSystem.ServerApi;
            if (api == null)
            {
                error = "Server API is not available.";
                return false;
            }

            string trimmedSelector = selector.Trim();
            IPlayer? onlinePlayer = api.World.AllOnlinePlayers.FirstOrDefault(player =>
                string.Equals(player.PlayerUID, trimmedSelector, StringComparison.OrdinalIgnoreCase)
                || string.Equals(player.PlayerName, trimmedSelector, StringComparison.OrdinalIgnoreCase));

            if (onlinePlayer != null)
            {
                playerUid = onlinePlayer.PlayerUID;
                playerName = onlinePlayer.PlayerName;
                return true;
            }

            IServerPlayerData? byUid = api.PlayerData.GetPlayerDataByUid(trimmedSelector);
            if (byUid != null)
            {
                playerUid = byUid.PlayerUID;
                playerName = byUid.LastKnownPlayername ?? string.Empty;
                return true;
            }

            IServerPlayerData? byName = api.PlayerData.GetPlayerDataByLastKnownName(trimmedSelector);
            if (byName != null)
            {
                playerUid = byName.PlayerUID;
                playerName = byName.LastKnownPlayername ?? string.Empty;
                return true;
            }

            error = $"Could not resolve player '{trimmedSelector}'.";
            return false;
        }

        private static string ResolvePlayerDisplayName(NoOfflineContainerFoodSpoilModSystem modSystem, string playerUid)
        {
            ICoreServerAPI? api = modSystem.ServerApi;
            if (api == null)
            {
                return string.Empty;
            }

            IPlayer? onlinePlayer = api.World.PlayerByUid(playerUid);
            if (!string.IsNullOrWhiteSpace(onlinePlayer?.PlayerName))
            {
                return onlinePlayer.PlayerName;
            }

            IServerPlayerData? playerData = api.PlayerData.GetPlayerDataByUid(playerUid);
            return playerData?.LastKnownPlayername ?? string.Empty;
        }

        private static string FormatPlayerIdentity(string playerUid, string playerName)
        {
            return string.IsNullOrWhiteSpace(playerName) ? playerUid : $"{playerName} ({playerUid})";
        }

        private static void RememberLastPosition(string playerUid, BlockPos position)
        {
            LastDebugPositionsByPlayerUid[playerUid] = new BlockPos(position.X, position.Y, position.Z);
        }

        private static string BuildConfigDump(NoOfflineContainerFoodSpoilConfig config)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("Config:");
            builder.AppendLine($"  TrackedPlayerLimit={FormatInt(config.TrackedPlayerLimit)}");
            builder.AppendLine($"  OfflineSpoilageMultiplier={FormatFloat(config.OfflineSpoilageMultiplier)}");
            builder.AppendLine($"  ResidentExpiryRealDays={FormatDouble(config.ResidentExpiryRealDays)}");
            builder.AppendLine($"  ResidentKeepaliveRadiusBlocks={FormatDouble(config.ResidentKeepaliveRadiusBlocks)}");
            builder.AppendLine($"  ProvisionalExpiryRealHours={FormatDouble(config.ProvisionalExpiryRealHours)}");
            builder.AppendLine($"  PromotionWindowRealDays={FormatDouble(config.PromotionWindowRealDays)}");
            builder.AppendLine($"  FarAwayRadiusBlocks={FormatDouble(config.FarAwayRadiusBlocks)}");
            builder.AppendLine($"  FarAwayExpiryRealHours={FormatDouble(config.FarAwayExpiryRealHours)}");
            builder.AppendLine($"  SessionHistoryPruneRealDays={FormatDouble(config.SessionHistoryPruneRealDays)}");
            return builder.ToString().TrimEnd();
        }

        private static bool TryGetConfigValue(NoOfflineContainerFoodSpoilConfig config, string setting, out string value, out string error)
        {
            value = string.Empty;
            error = string.Empty;

            switch (NormalizeSettingName(setting))
            {
                case "trackedplayerlimit":
                    value = $"TrackedPlayerLimit={FormatInt(config.TrackedPlayerLimit)}";
                    return true;
                case "offlinespoilagemultiplier":
                    value = $"OfflineSpoilageMultiplier={FormatFloat(config.OfflineSpoilageMultiplier)}";
                    return true;
                case "residentexpiryrealdays":
                    value = $"ResidentExpiryRealDays={FormatDouble(config.ResidentExpiryRealDays)}";
                    return true;
                case "residentkeepaliveradiusblocks":
                    value = $"ResidentKeepaliveRadiusBlocks={FormatDouble(config.ResidentKeepaliveRadiusBlocks)}";
                    return true;
                case "provisionalexpiryrealhours":
                    value = $"ProvisionalExpiryRealHours={FormatDouble(config.ProvisionalExpiryRealHours)}";
                    return true;
                case "promotionwindowrealdays":
                    value = $"PromotionWindowRealDays={FormatDouble(config.PromotionWindowRealDays)}";
                    return true;
                case "farawayradiusblocks":
                    value = $"FarAwayRadiusBlocks={FormatDouble(config.FarAwayRadiusBlocks)}";
                    return true;
                case "farawayexpiryrealhours":
                    value = $"FarAwayExpiryRealHours={FormatDouble(config.FarAwayExpiryRealHours)}";
                    return true;
                case "sessionhistoryprunerealdays":
                    value = $"SessionHistoryPruneRealDays={FormatDouble(config.SessionHistoryPruneRealDays)}";
                    return true;
                default:
                    error = $"Unknown setting '{setting}'.";
                    return false;
            }
        }

        private static bool TryApplyConfigValue(NoOfflineContainerFoodSpoilConfig config, string setting, string rawValue, out string canonicalName, out string formattedValue, out string error)
        {
            canonicalName = string.Empty;
            formattedValue = string.Empty;
            error = string.Empty;

            switch (NormalizeSettingName(setting))
            {
                case "trackedplayerlimit":
                    if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int trackedPlayerLimit))
                    {
                        error = $"Invalid integer value for {setting}.";
                        return false;
                    }

                    config.TrackedPlayerLimit = trackedPlayerLimit;
                    config.ClampToSafeMinimums();
                    canonicalName = nameof(config.TrackedPlayerLimit);
                    formattedValue = FormatInt(config.TrackedPlayerLimit);
                    return true;

                case "offlinespoilagemultiplier":
                    if (!float.TryParse(rawValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out float spoilageMultiplier))
                    {
                        error = $"Invalid number value for {setting}.";
                        return false;
                    }

                    config.OfflineSpoilageMultiplier = spoilageMultiplier;
                    config.ClampToSafeMinimums();
                    canonicalName = nameof(config.OfflineSpoilageMultiplier);
                    formattedValue = FormatFloat(config.OfflineSpoilageMultiplier);
                    return true;

                case "residentexpiryrealdays":
                    return TrySetDouble(rawValue, setting, nameof(config.ResidentExpiryRealDays), value => config.ResidentExpiryRealDays = value, () => config.ResidentExpiryRealDays, config, out canonicalName, out formattedValue, out error);
                case "residentkeepaliveradiusblocks":
                    return TrySetDouble(rawValue, setting, nameof(config.ResidentKeepaliveRadiusBlocks), value => config.ResidentKeepaliveRadiusBlocks = value, () => config.ResidentKeepaliveRadiusBlocks, config, out canonicalName, out formattedValue, out error);
                case "provisionalexpiryrealhours":
                    return TrySetDouble(rawValue, setting, nameof(config.ProvisionalExpiryRealHours), value => config.ProvisionalExpiryRealHours = value, () => config.ProvisionalExpiryRealHours, config, out canonicalName, out formattedValue, out error);
                case "promotionwindowrealdays":
                    return TrySetDouble(rawValue, setting, nameof(config.PromotionWindowRealDays), value => config.PromotionWindowRealDays = value, () => config.PromotionWindowRealDays, config, out canonicalName, out formattedValue, out error);
                case "farawayradiusblocks":
                    return TrySetDouble(rawValue, setting, nameof(config.FarAwayRadiusBlocks), value => config.FarAwayRadiusBlocks = value, () => config.FarAwayRadiusBlocks, config, out canonicalName, out formattedValue, out error);
                case "farawayexpiryrealhours":
                    return TrySetDouble(rawValue, setting, nameof(config.FarAwayExpiryRealHours), value => config.FarAwayExpiryRealHours = value, () => config.FarAwayExpiryRealHours, config, out canonicalName, out formattedValue, out error);
                case "sessionhistoryprunerealdays":
                    return TrySetDouble(rawValue, setting, nameof(config.SessionHistoryPruneRealDays), value => config.SessionHistoryPruneRealDays = value, () => config.SessionHistoryPruneRealDays, config, out canonicalName, out formattedValue, out error);
                default:
                    error = $"Unknown setting '{setting}'.";
                    return false;
            }
        }

        private static bool TrySetDouble(string rawValue, string originalSettingName, string canonicalSettingName, System.Action<double> setter, System.Func<double> getter, NoOfflineContainerFoodSpoilConfig config, out string canonicalName, out string formattedValue, out string error)
        {
            canonicalName = string.Empty;
            formattedValue = string.Empty;
            error = string.Empty;

            if (!double.TryParse(rawValue, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double value))
            {
                error = $"Invalid number value for {originalSettingName}.";
                return false;
            }

            setter(value);
            config.ClampToSafeMinimums();
            canonicalName = canonicalSettingName;
            formattedValue = FormatDouble(getter());
            return true;
        }

        private static string NormalizeSettingName(string setting)
        {
            StringBuilder builder = new StringBuilder(setting.Length);

            foreach (char character in setting)
            {
                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(char.ToLowerInvariant(character));
                }
            }

            return builder.ToString();
        }

        private static string GetSupportedSettingsSummary()
        {
            return "TrackedPlayerLimit, OfflineSpoilageMultiplier, ResidentExpiryRealDays, ResidentKeepaliveRadiusBlocks, ProvisionalExpiryRealHours, PromotionWindowRealDays, FarAwayRadiusBlocks, FarAwayExpiryRealHours, SessionHistoryPruneRealDays";
        }

        private static string FormatInt(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatFloat(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string FormatDouble(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string FormatUnixSeconds(double unixSeconds)
        {
            if (unixSeconds <= 0)
            {
                return "n/a";
            }

            long roundedSeconds = Convert.ToInt64(Math.Round(unixSeconds, MidpointRounding.AwayFromZero));
            DateTimeOffset timestamp = DateTimeOffset.FromUnixTimeSeconds(roundedSeconds);
            return $"{timestamp:yyyy-MM-dd HH:mm:ss zzz} ({roundedSeconds})";
        }

        private static string BuildDebugDump(NoOfflineContainerFoodSpoilModSystem modSystem, BlockEntityBehaviorOfflinePreserve behavior)
        {
            ContainerTrackingState snapshot = behavior.EvaluateStateForCommand();
            NoOfflineContainerFoodSpoilConfig config = modSystem.Config;
            StringBuilder builder = new StringBuilder();
            var openViewerUids = behavior.GetOpenViewerUidsForCommand();
            string primaryReason = snapshot.UserStates.FirstOrDefault(state => state.CountsForSpoilage)?.Reason
                ?? "no valid tracked users";

            builder.AppendLine($"Container: {behavior.Blockentity.Block.Code} @ {behavior.Blockentity.Pos}");
            builder.AppendLine($"Spoilage mode: {(snapshot.HasValidTrackedUser ? "normal" : "slowed")}");
            builder.AppendLine($"Applied perish multiplier: {(snapshot.HasValidTrackedUser ? 1f : config.OfflineSpoilageMultiplier):0.###}");
            builder.AppendLine($"Reason: {primaryReason}");
            builder.AppendLine($"PendingUnloadCatchup={behavior.HasPendingUnloadCatchupForCommand}");
            builder.AppendLine($"LastUnloadCheckpoint={FormatUnixSeconds(behavior.LastUnloadCheckpointUnixSecondsForCommand)}");
            builder.AppendLine($"LastUnloadCheckpointWorldHours={FormatDouble(behavior.LastUnloadCheckpointWorldHoursForCommand)}");
            builder.AppendLine($"Open viewers: {(openViewerUids.Count == 0 ? "(none)" : string.Join(", ", openViewerUids.OrderBy(uid => uid).Select(uid => FormatPlayerIdentity(uid, ResolvePlayerDisplayName(modSystem, uid)))))}");
            builder.AppendLine(BuildConfigDump(config));
            builder.AppendLine("Tracked users:");

            if (snapshot.UserStates.Count == 0)
            {
                builder.AppendLine("  (none)");
                return builder.ToString().TrimEnd();
            }

            foreach (TrackedUserStatus state in snapshot.UserStates.OrderByDescending(state => state.Entry.State == TrackedUserState.Resident))
            {
                string playerName = ResolvePlayerDisplayName(modSystem, state.Entry.PlayerUid);
                builder.AppendLine($"  {FormatPlayerIdentity(state.Entry.PlayerUid, playerName)}");
                builder.AppendLine($"    State={state.Entry.State}");
                builder.AppendLine($"    Online={state.Online}");
                builder.AppendLine($"    CountsForSpoilage={state.CountsForSpoilage}");
                builder.AppendLine($"    RemoveFromTracking={state.RemoveFromTracking}");
                builder.AppendLine($"    DistanceBlocks={(state.DistanceBlocks.HasValue ? state.DistanceBlocks.Value.ToString("0.0", CultureInfo.InvariantCulture) : "n/a")}");
                builder.AppendLine($"    WithinKeepaliveRadius={state.WithinKeepaliveRadius}");
                builder.AppendLine($"    BeyondFarAwayRadius={state.BeyondFarAwayRadius}");
                builder.AppendLine($"    LastMeaningfulInteraction={FormatUnixSeconds(state.Entry.LastMeaningfulInteractionUnixSeconds)}");
                builder.AppendLine($"    LastResidentPresence={FormatUnixSeconds(state.Entry.LastResidentPresenceUnixSeconds)}");
                builder.AppendLine($"    LastProvisionalInteraction={FormatUnixSeconds(state.Entry.LastProvisionalInteractionUnixSeconds)}");
                builder.AppendLine($"    AgeSeconds={state.AgeSeconds:0}");
                builder.AppendLine($"    ThresholdSeconds={state.ThresholdSeconds:0}");
                builder.AppendLine($"    Reason={state.Reason}");
            }

            return builder.ToString().TrimEnd();
        }
    }
}
