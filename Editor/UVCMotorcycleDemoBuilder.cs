using System;
using System.Linq;
using LiteNetLibManager;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MultiplayerARPG
{
    public static class UVCMotorcycleDemoBuilder
    {
        public const string PrefabPath = UVCIntegrationDemoBuilder.Root + "/Demo/Prefabs/UVC_Motorcycle_Vehicle.prefab";
        public const string TypePath = UVCIntegrationDemoBuilder.Root + "/Demo/GameData/VehicleType_UVCMotorcycle.asset";
        private const string Source = "Assets/UniversalVehicleController/ART/Prefabs/Vehicles/Motorbike/Motorbike.prefab";
        private const string ScenePath = UVCIntegrationDemoBuilder.Root + "/Demo/Scenes/UVCDrivingDemo.unity";
        private const string SceneId = "uvc_demo_motorcycle_0";

        [MenuItem("Tools/MMORPG KIT/UVC Integration/Add Motorcycle Demo")]
        public static void Build()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before building the motorcycle demo.");
            var database = AssetDatabase.LoadAssetAtPath<GameDatabase>(UVCIntegrationDemoBuilder.Root + "/Demo/GameData/GameDatabase_UVC.asset");
            if (database == null || AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
                throw new InvalidOperationException("Create the UVC driving demo first.");
            var type = AssetDatabase.LoadAssetAtPath<VehicleType>(TypePath);
            if (type == null)
            {
                type = ScriptableObject.CreateInstance<VehicleType>();
                type.name = "VehicleType_UVCMotorcycle";
                type.Id = "uvc_motorcycle";
                AssetDatabase.CreateAsset(type, TypePath);
            }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (prefab == null) prefab = CreatePrefab(type);
            ConfigureController(type);
#if !EXCLUDE_PREFAB_REFS || DISABLE_ADDRESSABLES
            var entity = prefab.GetComponent<VehicleEntity>();
            if (!(database.vehicleEntities ?? Array.Empty<VehicleEntity>()).Contains(entity))
            {
                database.vehicleEntities = (database.vehicleEntities ?? Array.Empty<VehicleEntity>()).Concat(new[] { entity }).ToArray();
                EditorUtility.SetDirty(database);
                AssetDatabase.SaveAssetIfDirty(database);
            }
#endif
            var scene = SceneManager.GetSceneByPath(ScenePath);
            bool loaded = scene.IsValid() && scene.isLoaded;
            if (!loaded) scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            try
            {
                if (!scene.GetRootGameObjects().SelectMany(go => go.GetComponentsInChildren<LiteNetLibIdentity>(true)).Any(id => id.SceneObjectId == SceneId))
                {
                    var bike = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                    bike.name = "UVC Motorcycle";
                    bike.transform.position = new Vector3(14f, 0.8f, 0f);
                    var identity = new SerializedObject(bike.GetComponent<LiteNetLibIdentity>());
                    identity.FindProperty("sceneObjectId").stringValue = SceneId;
                    identity.ApplyModifiedPropertiesWithoutUndo();
                    EditorSceneManager.SaveScene(scene);
                }
            }
            finally { if (!loaded) EditorSceneManager.CloseScene(scene, true); }
            Debug.Log("Motorcycle demo added beside the two cars. Start from 00Init_UVC. Q/E lean back/forward.");
        }

        private static GameObject CreatePrefab(VehicleType type)
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(Source);
            if (source == null) throw new InvalidOperationException("UVC Motorbike prefab is missing.");
            var previous = SceneManager.GetActiveScene();
            var stage = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(source, stage);
                go.name = "UVC_Motorcycle_Vehicle";
                var bike = go.GetComponent<PG.BikeController>();
                if (bike == null || bike.Wheels.Length != 2) throw new InvalidOperationException("Expected the configured two-wheel UVC bike.");
                // Use kit HP/crash damage. UVC's private local crash latch cannot be replicated or repaired safely.
                bike.Bike.MaxSqrGForceForCrash = float.MaxValue;
                bike.Bike.MaxReverseSpeedForCrash = float.MaxValue;
                // Author this parent once so Awake does not change the network visual hierarchy.
                if (bike.RearForkParent == null)
                {
                    var parent = new GameObject("RearFork_NetworkParent").transform;
                    parent.SetParent(bike.RearFork.parent, false);
                    parent.localPosition = bike.RearFork.localPosition;
                    bike.RearFork.SetParent(parent, true);
                    bike.RearForkParent = parent;
                }
                go.AddComponent<LiteNetLibIdentity>();
                var movement = go.AddComponent<UVCVehicleEntityMovement>();
                UVCVehicleEntityMovementFactory.EnsureInteractionCollider(go, movement.GetMovementBounds());
                var entity = go.AddComponent<VehicleEntity>();
                var data = new SerializedObject(entity);
                data.FindProperty("vehicleType").objectReferenceValue = type;
                data.FindProperty("activatableDistance").floatValue = 4f;
                data.ApplyModifiedPropertiesWithoutUndo();
                UVCIntegrationDemoBuilder.DisableDemoDeformation(go);
                UVCIntegrationDemoBuilder.ConfigureCrashDamage(go);
                var camera = Child(go.transform, "CameraTarget", new Vector3(0f, 1.1f, 0f));
                entity.CameraTargetTransform = camera;
                entity.FpsCameraTargetTransform = camera;
                entity.CombatTextTransform = camera;
                entity.OpponentAimTransform = camera;
                entity.Seats.Clear();
                entity.Seats.Add(new VehicleSeat
                {
                    cameraTarget = VehicleSeatCameraTarget.Vehicle,
                    passengingTransform = Child(go.transform, "DriverSeat", new Vector3(0f, 0.8f, -0.2f)),
                    exitTransform = Child(go.transform, "DriverExit", new Vector3(-1.5f, 0.5f, 0f)),
                    hidePassenger = true,
                });
                var visuals = new[] { bike.Handlebar, bike.FrontFork, bike.RearForkParent, bike.RearFork }.Distinct().ToArray();
                var movementData = new SerializedObject(movement);
                var additional = movementData.FindProperty("_additionalVisuals");
                additional.arraySize = visuals.Length;
                for (int i = 0; i < visuals.Length; ++i) additional.GetArrayElementAtIndex(i).objectReferenceValue = visuals[i];
                movementData.ApplyModifiedPropertiesWithoutUndo();
                return PrefabUtility.SaveAsPrefabAsset(go, PrefabPath);
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                EditorSceneManager.CloseScene(stage, true);
            }
        }

        private static void ConfigureController(VehicleType type)
        {
            string path = UVCIntegrationDemoBuilder.Root + "/Demo/Prefabs/PlayerCharacterController_UVC.prefab";
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var controller = root.GetComponent<UVCMotorcyclePlayerController>() ?? root.AddComponent<UVCMotorcyclePlayerController>();
                var data = new SerializedObject(root.GetComponent<BasePlayerCharacterController>());
                var mappings = data.FindProperty("vehicleControllers");
                int index = -1;
                for (int i = 0; i < mappings.arraySize; ++i)
                    if (mappings.GetArrayElementAtIndex(i).FindPropertyRelative("vehicleType").objectReferenceValue == type) index = i;
                if (index < 0) { index = mappings.arraySize; ++mappings.arraySize; }
                var entry = mappings.GetArrayElementAtIndex(index);
                entry.FindPropertyRelative("vehicleType").objectReferenceValue = type;
                var seats = entry.FindPropertyRelative("controllersForEachSeats");
                seats.arraySize = 1;
                seats.GetArrayElementAtIndex(0).objectReferenceValue = controller;
                data.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        private static Transform Child(Transform parent, string name, Vector3 position)
        {
            var child = new GameObject(name).transform;
            child.SetParent(parent, false);
            child.localPosition = position;
            return child;
        }
    }
}
