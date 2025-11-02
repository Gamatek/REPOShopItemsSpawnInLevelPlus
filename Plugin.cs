using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using Photon.Pun;

namespace REPOShopItemsSpawnInLevelPlus;

public class UsedVolumeTracker : MonoBehaviour { }
public class SpawnedItemTracker : MonoBehaviour { }

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInProcess("REPO.exe")]
public class Plugin : BaseUnityPlugin
{
    internal static new ManualLogSource Logger;

    internal static ConfigEntry<bool> SpawnUpgradeItems;
    internal static ConfigEntry<bool> MapHideUpgradeItems;
    internal static ConfigEntry<float> UpgradeItemSpawnChance;
    internal static ConfigEntry<bool> UseShopPriceForUpgradeItems;

    internal static ConfigEntry<bool> SpawnDroneItems;
    internal static ConfigEntry<bool> MapHideDroneItems;
    internal static ConfigEntry<float> DroneItemSpawnChance;
    internal static ConfigEntry<bool> UseShopPriceForDroneItems;

    internal static List<ConfigEntry<bool>> DisallowedItems;

    private Harmony harmony;

    // Static instance for script access
    public static Plugin Instance { get; private set; }

    private void Awake()
    {
        // Set instance for script access
        Instance = this;

        // Plugin startup logic
        Logger = base.Logger;
        Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");

        // Initialize and apply Harmony patches
        harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        harmony.PatchAll(typeof(Plugin));

        Logger.LogInfo("Harmony patches applied!");

        SpawnUpgradeItems = Config.Bind("UpgradeItems", "SpawnUpgradeItems", true, new ConfigDescription("Whether upgrade items can spawn in levels"));
        MapHideUpgradeItems = Config.Bind("UpgradeItems", "MapHideShopUpgradeItems", true, new ConfigDescription("(Client) Whether upgrade items are hidden on the map"));
        UpgradeItemSpawnChance = Config.Bind("UpgradeItems", "UpgradeItemSpawnChance", 2.5f, new ConfigDescription("% chance for an upgrade item to spawn", new AcceptableValueRange<float>(0.0f, 100.0f)));
        UseShopPriceForUpgradeItems = Config.Bind("UpgradeItems", "UseShopPriceForItemSelection", true, new ConfigDescription("If ON: Cheaper upgrade items appear more often. If OFF: All upgrade items have equal chance."));

        SpawnDroneItems = Config.Bind("DroneItems", "SpawnDroneItems", true, new ConfigDescription("Whether drone items can spawn in levels"));
        MapHideDroneItems = Config.Bind("DroneItems", "MapHideDroneItems", true, new ConfigDescription("(Client) Whether drone items are hidden on the map"));
        DroneItemSpawnChance = Config.Bind("DroneItems", "DroneItemsSpawnChance", 0.95f, new ConfigDescription("% chance for a drone item to spawn", new AcceptableValueRange<float>(0.0f, 100.0f)));
        UseShopPriceForDroneItems = Config.Bind("DroneItems", "UseShopPriceForItemSelection", true, new ConfigDescription("If ON: Cheaper drone items appear more often. If OFF: All drone items have equal chance."));
    }

    // [HarmonyPatch(typeof(StatsManager), "LoadItemsFromFolder")]
    // Patched at MainMenuOpen.Awake to ensure modded items are loaded
    [HarmonyPatch(typeof(MainMenuOpen), "Awake")]
    [HarmonyPostfix]
    public static void MainMenuOpen_Awake_Postfix(StatsManager __instance)
    {
        if (DisallowedItems != null) return;
        
        Logger.LogInfo("Initializing disallowed items list");
        DisallowedItems = new List<ConfigEntry<bool>>();
        
        foreach (var item in StatsManager.instance.itemDictionary.Values)
        {
            ConfigEntry<bool> configEntry;
            switch (item.itemType)
            {
                case SemiFunc.itemType.item_upgrade:
                    configEntry = Instance.Config.Bind("AllowedItems Upgrades", item.name, true,
                        new ConfigDescription("Whether this upgrade item can spawn in levels"));
                    break;
                case SemiFunc.itemType.drone:
                    configEntry = Instance.Config.Bind("AllowedItems Drones", item.name, true,
                        new ConfigDescription("Whether this drone item can spawn in levels"));
                    break;
                default:
                    continue;
            }

            if (!configEntry.Value)
            {
                DisallowedItems.Add(configEntry);
            }
        }
    }

