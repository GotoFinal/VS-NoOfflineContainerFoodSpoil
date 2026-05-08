using HarmonyLib;
using Vintagestory.API.Common;

namespace NoOfflineContainerFoodSpoil
{
    [HarmonyPatch]
    public static class NoOfflineContainerFoodSpoilHarmonyPatches
    {
        [HarmonyPatch(typeof(CollectibleObject), nameof(CollectibleObject.UpdateAndGetTransitionState))]
        [HarmonyPrefix]
        public static void BeforeUpdateAndGetTransitionState(IWorldAccessor world, ItemSlot inslot, EnumTransitionType type)
        {
            if (type != EnumTransitionType.Perish || inslot == null || world.Api == null)
            {
                return;
            }

            NoOfflineContainerFoodSpoilModSystem? modSystem = world.Api.ModLoader.GetModSystem<NoOfflineContainerFoodSpoilModSystem>();
            modSystem?.TryReconcilePendingUnloadCatchup(world, inslot);
        }

        [HarmonyPatch(typeof(CollectibleObject), "UpdateAndGetTransitionStatesNative")]
        [HarmonyPrefix]
        public static void BeforeUpdateAndGetTransitionStatesNative(IWorldAccessor world, ItemSlot inslot)
        {
            if (inslot == null || world.Api == null)
            {
                return;
            }

            NoOfflineContainerFoodSpoilModSystem? modSystem = world.Api.ModLoader.GetModSystem<NoOfflineContainerFoodSpoilModSystem>();
            modSystem?.TryReconcilePendingUnloadCatchup(world, inslot);
        }
    }
}
