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
    [Info("Better Dropped Item Stacker", "VisEntities", "1.6.0")]
    [Description("Combines scattered dropped items into one container.")]
    public class BetterDroppedItemStacker : RustPlugin
    {
        #region Fields

        private static BetterDroppedItemStacker _plugin;
        private static Configuration _config;

        private List<Timer> _pendingGroupTimers = new List<Timer>();

        private const int LAYER_DROPPED_ITEM = Layers.Mask.Physics_Debris;        
        private const int LAYER_OBSTACLES = Layers.Mask.World | Layers.Mask.Terrain | Layers.Mask.Construction;
        
        private const string PREFAB_DROPPED_ITEM_CONTAINER = "assets/prefabs/misc/item drop/item_drop.prefab";

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
                    if (TerrainUtil.GetGroundInfo(worldEntity.transform.position, out RaycastHit raycastHit, 1f, LAYER_OBSTACLES))
                    {
                        DroppedItemContainer droppedItemContainer = SpawnDroppedItemContainer(raycastHit.point, Quaternion.FromToRotation(Vector3.up, raycastHit.normal), nearbyDroppedItems.Count, nearbyDroppedItems);
                    }
                }

                Pool.FreeUnmanaged(ref nearbyDroppedItems);
            });

            _pendingGroupTimers.Add(itemGroupingTimer);
        }

        #endregion Oxide Hooks

        #region Container Spawning and Setup

        private DroppedItemContainer SpawnDroppedItemContainer(Vector3 position, Quaternion rotation, int capacity, List<DroppedItem> droppedItems)
        {
            DroppedItemContainer droppedItemContainer = GameManager.server.CreateEntity(PREFAB_DROPPED_ITEM_CONTAINER, position, rotation) as DroppedItemContainer;
            if (droppedItemContainer == null)
                return null;

            droppedItemContainer.inventory = new ItemContainer();
            droppedItemContainer.inventory.ServerInitialize(null, capacity);
            droppedItemContainer.inventory.GiveUID();
            droppedItemContainer.inventory.entityOwner = droppedItemContainer;

            foreach (DroppedItem droppedItem in droppedItems)
            {
                droppedItem.item.MoveToContainer(droppedItemContainer.inventory);
            }

            droppedItemContainer.Spawn();

            float despawnTime = droppedItemContainer.CalculateRemovalTime();
            if (_config.ContainerLifetimeSeconds > 0)
                despawnTime = _config.ContainerLifetimeSeconds;

            droppedItemContainer.ResetRemovalTime(despawnTime);

            ExposedHooks.OnDroppedItemStackerSpawned(droppedItemContainer, droppedItems);
            return droppedItemContainer;
        }

        #endregion Container Spawning and Setup

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
            public static void OnDroppedItemStackerSpawned(DroppedItemContainer droppedItemContainer, List<DroppedItem> groupedItems)
            {
                Interface.CallHook("OnDroppedItemStackerSpawned", droppedItemContainer, groupedItems);
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