    private static bool GetRandomItemOfType(SemiFunc.itemType itemType, out Item item)
    {
        item = null;

        // Filter candidate items by type, validity, value, and blacklist
        var possibleItems = StatsManager.instance.itemDictionary.Values
            .Where(i => i.itemType == itemType)
            .Where(i => i.value != null && i.value.valueMin > 0f)
            .Where(i => !DisallowedItems.Any(cfg => cfg.Definition.Key == i.name && !cfg.Value))
            .ToList();

        if (possibleItems.Count == 0)
        {
            Logger.LogWarning($"GetRandomItemOfType: No valid items found for type {itemType} after filtering.");
            return false;
        }

        bool useWeightedSelection = itemType switch
        {
            SemiFunc.itemType.item_upgrade => UseShopPriceForUpgradeItems.Value,
            SemiFunc.itemType.drone => UseShopPriceForDroneItems.Value,
            _ => false
        };

        if (useWeightedSelection)
        {
            // Weighted selection based on shop price (cheaper items spawn more often)
            float totalWeight = possibleItems.Sum(i => 1.0f / i.value.valueMin);

            if (totalWeight <= 0f || float.IsNaN(totalWeight) || float.IsInfinity(totalWeight))
            {
                Logger.LogWarning($"GetRandomItemOfType: Invalid total weight {totalWeight} for type {itemType}.");
                return false;
            }

            float randomRoll = Random.Range(0f, totalWeight);

            foreach (var currentItem in possibleItems)
            {
                float itemWeight = 1.0f / currentItem.value.valueMin;
                randomRoll -= itemWeight;

                if (randomRoll <= 0f)
                {
                    item = currentItem;
                    break;
                }
            }

            if (item == null)
            {
                Logger.LogWarning($"GetRandomItemOfType: Weighted selection failed for type {itemType}.");
                return false;
            }

            float itemChancePercent = (1.0f / item.value.valueMin) / totalWeight * 100.0f;
            Logger.LogInfo($"Selected {item.name} at {itemChancePercent:F2}% chance (weighted by shop price)");
        }
        else
        {
            // Equal chance for all items
            int randomIndex = Random.Range(0, possibleItems.Count);
            item = possibleItems[randomIndex];
            Logger.LogInfo($"Selected {item.name} at {100.0f / possibleItems.Count:F2}% chance (equal probability)");
        }

        return true;
    }

    private static bool HasValuablePropSwitch(ValuableVolume volume)
    {
        return volume.transform.GetComponentInParent<ValuablePropSwitch>() != null;
    }

    private static bool ShouldSpawnItem(ValuableVolume volume, out SemiFunc.itemType? itemType, out bool hasSwitch)
    {
        itemType = null;
        hasSwitch = HasValuablePropSwitch(volume);
        
        if (hasSwitch) return false; // Switches not supported yet (sync issues)

        switch (volume.VolumeType)
        {
            case ValuableVolume.Type.Tiny:
                if (!SpawnUpgradeItems.Value) return false;
                itemType = SemiFunc.itemType.item_upgrade;
                return Random.Range(0f, 100f) <= UpgradeItemSpawnChance.Value;

            case ValuableVolume.Type.Small:
                if (!SpawnDroneItems.Value) return false;
                itemType = SemiFunc.itemType.drone;
                return Random.Range(0f, 100f) <= DroneItemSpawnChance.Value;

            default:
                return false;
        }
    }

    private static GameObject SpawnItem(Item item, Vector3 position, Quaternion rotation)
    {
        GameObject spawnedObj;
        
        if (SemiFunc.IsMultiplayer())
        {
            spawnedObj = PhotonNetwork.Instantiate("Items/" + item.name, position, rotation, 0);
        }
        else
        {
            var preFab = Resources.Load<GameObject>("Items/" + item.name);
            if (preFab == null) return null;
            spawnedObj = Object.Instantiate(preFab, position, rotation);
        }
        
        // Add tracker component to the spawned item
        spawnedObj.AddComponent<SpawnedItemTracker>();
        return spawnedObj;
    }

