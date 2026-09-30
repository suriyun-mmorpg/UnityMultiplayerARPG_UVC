using System;
using System.IO;
using System.Linq;
using LiteNetLibManager;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MultiplayerARPG
{
    /// <summary>Creates project-specific demo assets without changing the UVC package or kit demos.</summary>
    [InitializeOnLoad]
    public static class UVCIntegrationDemoBuilder
    {
        public const string Root = "Assets/UVCIntegration";
        private const string CarSource = "Assets/UniversalVehicleController/ART/Prefabs/Vehicles/S34/S34_Stock.prefab";
        private const string InitSource = "Assets/UnityMultiplayerARPG/Demo/Scenes/00Init.unity";
        private const string DatabaseSource = "Assets/UnityMultiplayerARPG/Demo/GameData/GameDatabase.asset";
        private const string ControllerSource = "Assets/UnityMultiplayerARPG/Demo/Prefabs/Gameplay/PlayerCharacterController.prefab";
        private const string Request = "Temp/UVCIntegration.BuildDemo.request";

        static UVCIntegrationDemoBuilder()
        {
            // One-shot local build requests are useful when running Unity versions without Pipeline.
            EditorApplication.delayCall += ProcessRequest;
        }

        private static void ProcessRequest()
        {
            if (!File.Exists(Request) || EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += ProcessRequest;
                return;
            }
            File.Delete(Request);
            try
            {
                BuildDemo();
                File.WriteAllText("Temp/UVCIntegration.BuildDemo.result", "SUCCESS");
            }
            catch (Exception ex)
            {
                File.WriteAllText("Temp/UVCIntegration.BuildDemo.result", ex.ToString());
                Debug.LogException(ex);
            }
        }

        [MenuItem("Tools/MMORPG KIT/UVC Integration/Create Demo Assets")]
        public static void BuildDemo()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Exit Play Mode before creating demo assets.");
            RequireAsset<GameObject>(CarSource);
            RequireAsset<GameObject>(ControllerSource);
            RequireAsset<GameDatabase>(DatabaseSource);
            EnsureFolder(Root + "/Demo/Scenes");
            EnsureFolder(Root + "/Demo/Prefabs");
            EnsureFolder(Root + "/Demo/GameData");

            string initPath = Root + "/Demo/Scenes/00Init_UVC.unity";
            string mapPath = Root + "/Demo/Scenes/UVCDrivingDemo.unity";
            // Never overwrite authored scenes or assets on subsequent imports.
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(initPath) != null || AssetDatabase.LoadAssetAtPath<SceneAsset>(mapPath) != null)
                throw new InvalidOperationException("UVC demo scenes already exist. Existing authored demo assets are preserved.");

            var type = CreateAsset<VehicleType>("VehicleType_UVCCar");
            type.Id = "uvc_car";
            var map = CreateAsset<MapInfo>("MapInfo_UVCDrivingDemo");
            map.Id = "uvc_driving_demo";
            map.respawnPointsByCondition = new WarpPointByCondition[0];
            SetVector(map, "startPosition", new Vector3(-4f, 1f, 0f));

            Scene previous = SceneManager.GetActiveScene();
            Scene stage = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                SceneManager.SetActiveScene(stage);
                GameObject car = (GameObject)PrefabUtility.InstantiatePrefab(RequireAsset<GameObject>(CarSource), stage);
                car.name = "UVC_S34_Vehicle";
                var identity = car.AddComponent<LiteNetLibIdentity>();
                var movement = car.AddComponent<UVCVehicleEntityMovement>();
                UVCVehicleEntityMovementFactory.EnsureInteractionCollider(car, movement.GetMovementBounds());
                DisableDemoDeformation(car);
                var entity = car.AddComponent<VehicleEntity>();
                SetObject(entity, "vehicleType", type);
                ConfigureCrashDamage(car);
                SetFloat(entity, "activatableDistance", 4f);
                var cameraTarget = Child(car.transform, "CameraTarget", new Vector3(0f, 1.5f, 0f));
                entity.CameraTargetTransform = cameraTarget;
                entity.FpsCameraTargetTransform = cameraTarget;
                entity.CombatTextTransform = cameraTarget;
                entity.OpponentAimTransform = cameraTarget;
                entity.Seats.Add(new VehicleSeat
                {
                    cameraTarget = VehicleSeatCameraTarget.Vehicle,
                    passengingTransform = Child(car.transform, "DriverSeat", new Vector3(-0.4f, 0.7f, 0f)),
                    exitTransform = Child(car.transform, "DriverExit", new Vector3(-2f, 0.5f, 0f)),
                    hidePassenger = true,
                });
                entity.Seats.Add(new VehicleSeat
                {
                    cameraTarget = VehicleSeatCameraTarget.Vehicle,
                    passengingTransform = Child(car.transform, "PassengerSeat", new Vector3(0.4f, 0.7f, 0f)),
                    exitTransform = Child(car.transform, "PassengerExit", new Vector3(2f, 0.5f, 0f)),
                    hidePassenger = true,
                });
                car.GetComponent<PG.CarController>().Engine.EnableBoost = true;
                var vehiclePrefab = PrefabUtility.SaveAsPrefabAsset(car, Root + "/Demo/Prefabs/UVC_S34_Vehicle.prefab");
                Object.DestroyImmediate(car);

                GameObject controls = (GameObject)PrefabUtility.InstantiatePrefab(RequireAsset<GameObject>(ControllerSource), stage);
                controls.name = "PlayerCharacterController_UVC";
                var driverController = controls.AddComponent<UVCVehiclePlayerController>();
                var passengerController = controls.AddComponent<UVCVehiclePlayerController>();
                var controller = controls.GetComponent<BasePlayerCharacterController>();
                var serialized = new SerializedObject(controller);
                var mappings = serialized.FindProperty("vehicleControllers");
                mappings.arraySize = 1;
                var entry = mappings.GetArrayElementAtIndex(0);
                entry.FindPropertyRelative("vehicleType").objectReferenceValue = type;
                var seats = entry.FindPropertyRelative("controllersForEachSeats");
                seats.arraySize = 2;
                seats.GetArrayElementAtIndex(0).objectReferenceValue = driverController;
                seats.GetArrayElementAtIndex(1).objectReferenceValue = passengerController;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                var controllerPrefab = PrefabUtility.SaveAsPrefabAsset(controls, Root + "/Demo/Prefabs/PlayerCharacterController_UVC.prefab");
                Object.DestroyImmediate(controls);

                // Two scene vehicles allow testing ownership handover and passenger seating.
                for (int i = 0; i < 2; ++i)
                {
                    GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(vehiclePrefab, stage);
                    instance.name = i == 0 ? "UVC Car A" : "UVC Car B";
                    instance.transform.position = new Vector3(i * 7f, 0.8f, 0f);
                    var sceneIdentity = new SerializedObject(instance.GetComponent<LiteNetLibIdentity>());
                    sceneIdentity.FindProperty("sceneObjectId").stringValue = "uvc_demo_car_" + i;
                    sceneIdentity.ApplyModifiedPropertiesWithoutUndo();
                }
                CreateDrivingArea();
                new GameObject("Demo instructions").AddComponent<UVCDemoInstructions>();
                EditorSceneManager.SaveScene(stage, mapPath);
                var mapSerialized = new SerializedObject(map);
                var scene = mapSerialized.FindProperty("scene");
                scene.FindPropertyRelative("sceneAsset").objectReferenceValue = RequireAsset<SceneAsset>(mapPath);
                scene.FindPropertyRelative("sceneName").stringValue = "UVCDrivingDemo";
                mapSerialized.ApplyModifiedPropertiesWithoutUndo();

                var database = Object.Instantiate(RequireAsset<GameDatabase>(DatabaseSource));
                database.name = "GameDatabase_UVC";
#if !EXCLUDE_PREFAB_REFS || DISABLE_ADDRESSABLES
                database.vehicleEntities = (database.vehicleEntities ?? new VehicleEntity[0]).Concat(new[] { vehiclePrefab.GetComponent<VehicleEntity>() }).ToArray();
#endif
                database.mapInfos = (database.mapInfos ?? new BaseMapInfo[0]).Concat(new[] { map }).ToArray();
                database.playerCharacters = (database.playerCharacters ?? new PlayerCharacter[0]).Select(source =>
                {
                    var clone = Object.Instantiate(source);
                    clone.name = source.name + "_UVC";
                    clone.Id = source.Id + "_uvc";
                    SetObject(clone, "startMap", map);
                    SetBool(clone, "useOverrideStartPosition", false);
                    SetBool(clone, "useOverrideStartRotation", false);
                    var data = new SerializedObject(clone);
                    data.FindProperty("startPointsByCondition").arraySize = 0;
                    data.ApplyModifiedPropertiesWithoutUndo();
                    AssetDatabase.CreateAsset(clone, Root + "/Demo/GameData/" + clone.name + ".asset");
                    return clone;
                }).ToArray();
                CreatePlayerEntityPrefabs(database);
                AssetDatabase.CreateAsset(database, Root + "/Demo/GameData/GameDatabase_UVC.asset");
                AssetDatabase.CopyAsset(InitSource, initPath);
                Scene init = EditorSceneManager.OpenScene(initPath, OpenSceneMode.Additive);
                try
                {
                    GameInstance game = init.GetRootGameObjects().SelectMany(go => go.GetComponentsInChildren<GameInstance>(true)).Single();
                    SetObject(game, "gameDatabase", database);
                    SetObject(game, "defaultControllerPrefab", controllerPrefab.GetComponent<BasePlayerCharacterController>());
                    var gameSerialized = new SerializedObject(game);
                    var addressable = gameSerialized.FindProperty("addressableDefaultControllerPrefab");
                    if (addressable != null)
                        addressable.FindPropertyRelative("m_AssetGUID").stringValue = string.Empty;
                    gameSerialized.ApplyModifiedPropertiesWithoutUndo();
                    EditorSceneManager.SaveScene(init);
                }
                finally { EditorSceneManager.CloseScene(init, true); }
                EditorUtility.SetDirty(map);
                EditorUtility.SetDirty(type);
                AssetDatabase.SaveAssets();
                AddBuildScenes(initPath, "Assets/UnityMultiplayerARPG/Demo/Scenes/01Home.unity", mapPath);
                Debug.Log("UVC demo created. Open 00Init_UVC, create a new character, and start Single Player or Host.");
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded)
                    SceneManager.SetActiveScene(previous);
                EditorSceneManager.CloseScene(stage, true);
            }
        }

        [MenuItem("Tools/MMORPG KIT/UVC Integration/Configure Demo Networking")]
        public static void ConfigureDemoNetworking()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Exit Play Mode before configuring demo networking.");
            string path = Root + "/Demo/Prefabs/UVC_S34_Vehicle.prefab";
            var prefab = PrefabUtility.LoadPrefabContents(path);
            try
            {
                DisableDemoDeformation(prefab);
                ConfigureCrashDamage(prefab);
                PrefabUtility.SaveAsPrefabAsset(prefab, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(prefab); }
        }

        private static void DisableDemoDeformation(GameObject car)
        {
            // The demo uses kit gameplay damage. UVC deformation would create
            // different colliders on the server and predicting owner without damage replication.
            foreach (var damage in car.GetComponentsInChildren<PG.VehicleDamageController>(true))
                SetFloat(damage, "DamageFactor", 0f);
        }

        private static void ConfigureCrashDamage(GameObject car)
        {
            if (car.GetComponent<UVCVehicleHitDamage>() == null)
                car.AddComponent<UVCVehicleHitDamage>();
            var hitDamage = car.GetComponent<UVCVehicleHitDamage>();
            var chassisBounds = car.GetComponent<BoxCollider>();
            if (chassisBounds != null)
            {
                SetVector(hitDamage, "_center", chassisBounds.center);
                SetVector(hitDamage, "_size", chassisBounds.size + Vector3.one * 0.05f);
            }
            if (car.GetComponent<UVCVehicleCrashDamage>() == null)
                car.AddComponent<UVCVehicleCrashDamage>();
            var entity = car.GetComponent<VehicleEntity>();
            var data = new SerializedObject(entity);
            data.FindProperty("canBeAttacked").boolValue = true;
            data.FindProperty("hp").FindPropertyRelative("baseAmount").intValue = 1000;
            data.FindProperty("destroyDelay").floatValue = 3f;
            data.FindProperty("destroyRespawnDelay").floatValue = 10f;
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        [MenuItem("Tools/MMORPG KIT/UVC Integration/Create Player Entity Prefabs")]
        public static void CreatePlayerEntityPrefabs()
        {
            var database = RequireAsset<GameDatabase>(Root + "/Demo/GameData/GameDatabase_UVC.asset");
            CreatePlayerEntityPrefabs(database);
            EditorUtility.SetDirty(database);
            AssetDatabase.SaveAssetIfDirty(database);
        }

        private static void CreatePlayerEntityPrefabs(GameDatabase database)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Exit Play Mode before creating player entity prefabs.");
#if !EXCLUDE_PREFAB_REFS || DISABLE_ADDRESSABLES
            var sources = RequireAsset<GameDatabase>(DatabaseSource).playerCharacterEntities
                .Where(source => source != null).ToArray();
            if (sources.Length == 0)
                throw new InvalidOperationException("Source database has no valid player character entity prefabs.");
            var classes = database.playerCharacters.ToDictionary(data => data.Id);
            var prefabs = new BasePlayerCharacterEntity[sources.Length];
            Scene previous = SceneManager.GetActiveScene();
            Scene stage = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                for (int i = 0; i < sources.Length; ++i)
                {
                    var source = sources[i];
                    string path = Root + "/Demo/Prefabs/" + source.name + "_UVC.prefab";
                    var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(existing != null ? existing : source.gameObject, stage);
                    try
                    {
                        instance.name = source.name + "_UVC";
                        var entity = instance.GetComponent<BasePlayerCharacterEntity>();
                        var serialized = new SerializedObject(entity);
                        var data = serialized.FindProperty("characterDatabases");
                        var sourceClasses = source.CharacterDatabases;
                        if (sourceClasses.Length == 0)
                            throw new InvalidOperationException("Source player entity has no classes: " + source.name);
                        data.arraySize = sourceClasses.Length;
                        for (int c = 0; c < sourceClasses.Length; ++c)
                        {
                            if (sourceClasses[c] == null || !classes.TryGetValue(sourceClasses[c].Id + "_uvc", out var demoClass))
                                throw new InvalidOperationException("Missing demo class for " + source.name);
                            data.GetArrayElementAtIndex(c).objectReferenceValue = demoClass;
                        }
                        serialized.ApplyModifiedPropertiesWithoutUndo();
                        prefabs[i] = PrefabUtility.SaveAsPrefabAsset(instance, path).GetComponent<BasePlayerCharacterEntity>();
                    }
                    finally { Object.DestroyImmediate(instance); }
                }
                database.playerCharacterEntities = prefabs;
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded)
                    SceneManager.SetActiveScene(previous);
                EditorSceneManager.CloseScene(stage, true);
            }
