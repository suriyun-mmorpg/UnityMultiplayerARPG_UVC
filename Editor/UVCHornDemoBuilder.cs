using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace MultiplayerARPG
{
    [InitializeOnLoad]
    public static class UVCHornDemoBuilder
    {
        static UVCHornDemoBuilder() { EditorApplication.update += ProcessRequest; }
        private static void ProcessRequest()
        {
            const string request = "Temp/UVCHornDemo.Build.request";
            if (!File.Exists(request) || EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode) return;
            File.Delete(request);
            try { Build(); File.WriteAllText("Temp/UVCHornDemo.Build.result", "SUCCESS"); }
            catch (Exception ex) { File.WriteAllText("Temp/UVCHornDemo.Build.result", ex.ToString()); Debug.LogException(ex); }
        }

        [MenuItem("Tools/MMORPG KIT/UVC Integration/Add Horn Demo")]
        public static void Build()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Stop Play Mode before updating the horn demo.");
            bool wzm = AssetDatabase.IsValidFolder("Assets/__WZM");
            string clipPath = wzm ? "Assets/__ARM/_Data/GameData/Vehicles/Horn_UVC.wav" : "Assets/UVCIntegration/Demo/GameData/Horn_UVC.wav";
            if (!File.Exists(clipPath))
            {
                WriteDemoClip(clipPath);
                AssetDatabase.ImportAsset(clipPath, ImportAssetOptions.ForceSynchronousImport);
            }
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(clipPath);
            if (clip == null) throw new InvalidOperationException("Horn clip could not be imported.");
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
                var root = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    var horn = root.GetComponent<VehicleHornComponent>();
                    if (horn != null && horn.HornAudioSource != null) continue;
                    if (horn == null) horn = root.AddComponent<VehicleHornComponent>();
                    var audioObject = new GameObject("Horn Audio");
                    audioObject.transform.SetParent(root.transform, false);
                    var audio = audioObject.AddComponent<AudioSource>();
                    audio.playOnAwake = false;
                    audio.loop = true;
                    audio.clip = clip;
                    audio.volume = 0.65f;
                    audio.spatialBlend = 1f;
                    audio.minDistance = 5f;
                    audio.maxDistance = 60f;
                    audio.rolloffMode = AudioRolloffMode.Logarithmic;
                    audio.pitch = root.GetComponent<PG.BikeController>() != null ? 1.3f : 1f;
                    var routedAudio = root.GetComponentsInChildren<AudioSource>(true).FirstOrDefault(source => source.outputAudioMixerGroup != null);
                    if (routedAudio != null) audio.outputAudioMixerGroup = routedAudio.outputAudioMixerGroup;
                    var serialized = new SerializedObject(horn);
                    serialized.FindProperty("_audioSource").objectReferenceValue = audio;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                }
                finally { PrefabUtility.UnloadPrefabContents(root); }
            }
            VehicleHornUIBuilder.BuildAndInstall();
            AssetDatabase.SaveAssets();
        }

        // Original seamless half-second dual-tone sample, with an integral number of cycles.
        private static void WriteDemoClip(string path)
        {
            const int rate = 22050, samples = rate / 2, bytes = samples * sizeof(short);
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + bytes);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
                writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2);
                writer.Write((short)2); writer.Write((short)16);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(bytes);
                for (int i = 0; i < samples; ++i)
                {
                    double t = (double)i / rate;
                    double value = 0.28 * Math.Sin(2 * Math.PI * 350 * t) + 0.28 * Math.Sin(2 * Math.PI * 440 * t) + 0.06 * Math.Sin(2 * Math.PI * 700 * t);
                    writer.Write((short)(value * short.MaxValue));
                }
            }
        }
    }
}