    private static bool RandomItemSpawn(ValuableVolume volume)
    {
        if (!ShouldSpawnItem(volume, out var itemType, out var hasSwitch)) return false;
        if (!itemType.HasValue) return false;
        if (!GetRandomItemOfType(itemType.Value, out var item)) return false;

        SpawnItem(item, volume.transform.position, volume.transform.rotation);
        return true;
    }

    [HarmonyPatch(typeof(ValuableDirector), "Spawn")]
    [HarmonyPrefix]
    public static void ValuableDirector_Spawn_Prefix(GameObject _valuable, ValuableVolume _volume, string _path)
    {
        _volume.gameObject.AddComponent<UsedVolumeTracker>();
    }

    [HarmonyPatch(typeof(ValuableDirector), nameof(ValuableDirector.VolumesAndSwitchSetup))]
    [HarmonyPostfix]
    public static void ValuableDirector_VolumesAndSwitchSetup_Postfix(ValuableDirector __instance)
    {
        if (!SemiFunc.RunIsLevel()) return;

        var volumes = Object.FindObjectsOfType<ValuableVolume>(includeInactive: false)
            .Where(volume => volume.gameObject.GetComponent<UsedVolumeTracker>() == null)
            .Where(volume => !HasValuablePropSwitch(volume))
            .ToList();

        Logger.LogInfo($"Found {volumes.Count} potential volumes to spawn items in");
        
        int tinyVolumes = volumes.Count(v => v.VolumeType == ValuableVolume.Type.Tiny);
        int smallVolumes = volumes.Count(v => v.VolumeType == ValuableVolume.Type.Small);
        
        Logger.LogInfo($"Upgrade item spawn chance: {UpgradeItemSpawnChance.Value}% on {tinyVolumes} tiny volumes");
        Logger.LogInfo($"Drone item spawn chance: {DroneItemSpawnChance.Value}% on {smallVolumes} small volumes");

        int spawnedItems = volumes.Count(volume => RandomItemSpawn(volume));

        Logger.LogInfo($"Spawned {spawnedItems} items in total");
    }

    [HarmonyPatch(typeof(Map), nameof(Map.AddCustom))]
    [HarmonyPostfix]
    public static void Map_AddCustom_Postfix(MapCustom mapCustom)
    {
        if (!SemiFunc.RunIsLevel()) return;
        if (!mapCustom.gameObject.TryGetComponent<ItemAttributes>(out var itemAttributes)) return;

        // Hide map entities for specific item types if configured
        bool shouldHide = itemAttributes.item.itemType switch
        {
            SemiFunc.itemType.item_upgrade => MapHideUpgradeItems.Value,
            SemiFunc.itemType.drone => MapHideDroneItems.Value,
            _ => false
        };

        if (shouldHide)
        {
            mapCustom.mapCustomEntity.gameObject.SetActive(false);
        }
    }

    [HarmonyPatch(typeof(ExtractionPoint), "DestroyAllPhysObjectsInHaulList")]
    [HarmonyPostfix]
    public static void ExtractionPoint_DestroyAllPhysObjectsInHaulList_Postfix(ExtractionPoint __instance)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;

        var spawnedItemGameObjects = Object.FindObjectsOfType<SpawnedItemTracker>(includeInactive: false)
            .Select(tracker => tracker.gameObject)
            .ToList();

        foreach (var gameObject in spawnedItemGameObjects)
        {
            var roomVolumeCheck = gameObject.GetComponent<RoomVolumeCheck>();
            if (roomVolumeCheck == null) continue;
            
            if (roomVolumeCheck.CurrentRooms.Any(room => room.Extraction))
            {
                var itemAttr = gameObject.GetComponent<ItemAttributes>();

                Logger.LogInfo($"Adding item {gameObject.name} to purchased items");
                StatsManager.instance.ItemPurchase(itemAttr.item.name);

                Logger.LogInfo($"Destroying spawned item {gameObject.name} in extraction point {__instance.name}");
                gameObject.GetComponent<PhysGrabObject>().DestroyPhysGrabObject();
            }
        }
    }
}
