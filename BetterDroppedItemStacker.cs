/*
 * Copyright (C) 2024 Game4Freak.io
 * Your use of this mod indicates acceptance of the Game4Freak EULA.
 * Full legal terms can be found at https://game4freak.io/eula/
 */

using Facepunch;
using Newtonsoft.Json;
using Rust;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Better Dropped Item Stacker", "VisEntities", "1.4.0")]
    [Description("Reduces the number of individual dropped items by grouping them into one container.")]
    public class BetterDroppedItemStacker : RustPlugin
    {
        #region Fields

        private static BetterDroppedItemStacker _plugin;
        private static Configuration _config;

        private List<Timer> _activeTimers = new List<Timer>();

        private const int LAYER_PHYSICS_DEBRIS = Layers.Mask.Physics_Debris;        
        private const int LAYER_OBSTACLES = Layers.Mask.World | Layers.Mask.Terrain | Layers.Mask.Construction;

        private const string PREFAB_ITEM_DROP = "assets/prefabs/misc/item drop/item_drop.prefab";

        #endregion Fields

        #region Configuration

        private class Configuration
        {
            [JsonProperty("Version")]
            public string Version { get; set; }

            [JsonProperty("Duration Before Grouping Items Seconds")]
            public float DurationBeforeGroupingItemsSeconds { get; set; }

            [JsonProperty("Number Of Nearby Items Needed For Grouping")]
            public int NumberOfNearbyItemsNeededForGrouping { get; set; }

            [JsonProperty("Detection Radius For Nearby Dropped Items")]
            public float DetectionRadiusForNearbyDroppedItems { get; set; }

            [JsonProperty("Dropped Item Container Fallback Despawn Time Seconds")]
            public float DroppedItemContainerFallbackDespawnTimeSeconds { get; set; }

            [JsonProperty("Item Categories To Exclude From Grouping ")]
            public List<string> ItemCategoriesToExcludeFromGrouping { get; set; }
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

            if (string.Compare(_config.Version, "1.1.0") < 0)
            {
                _config.DroppedItemContainerFallbackDespawnTimeSeconds = defaultConfig.DroppedItemContainerFallbackDespawnTimeSeconds;
            }

            if (string.Compare(_config.Version, "1.2.0") < 0)
            {
                _config.DroppedItemContainerFallbackDespawnTimeSeconds = defaultConfig.DroppedItemContainerFallbackDespawnTimeSeconds;
                _config.ItemCategoriesToExcludeFromGrouping = defaultConfig.ItemCategoriesToExcludeFromGrouping;
            }

            PrintWarning("Config update complete! Updated from version " + _config.Version + " to " + Version.ToString());
            _config.Version = Version.ToString();
        }

        private Configuration GetDefaultConfig()
        {
            return new Configuration
            {
                Version = Version.ToString(),
                NumberOfNearbyItemsNeededForGrouping = 6,
                DurationBeforeGroupingItemsSeconds = 5,
                DetectionRadiusForNearbyDroppedItems = 4f,
                DroppedItemContainerFallbackDespawnTimeSeconds = 300f,
                ItemCategoriesToExcludeFromGrouping = new List<string>
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
            foreach (Timer timer in _activeTimers)
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

            var itemGroupingTimer = timer.Once(_config.DurationBeforeGroupingItemsSeconds, () =>
            {
                if (worldEntity == null)
                    return;

                List<DroppedItem> nearbyDroppedItems = GetNearbyDroppedItems(worldEntity.transform.position, _config.DetectionRadiusForNearbyDroppedItems);
                if (nearbyDroppedItems.Count >= _config.NumberOfNearbyItemsNeededForGrouping)
                {
                    if (TerrainUtil.GetGroundInfo(worldEntity.transform.position, out RaycastHit raycastHit, 1f, LAYER_OBSTACLES))
                    {
                        DroppedItemContainer droppedItemContainer = SpawnDroppedItemContainer(raycastHit.point, Quaternion.FromToRotation(Vector3.up, raycastHit.normal), nearbyDroppedItems.Count, nearbyDroppedItems);
                    }
                }

                Pool.FreeList(ref nearbyDroppedItems);
            });

            _activeTimers.Add(itemGroupingTimer);
        }

        #endregion Oxide Hooks

        #region Item Container Spawning and Setup

        private DroppedItemContainer SpawnDroppedItemContainer(Vector3 position, Quaternion rotation, int capacity, List<DroppedItem> droppedItems)
        {
            DroppedItemContainer droppedItemContainer = GameManager.server.CreateEntity(PREFAB_ITEM_DROP, position, rotation) as DroppedItemContainer;
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
            // Calculate the removal time based on contents of the container and ensure it's no less than the configured minimum.
            droppedItemContainer.ResetRemovalTime(Math.Max(_config.DroppedItemContainerFallbackDespawnTimeSeconds, droppedItemContainer.CalculateRemovalTime()));
        
            return droppedItemContainer;
        }

        #endregion Item Container Spawning and Setup

        #region Nearby Items Retrieval

        private List<DroppedItem> GetNearbyDroppedItems(Vector3 position, float radius)
        {
            List<DroppedItem> droppedItems = Pool.GetList<DroppedItem>();
            Vis.Entities(position, radius, droppedItems, LAYER_PHYSICS_DEBRIS, QueryTriggerInteraction.Ignore);

            for (int i = droppedItems.Count - 1; i >= 0; i--)
            {
                DroppedItem droppedItem = droppedItems[i];
                if (droppedItem == null || droppedItem.ShortPrefabName != "generic_world"
                    || _config.ItemCategoriesToExcludeFromGrouping.Contains(droppedItem.item.info.category.ToString())
                    || !GamePhysics.LineOfSight(position, droppedItem.transform.position, LAYER_OBSTACLES))
                {
                    droppedItems.RemoveAt(i);
                }
            }

            return droppedItems;
        }

        #endregion Nearby Items Retrieval

        #region Helper Classes

        public static class TerrainUtil
        {
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