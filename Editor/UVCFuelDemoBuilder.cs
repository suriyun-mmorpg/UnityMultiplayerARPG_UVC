using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MultiplayerARPG
{
    [InitializeOnLoad]
    public static class UVCFuelDemoBuilder
    {
        static UVCFuelDemoBuilder() { EditorApplication.update += ProcessRequest; }

        private static void ProcessRequest()
        {
            const string request = "Temp/UVCFuelDemo.Build.request";
            if (!File.Exists(request) || EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            File.Delete(request);
            try { Build(); File.WriteAllText("Temp/UVCFuelDemo.Build.result", "SUCCESS"); }
            catch (Exception ex) { File.WriteAllText("Temp/UVCFuelDemo.Build.result", ex.ToString()); Debug.LogException(ex); }
        }

        [MenuItem("Tools/MMORPG KIT/UVC Integration/Add Fuel Demo")]
        public static void Build()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Stop Play Mode before updating the fuel demo.");
            bool wzm = AssetDatabase.IsValidFolder("Assets/__WZM");
            string dataRoot = wzm ? "Assets/__ARM/_Data/GameData/Vehicles" : "Assets/UVCIntegration/Demo/GameData";
            string fuelPath = dataRoot + "/FuelCan_UVC.asset";
            JunkItem can = AssetDatabase.LoadAssetAtPath<JunkItem>(fuelPath);
            if (can == null)
            {
                can = ScriptableObject.CreateInstance<JunkItem>();
                can.Id = "uvc_demo_fuel_can";
                can.DefaultTitle = "Fuel can (2 L)";
                can.DefaultDescription = "Park the vehicle and use REFUEL (CAN) on its fuel HUD. The whole can must fit in the tank.";
                can.MaxStack = 20;
                can.Weight = 2f;
                AssetDatabase.CreateAsset(can, fuelPath);
            }
            string[] prefabs = wzm ? new[]
            {
                "Assets/__ARM/_Data/GameEntity/Vehicles/UVC_S34_Stock.prefab",
                "Assets/__ARM/_Data/GameEntity/Vehicles/UVC_S34_Race.prefab",
            } : new[]
            {
                "Assets/UVCIntegration/Demo/Prefabs/UVC_S34_Vehicle.prefab",
                "Assets/UVCIntegration/Demo/Prefabs/UVC_Motorcycle_Vehicle.prefab",
            };
            foreach (string path in prefabs)
            {
                GameObject root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    VehicleFuelComponent fuel = root.GetComponent<VehicleFuelComponent>();
                    if (fuel == null)
                    {
                        fuel = root.AddComponent<VehicleFuelComponent>();
                        var serialized = new SerializedObject(fuel);
                        serialized.FindProperty("_capacity").floatValue = 10f;
                        serialized.FindProperty("_startingFuel").floatValue = 2f;
                        serialized.FindProperty("_idleConsumption").floatValue = 0.12f;
                        serialized.FindProperty("_throttleConsumption").floatValue = 2.88f;
                        SerializedProperty items = serialized.FindProperty("_fuelItems");
                        items.arraySize = 1;
                        items.GetArrayElementAtIndex(0).FindPropertyRelative("item").objectReferenceValue = can;
                        items.GetArrayElementAtIndex(0).FindPropertyRelative("fuelAmount").floatValue = 2f;
                        serialized.ApplyModifiedPropertiesWithoutUndo();
                    }
                    if (root.GetComponent<UVCVehicleFuelAdapter>() == null)
                        root.AddComponent<UVCVehicleFuelAdapter>();
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            string databasePath = wzm ? "Assets/__WZM/_Data/Game Database.asset" : "Assets/UVCIntegration/Demo/GameData/GameDatabase_UVC.asset";
            GameDatabase database = AssetDatabase.LoadAssetAtPath<GameDatabase>(databasePath);
            if (database == null)
                throw new InvalidOperationException("Demo game database is missing: " + databasePath);
            if (!(database.items ?? Array.Empty<BaseItem>()).Any(item => item != null && item == can))
            {
                database.items = (database.items ?? Array.Empty<BaseItem>()).Concat(new BaseItem[] { can }).ToArray();
                EditorUtility.SetDirty(database);
            }
            foreach (PlayerCharacter character in database.playerCharacters ?? Array.Empty<PlayerCharacter>())
            {
                if (character == null || (character.StartItems ?? Array.Empty<ItemAmount>()).Any(item => item.item == can))
                    continue;
                var serialized = new SerializedObject(character);
                SerializedProperty items = serialized.FindProperty("startItems");
                int index = items.arraySize++;
                items.GetArrayElementAtIndex(index).FindPropertyRelative("item").objectReferenceValue = can;
                items.GetArrayElementAtIndex(index).FindPropertyRelative("level").intValue = 1;
                items.GetArrayElementAtIndex(index).FindPropertyRelative("amount").intValue = 3;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(character);
            }
            string oldMarkerPath = dataRoot + "/FuelStationMarker.mat";
            string markerPath = wzm ? "Assets/__ARM/_Data/GameEntity/Vehicles/FuelStationMarker.mat"
                : "Assets/UVCIntegration/Demo/Prefabs/FuelStationMarker.mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(oldMarkerPath) != null && AssetDatabase.LoadAssetAtPath<Material>(markerPath) == null)
            {
                string error = AssetDatabase.MoveAsset(oldMarkerPath, markerPath);
                if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
            }
            AddStation(wzm ? "Assets/_Scenes/999_empty.unity" : "Assets/UVCIntegration/Demo/Scenes/UVCDrivingDemo.unity", dataRoot);
            VehicleFuelUIBuilder.BuildAndInstall();
            AssetDatabase.SaveAssets();
        }

        private static void AddStation(string path, string dataRoot)
        {
            Scene scene = SceneManager.GetSceneByPath(path);
            bool alreadyLoaded = scene.IsValid() && scene.isLoaded;
            if (!alreadyLoaded)
                scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            try
            {
                if (scene.GetRootGameObjects().Any(root => root.GetComponentInChildren<UVCFuelDemoStation>(true) != null))
                    return;
                VehicleEntity vehicle = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<VehicleEntity>(true)).FirstOrDefault();
                if (vehicle == null)
                    throw new InvalidOperationException("No vehicle in demo scene: " + path);
                GameObject station = new GameObject("Fuel station (park to refill)");
                SceneManager.MoveGameObjectToScene(station, scene);
                station.transform.position = vehicle.transform.position + Vector3.right * 8f;
                station.AddComponent<UVCFuelDemoStation>();
                GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                marker.name = "Refuel area";
                marker.transform.SetParent(station.transform, false);
                marker.transform.localPosition = new Vector3(0f, -0.6f, 0f);
                marker.transform.localScale = new Vector3(6f, 0.03f, 6f);
                Object.DestroyImmediate(marker.GetComponent<Collider>());
                string materialPath = AssetDatabase.IsValidFolder("Assets/__WZM")
                    ? "Assets/__ARM/_Data/GameEntity/Vehicles/FuelStationMarker.mat"
                    : "Assets/UVCIntegration/Demo/Prefabs/FuelStationMarker.mat";
                Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material == null)
                {
                    Shader shader = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null
                        ? Shader.Find("Universal Render Pipeline/Unlit") : Shader.Find("Unlit/Color");
                    material = new Material(shader);
                    material.color = new Color(0.1f, 0.65f, 0.4f, 1f);
                    AssetDatabase.CreateAsset(material, materialPath);
                }
                marker.GetComponent<Renderer>().sharedMaterial = material;
                var label = new GameObject("Station label").AddComponent<TMPro.TextMeshPro>();
                label.transform.SetParent(station.transform, false);
                label.transform.localPosition = Vector3.up * 2f;
                label.rectTransform.sizeDelta = new Vector2(6f, 1.5f);
                label.font = TMPro.TMP_Settings.defaultFontAsset;
                label.text = "FUEL\nPARK TO REFILL";
                label.fontSize = 3f;
                label.alignment = TMPro.TextAlignmentOptions.Center;
                EditorSceneManager.SaveScene(scene);
            }
            finally { if (!alreadyLoaded) EditorSceneManager.CloseScene(scene, true); }
        }
    }
}
