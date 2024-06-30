using Facepunch;
using Newtonsoft.Json;
using Rust;
using System.Collections.Generic;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Better Dropped Item Stacker", "VisEntities", "1.0.1")]
    [Description("Reduces the number of individual dropped items by grouping them into one container.")]
    public class BetterDroppedItemStacker : RustPlugin
    {
        #region Fields

        private static BetterDroppedItemStacker _plugin;
        private static Configuration _config;

        private List<Timer> _activeTimers = new List<Timer>();

        private const int LAYER_WORLD = Layers.Mask.World;
        private const int LAYER_TERRAIN = Layers.Mask.Terrain;
        private const int LAYER_CONSTRUCTION = Layers.Mask.Construction;
        private const int LAYER_PHYSICS_DEBRIS = Layers.Mask.Physics_Debris;

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
                DetectionRadiusForNearbyDroppedItems = 4f
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

            var itemGroupingTimer = timer.Once(_config.DurationBeforeGroupingItemsSeconds, () =>
            {
                if (worldEntity == null)
                    return;

                List<DroppedItem> nearbyDroppedItems = GetNearbyDroppedItems(worldEntity.transform.position, _config.DetectionRadiusForNearbyDroppedItems);

                if (nearbyDroppedItems.Count >= _config.NumberOfNearbyItemsNeededForGrouping)
                {
                    if (TerrainUtil.GetGroundInfo(worldEntity.transform.position, out RaycastHit raycastHit, 5f, LAYER_TERRAIN | LAYER_WORLD | LAYER_CONSTRUCTION))
                    {
                        DroppedItemContainer droppedItemContainer = SpawnDroppedItemContainer(raycastHit.point, Quaternion.FromToRotation(Vector3.up, raycastHit.normal), nearbyDroppedItems.Count);
                        if (droppedItemContainer != null)
                        {
                            foreach (DroppedItem nearbyItem in nearbyDroppedItems)
                            {
                                if (nearbyItem != null && nearbyItem.ShortPrefabName == "generic_world")
                                {
                                    if (!nearbyItem.item.MoveToContainer(droppedItemContainer.inventory))
                                    {
                                        // nearbyItem.item.Remove();
                                    }
                                }
                            }
                        }
                    }
                }

                Pool.FreeList(ref nearbyDroppedItems);
            });

            _activeTimers.Add(itemGroupingTimer);
        }

        #endregion Oxide Hooks

        #region Item Container Spawning and Setup

        private DroppedItemContainer SpawnDroppedItemContainer(Vector3 position, Quaternion rotation, int capacity)
        {
            DroppedItemContainer droppedItemContainer = GameManager.server.CreateEntity(PREFAB_ITEM_DROP, position, rotation) as DroppedItemContainer;
            if (droppedItemContainer == null)
                return null;

            droppedItemContainer.inventory = new ItemContainer();
            droppedItemContainer.inventory.ServerInitialize(null, capacity);
            droppedItemContainer.inventory.GiveUID();
            droppedItemContainer.inventory.entityOwner = droppedItemContainer;

            droppedItemContainer.Spawn();
            return droppedItemContainer;
        }

        #endregion Item Container Spawning and Setup

        #region Nearby Items Retrieval

        private List<DroppedItem> GetNearbyDroppedItems(Vector3 position, float radius)
        {
            List<DroppedItem> droppedItems = Pool.GetList<DroppedItem>();
            Vis.Entities(position, radius, droppedItems, LAYER_PHYSICS_DEBRIS, QueryTriggerInteraction.Ignore);
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