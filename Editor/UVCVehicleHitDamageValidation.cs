using System;
using System.Collections.Generic;
using System.Reflection;
using LiteNetLibManager;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
namespace MultiplayerARPG
{
    public static class UVCVehicleHitDamageValidation
    {
        public static void Validate()
        {
            var previous = SceneManager.GetActiveScene();
            var stage = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            MapInfo map = null;
            try
            {
                SceneManager.SetActiveScene(stage);
                var manager = new GameObject("hit test server").AddComponent<LiteNetLibGameManager>();
                Property(manager, "IsServer", true);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(UVCIntegrationDemoBuilder.Root + "/Demo/Prefabs/UVC_S34_Vehicle.prefab");
                var car = (GameObject)PrefabUtility.InstantiatePrefab(prefab, stage);
                // Place queries far away from the user's currently open scene.
                car.transform.position = new Vector3(10000f, 10000f, 10000f);
                var vehicle = car.GetComponent<VehicleEntity>();
                Property(vehicle.Identity, "Manager", manager);
                vehicle.CurrentHp = 1000;
                var hit = car.GetComponent<UVCVehicleHitDamage>();
                Check(hit != null, "Demo must have hit damage.");
                hit.OnIdentityInitialize();
                var driver = Character("driver", manager, 100);
                var target = Character("target", manager, 101);
                var collider = target.GetComponent<BoxCollider>();
                var passengers = (Dictionary<byte, BaseGameEntity>)Field(vehicle, "_passengers").GetValue(vehicle);
                passengers[0] = driver;
                target.transform.position = car.transform.position + new Vector3(0f, 0.7f, 2f);
                Physics.SyncTransforms();
                Hit(hit, collider, 2f);
                Check(target.Hits == 0, "Parking-speed contacts cause no damage.");
                Hit(hit, collider, 10f);
                Check(target.Hits == 1 && target.RawDamage == 98f && target.CurrentHp == 951 && ReferenceEquals(target.LastDriver.Entity, driver),
                    "Driver attribution and combat pipeline mitigation must be preserved.");
                var second = target.gameObject.AddComponent<CapsuleCollider>();
                Hit(hit, second, 10f);
                Check(target.Hits == 1, "Multiple colliders share one character cooldown.");
                Reset(hit); target.BlockDamage = true;
                Hit(hit, collider, 10f); Check(target.Hits == 1, "Combat permission must be honored."); target.BlockDamage = false;
                target.IsInvincible = true;
                Hit(hit, collider, 10f); Check(target.Hits == 1, "Invincibility must be honored."); target.IsInvincible = false;
                var safe = new GameObject("safe area").AddComponent<SafeArea>();
                driver.SafeArea = safe; Hit(hit, collider, 10f); Check(target.Hits == 1, "Safe-area drivers cannot ram."); driver.SafeArea = null;
                target.SafeArea = safe; Hit(hit, collider, 10f); Check(target.Hits == 1, "Safe-area targets cannot be rammed."); target.SafeArea = null;
                passengers[1] = target;
                Hit(hit, collider, 10f); Check(target.Hits == 1, "Own passengers are excluded."); passengers.Remove(1);
                passengers.Clear(); Hit(hit, collider, 10f); Check(target.Hits == 1, "Unoccupied cars cannot damage characters."); passengers[0] = driver;
                Property(manager, "IsServer", false);
                Hit(hit, collider, 10f); Check(target.Hits == 1, "Clients cannot apply hit damage."); Property(manager, "IsServer", true);
                map = ScriptableObject.CreateInstance<MapInfo>();
                target.TestMap = map;
                SetMapMode(map, PvpMode.None);
                Hit(hit, collider, 10f); Check(target.Hits == 1, "Kit PvE rules must reject player ramming.");
                SetMapMode(map, PvpMode.Pvp);
                driver.PartyId = target.PartyId = 7;
                Hit(hit, collider, 10f); Check(target.Hits == 1, "Kit party protection must reject ramming.");
                driver.PartyId = target.PartyId = 0;
                Hit(hit, collider, 100f); Check(target.Hits == 2 && target.RawDamage == 250f, "PvP permits hits and raw damage is capped.");
                Reset(hit); Hit(hit, collider, -10f); Check(target.Hits == 2, "Separating contacts cause no damage.");
                // Real overlap query against character controller/body colliders.
                var body = car.GetComponent<Rigidbody>();
                body.isKinematic = false;
                body.velocity = Vector3.forward * 10f;
                Invoke(hit, "FixedUpdate");
                Check(target.Hits == 3, "Chassis overlap must find the character.");
                Reset(hit);
                target.transform.position = car.transform.position + new Vector3(0f, 0.7f, 3f);
                var child = new GameObject("trigger hitbox"); child.transform.SetParent(target.transform, false);
                child.AddComponent<BoxCollider>().isTrigger = true;
                var box = child.AddComponent<DamageableHitBox>();
                Field(box, "_damageableEntity").SetValue(box, target);
                collider.enabled = second.enabled = false;
                body.velocity = Vector3.forward * 100f;
                Physics.SyncTransforms(); Invoke(hit, "FixedUpdate");
                car.transform.position += Vector3.forward * 2f;
                Physics.SyncTransforms(); Invoke(hit, "FixedUpdate");
                Check(target.Hits == 4, "Swept chassis must hit trigger character hitboxes between physics positions.");
                Reset(hit); body.velocity = Vector3.zero; Invoke(hit, "FixedUpdate");
                Check(target.Hits == 4, "Stationary overlap must not damage.");
                Reset(hit);
                target.transform.position = car.transform.position + Vector3.forward * 10f;
                body.velocity = Vector3.forward * 10f;
                Physics.SyncTransforms(); Invoke(hit, "FixedUpdate");
                car.transform.position += Vector3.forward * 20f;
                Physics.SyncTransforms(); Invoke(hit, "FixedUpdate");
                Check(target.Hits == 4, "External teleports must not sweep damage across the map.");
                var movement = car.GetComponent<UVCVehicleEntityMovement>();
                Invoke(movement, "Awake");
                movement.Teleport(car.transform.position + Vector3.right, Quaternion.identity, true);
                Check(!(bool)Field(hit, "_hasHistory").GetValue(hit), "Adapter teleports must explicitly reset hit sweep history.");
                Reset(hit); target.CurrentHp = 1;
                Hit(hit, child.GetComponent<Collider>(), 100f);
                Check(target.IsDead() && target.Hits == 5, "Lethal impacts must reach the combat received-damage path.");
                Hit(hit, child.GetComponent<Collider>(), 100f); Check(target.Hits == 5, "Dead targets cannot be hit again.");
                target.CurrentHp = 1000;
                var cooldowns = (Dictionary<BaseCharacterEntity, float>)Field(hit, "_nextHits").GetValue(hit);
                cooldowns[target] = Time.unscaledTime - 1f;
                Hit(hit, child.GetComponent<Collider>(), 10f);
                Check(target.Hits == 6, "A later impact is allowed after the target cooldown expires.");
            }
            finally
            {
                if (map != null) Object.DestroyImmediate(map);
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                EditorSceneManager.CloseScene(stage, true);
            }
        }
        private static UVCHitCharacterValidationProbe Character(string name, LiteNetLibGameManager manager, uint id)
        {
            var go = new GameObject(name);
            var target = go.AddComponent<UVCHitCharacterValidationProbe>();
            Property(target.Identity, "Manager", manager); Property(target.Identity, "ObjectId", id);
            target.CurrentHp = 1000;
            go.AddComponent<BoxCollider>();
            return target;
        }
        private static void Hit(UVCVehicleHitDamage hit, Collider collider, float speed) =>
            typeof(UVCVehicleHitDamage).GetMethod("TryHit", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(hit, new object[] { collider, Vector3.forward * speed, Vector3.forward, true });
        private static void Reset(UVCVehicleHitDamage hit) => hit.OnIdentityInitialize();
        private static void SetMapMode(MapInfo map, PvpMode mode)
        { var data = new SerializedObject(map); data.FindProperty("pvpMode").intValue = (int)mode; data.ApplyModifiedPropertiesWithoutUndo(); }
        private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("UVC hit validation: " + message); }
        private static FieldInfo Field(object target, string name)
        {
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            { var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); if (field != null) return field; }
            throw new MissingFieldException(name);
        }
        private static void Property(object target, string name, object value)
        {
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            { var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); if (prop != null) { prop.SetValue(target, value); return; } }
            throw new MissingMemberException(name);
        }
        private static void Invoke(object target, string name) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, null);
    }
}
