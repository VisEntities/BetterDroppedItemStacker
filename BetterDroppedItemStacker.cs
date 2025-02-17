/*
 * Copyright (C) 2024 Game4Freak.io
 * Your use of this mod indicates acceptance of the Game4Freak EULA.
 * Full legal terms can be found at https://game4freak.io/eula/
 */

using Facepunch;
using Newtonsoft.Json;
using Oxide.Core;
using Rust;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Better Dropped Item Stacker", "VisEntities", "1.7.0")]
    [Description("Combines scattered dropped items into one container.")]
    public class BetterDroppedItemStacker : RustPlugin
    {
        #region Fields

        private static BetterDroppedItemStacker _plugin;
        private static Configuration _config;

        private List<Timer> _pendingGroupTimers = new List<Timer>();

        private const int LAYER_ITEM_DROP = Layers.Mask.Ragdoll;
        private const int LAYER_DROPPED_ITEM = Layers.Mask.Physics_Debris;        
        private const int LAYER_OBSTACLES = Layers.Mask.Construction | Layers.Mask.Terrain | Layers.Mask.World;
        
        private const string PREFAB_ITEM_DROP = "assets/prefabs/misc/item drop/item_drop.prefab";

        #endregion Fields

        #region Configuration

        private class Configuration
        {
            [JsonProperty("Version")]
            public string Version { get; set; }

            [JsonProperty("Time Delay Before Grouping Items Seconds")]
            public float TimeDelayBeforeGroupingItemsSeconds { get; set; }
            
            [JsonProperty("Minimum Nearby Items Required To Group")]
            public int MinimumNearbyItemsRequiredToGroup { get; set; }

            [JsonProperty("Item Detection Radius")]
            public float ItemDetectionRadius { get; set; }

            [JsonProperty("Container Lifetime Seconds (0=auto-calculate)")]
            public float ContainerLifetimeSeconds { get; set; }

            [JsonProperty("Group Into Existing Containers")]
            public bool GroupIntoExistingContainers { get; set; }

            [JsonProperty("Item Categories To Ignore During Grouping")]
            public List<string> ItemCategoriesToIgnoreDuringGrouping { get; set; }
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<Configuration>();

            if (string.Compare(_config.Version, Version.ToString()) < 0)
                UpdateConfig();

            SaveConfig();
        }

        protected override void LoadDefaultConfig()
        {
            _config = GetDefaultConfig();
        }

        protected override void SaveConfig()
        {
            Config.WriteObject(_config, true);
        }

        private void UpdateConfig()
        {
            PrintWarning("Config changes detected! Updating...");

            Configuration defaultConfig = GetDefaultConfig();

            if (string.Compare(_config.Version, "1.0.0") < 0)
                _config = defaultConfig;

            if (string.Compare(_config.Version, "1.6.0") < 0)
                _config = defaultConfig;

            if (string.Compare(_config.Version, "1.7.0") < 0)
            {
                _config.GroupIntoExistingContainers = defaultConfig.GroupIntoExistingContainers;
            }

            PrintWarning("Config update complete! Updated from version " + _config.Version + " to " + Version.ToString());
            _config.Version = Version.ToString();
        }

        private Configuration GetDefaultConfig()
        {
            return new Configuration
            {
                Version = Version.ToString(),
                MinimumNearbyItemsRequiredToGroup = 5,
                TimeDelayBeforeGroupingItemsSeconds = 5,
                ItemDetectionRadius = 4f,
                ContainerLifetimeSeconds = 600f,
                GroupIntoExistingContainers = true,
                ItemCategoriesToIgnoreDuringGrouping = new List<string>
                {
                    "Weapon",
                    "Ammunition"
                }
            };
        }

        #endregion Configuration

        #region Oxide Hooks
        
        private void Init()
        {
            _plugin = this;
        }

        private void Unload()
        {
            foreach (Timer timer in _pendingGroupTimers)
            {
                if (timer != null)
                    timer.Destroy();
            }

            _config = null;
            _plugin = null;
        }

        private void OnItemDropped(Item item, BaseEntity worldEntity)
        {
            if (item == null || worldEntity == null)
                return;

            BasePlayer ownerPlayer = item.GetOwnerPlayer();
            if (ownerPlayer == null)
                return;

            if (ownerPlayer.InSafeZone())
                return;

            Timer itemGroupingTimer = timer.Once(_config.TimeDelayBeforeGroupingItemsSeconds, () =>
            {
                if (worldEntity == null)
                    return;

                List<DroppedItem> nearbyDroppedItems = GetNearbyDroppedItems(worldEntity.transform.position, _config.ItemDetectionRadius);
                if (nearbyDroppedItems.Count >= _config.MinimumNearbyItemsRequiredToGroup)
                {
                    DroppedItemContainer container = null;
                    if (_config.GroupIntoExistingContainers)
                    {
                        container = FindExistingDroppedItemContainer(worldEntity.transform.position, 2f);
                    }
                    if (container == null && TerrainUtil.GetGroundInfo(worldEntity.transform.position, out RaycastHit raycastHit, 1f, LAYER_OBSTACLES))
                    {
                        container = SpawnDroppedItemContainer(raycastHit.point, Quaternion.FromToRotation(Vector3.up, raycastHit.normal), nearbyDroppedItems.Count);
                    }
                    if (container != null)
                    {
                        GroupItemsIntoContainer(container, nearbyDroppedItems);
                    }
                }

                Pool.FreeUnmanaged(ref nearbyDroppedItems);
            });

            _pendingGroupTimers.Add(itemGroupingTimer);
        }

        #endregion Oxide Hooks

        #region Container Spawning and Setup

        private DroppedItemContainer SpawnDroppedItemContainer(Vector3 position, Quaternion rotation, int capacity)
        {
            DroppedItemContainer container = GameManager.server.CreateEntity(PREFAB_ITEM_DROP, position, rotation) as DroppedItemContainer;
            if (container == null)
                return null;

            container.inventory = new ItemContainer();
            container.inventory.ServerInitialize(null, capacity);
            container.inventory.GiveUID();
            container.inventory.entityOwner = container;

            container.Spawn();

            float despawnTime = container.CalculateRemovalTime();
            if (_config.ContainerLifetimeSeconds > 0)
            {
                despawnTime = _config.ContainerLifetimeSeconds;
            }
            container.ResetRemovalTime(despawnTime);

            return container;
        }

        private DroppedItemContainer FindExistingDroppedItemContainer(Vector3 position, float searchRadius)
        {
            List<DroppedItemContainer> nearbyContainers = Pool.Get<List<DroppedItemContainer>>();
            Vis.Entities(position, searchRadius, nearbyContainers, LAYER_ITEM_DROP, QueryTriggerInteraction.Ignore);

            foreach (DroppedItemContainer container in nearbyContainers)
            {
                if (container != null && TerrainUtil.HasLineOfSight(position, container.transform.position, LAYER_OBSTACLES))
                {
                    Pool.FreeUnmanaged(ref nearbyContainers);
                    return container;
                }
            }
            Pool.FreeUnmanaged(ref nearbyContainers);
            return null;
        }

        #endregion Container Spawning and Setup

        #region Item Grouping

        private void GroupItemsIntoContainer(DroppedItemContainer container, List<DroppedItem> items)
        {
            int currentSlotCount = container.inventory.itemList.Count;
            int requiredSlots = items.Count;
            if (currentSlotCount + requiredSlots > container.inventory.capacity)
            {
                int newCapacity = currentSlotCount + requiredSlots;
                container.inventory.capacity = newCapacity;
            }

            foreach (DroppedItem droppedItem in items)
            {
                droppedItem.item.MoveToContainer(container.inventory);
            }

            float newDespawnTime = container.CalculateRemovalTime();
            if (_config.ContainerLifetimeSeconds > 0)
            {
                newDespawnTime = _config.ContainerLifetimeSeconds;
            }
            container.ResetRemovalTime(newDespawnTime);

            ExposedHooks.OnDroppedItemsGrouped(container, items);
        }

        #endregion Item Grouping

        #region Nearby Items Retrieval

        private List<DroppedItem> GetNearbyDroppedItems(Vector3 position, float radius)
        {
            List<DroppedItem> droppedItems = Pool.Get<List<DroppedItem>>();
            Vis.Entities(position, radius, droppedItems, LAYER_DROPPED_ITEM, QueryTriggerInteraction.Ignore);

            for (int i = droppedItems.Count - 1; i >= 0; i--)
            {
                DroppedItem droppedItem = droppedItems[i];
                if (droppedItem == null || droppedItem.ShortPrefabName != "generic_world"
                    || _config.ItemCategoriesToIgnoreDuringGrouping.Contains(droppedItem.item.info.category.ToString())
                    || !TerrainUtil.HasLineOfSight(position, droppedItem.transform.position, LAYER_OBSTACLES))
                {
                    droppedItems.RemoveAt(i);
                }
            }

            return droppedItems;
        }

        #endregion Nearby Items Retrieval

        #region Exposed Hooks

        private static class ExposedHooks
        {
            public static void OnDroppedItemsGrouped(DroppedItemContainer container, List<DroppedItem> groupedItems)
            {
                Interface.CallHook("OnDroppedItemsGrouped", container, groupedItems);
            }
        }

        #endregion Exposed Hooks

        #region Helper Classes

        public static class TerrainUtil
        {
            public static bool HasLineOfSight(Vector3 startPosition, Vector3 endPosition, LayerMask mask)
            {
                return GamePhysics.LineOfSight(startPosition, endPosition, mask);
            }

            public static bool GetGroundInfo(Vector3 startPosition, out RaycastHit raycastHit, float range, LayerMask mask)
            {
                return Physics.Linecast(startPosition + new Vector3(0.0f, range, 0.0f), startPosition - new Vector3(0.0f, range, 0.0f), out raycastHit, mask);
            }

            public static bool GetGroundInfo(Vector3 startPosition, out RaycastHit raycastHit, float range, LayerMask mask, Transform ignoreTransform = null)
            {
                startPosition.y += 0.25f;
                range += 0.25f;
                raycastHit = default;

                RaycastHit hit;
                if (!GamePhysics.Trace(new Ray(startPosition, Vector3.down), 0f, out hit, range, mask, QueryTriggerInteraction.UseGlobal, null))
                    return false;

                if (ignoreTransform != null && hit.collider != null
                    && (hit.collider.transform == ignoreTransform || hit.collider.transform.IsChildOf(ignoreTransform)))
                {
                    return GetGroundInfo(startPosition - new Vector3(0f, 0.01f, 0f), out raycastHit, range, mask, ignoreTransform);
                }

                raycastHit = hit;
                return true;
            }
        }

        #endregion Helper Classes
    }
}