#else
            throw new InvalidOperationException("Demo generation requires direct prefab references enabled.");
#endif
        }

        private static void CreateDrivingArea()
        {
            CreateBox("Driving pad", new Vector3(0f, -0.5f, 35f), new Vector3(70f, 1f, 100f), new Color(0.22f, 0.25f, 0.28f));
            for (int i = 0; i < 8; ++i)
            {
                CreateBox("Slalom marker " + (i + 1), new Vector3((i % 2 == 0 ? -1 : 1) * 4f, 0.5f, 15f + i * 7f), new Vector3(0.8f, 1f, 0.8f), new Color(1f, 0.5f, 0.1f));
            }
            var ramp = CreateBox("Brake test ramp", new Vector3(20f, 1f, 35f), new Vector3(7f, 0.6f, 12f), new Color(0.35f, 0.4f, 0.5f));
            ramp.transform.rotation = Quaternion.Euler(-12f, 0f, 0f);
            var light = new GameObject("Sun").AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            RenderSettings.ambientLight = new Color(0.5f, 0.5f, 0.5f);
        }

        private static GameObject CreateBox(string name, Vector3 position, Vector3 scale, Color color)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.position = position;
            go.transform.localScale = scale;
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard"));
            material.color = color;
            string materialPath = Root + "/Demo/GameData/" + name.Replace(" ", "_") + ".mat";
            AssetDatabase.CreateAsset(material, materialPath);
            go.GetComponent<Renderer>().sharedMaterial = material;
            return go;
        }

        private static Transform Child(Transform parent, string name, Vector3 position)
        {
            var child = new GameObject(name).transform;
            child.SetParent(parent, false);
            child.localPosition = position;
            return child;
        }

        private static T RequireAsset<T>(string path) where T : Object =>
            AssetDatabase.LoadAssetAtPath<T>(path) ?? throw new FileNotFoundException("Required demo dependency not found: " + path);

        private static T CreateAsset<T>(string name) where T : ScriptableObject
        {
            var asset = ScriptableObject.CreateInstance<T>();
            asset.name = name;
            AssetDatabase.CreateAsset(asset, Root + "/Demo/GameData/" + name + ".asset");
            return asset;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = path.Substring(0, path.LastIndexOf('/'));
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, path.Substring(path.LastIndexOf('/') + 1));
        }

        private static void SetObject(Object target, string field, Object value)
        {
            var data = new SerializedObject(target);
            data.FindProperty(field).objectReferenceValue = value;
            data.ApplyModifiedPropertiesWithoutUndo();
        }
        private static void SetBool(Object target, string field, bool value)
        {
            var data = new SerializedObject(target);
            data.FindProperty(field).boolValue = value;
            data.ApplyModifiedPropertiesWithoutUndo();
        }
        private static void SetFloat(Object target, string field, float value)
        {
            var data = new SerializedObject(target);
            data.FindProperty(field).floatValue = value;
            data.ApplyModifiedPropertiesWithoutUndo();
        }
        private static void SetVector(Object target, string field, Vector3 value)
        {
            var data = new SerializedObject(target);
            data.FindProperty(field).vector3Value = value;
            data.ApplyModifiedPropertiesWithoutUndo();
        }
        private static void AddBuildScenes(params string[] paths)
        {
            var scenes = EditorBuildSettings.scenes.ToList();
            foreach (string path in paths)
            {
                if (!scenes.Any(scene => scene.path == path))
                    scenes.Add(new EditorBuildSettingsScene(path, true));
            }
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
