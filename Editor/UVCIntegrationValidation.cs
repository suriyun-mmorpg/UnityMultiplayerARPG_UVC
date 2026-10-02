using System;
using System.Linq;
using System.IO;
using System.Reflection;
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MultiplayerARPG
{
    [InitializeOnLoad]
    public static class UVCIntegrationValidation
    {
        static UVCIntegrationValidation() { EditorApplication.delayCall += ProcessRequest; }

        private static void ProcessRequest()
        {
            const string request = "Temp/UVCIntegration.Validate.request";
            if (!File.Exists(request)) return;
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
            { EditorApplication.delayCall += ProcessRequest; return; }
            File.Delete(request);
            try { Validate(); File.WriteAllText("Temp/UVCIntegration.Validate.result", "SUCCESS"); }
            catch (Exception ex) { File.WriteAllText("Temp/UVCIntegration.Validate.result", ex.ToString()); Debug.LogException(ex); }
        }

        [MenuItem("Tools/MMORPG KIT/UVC Integration/Validate Integration")]
        public static void Validate()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Run validation outside Play Mode.");
            ValidateCharacterCollision();
            ValidatePlatforms();
            ValidateInput();
            ValidateControlSessions();
            ValidateCrashDamageModel();
            ValidatePredictionAndTelemetry();
            ValidateSnapshots();
            ValidateDemo();
            UVCMultiplayerValidation.Validate();
            UVCVehicleHitDamageValidation.Validate();
            if (AssetDatabase.LoadAssetAtPath<GameObject>(UVCMotorcycleDemoBuilder.PrefabPath) != null)
                UVCMultiplayerValidation.Validate(UVCMotorcycleDemoBuilder.PrefabPath);
            Debug.Log("UVC validation passed: input, ownership/timeout, prediction, telemetry, snapshots, multiplayer routing, crash damage, character hit damage, combat permissions, demo registration and scene identities.");
        }

        private static void ValidateCharacterCollision()
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                // Project collision matrices may disable Default-vs-Default contacts.
                int layer = Enumerable.Range(0, 32).First(candidate => !Physics.GetIgnoreLayerCollision(candidate, candidate));
                var car = new GameObject("Network chassis collision regression");
                car.layer = layer;
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(car, scene);
                var body = car.AddComponent<Rigidbody>();
                body.isKinematic = true;
                var shape = new GameObject("Compound chassis");
                shape.layer = layer;
                shape.transform.SetParent(car.transform, false);
                shape.transform.localPosition = Vector3.up;
                var box = shape.AddComponent<BoxCollider>();
                box.size = new Vector3(2f, 1f, 4f);
                var character = new GameObject("Stationary character");
                character.layer = layer;
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(character, scene);
                character.transform.position = Vector3.right * 4f;
                var controller = character.AddComponent<CharacterController>();
                controller.center = Vector3.up;
                controller.height = 2f;
                controller.radius = 0.5f;
                Physics.SyncTransforms();
                var collision = new UVCVehicleCharacterCollision(body);
                Vector3 corrected = collision.Constrain(Vector3.right * 8f, Quaternion.identity);
                Check(corrected.x > 2f && corrected.x <= 2.5f + controller.skinWidth + controller.contactOffset + 0.01f,
                    "A remote chassis must stop before crossing a standing character. Position=" + corrected);
                Check(character.transform.position == Vector3.right * 4f,
                    "Vehicle correction must never move the stationary character.");
                body.position = Vector3.right * 3.5f;
                Check(Physics.ComputePenetration(box, body.position + Vector3.up, Quaternion.identity,
                    controller, character.transform.position, character.transform.rotation, out _, out float initialDepth) && initialDepth > 0.5f,
                    "The collision regression must start with a penetrating chassis.");
                corrected = collision.Constrain(body.position, Quaternion.identity);
                Check(!Physics.ComputePenetration(box, corrected + Vector3.up, Quaternion.identity,
                    controller, character.transform.position, character.transform.rotation, out _, out float depth) || depth < 0.001f,
                    "Existing overlap must be resolved using the compound chassis physics pose.");
                body.position = corrected;
                Check(Vector3.Distance(collision.Constrain(corrected, Quaternion.identity), corrected) < 0.001f,
                    "An already separated car must remain stable.");
                Physics.IgnoreCollision(box, controller, true);
                Check(collision.Constrain(Vector3.right * 4f, Quaternion.identity) == Vector3.right * 4f,
                    "Ignored passenger collision pairs must remain ignored.");
                Physics.IgnoreCollision(box, controller, false);
                controller.enabled = false;
                Check(collision.Constrain(Vector3.right * 4f, Quaternion.identity) == Vector3.right * 4f,
                    "Disabled or seated character colliders must not block a car.");
                body.position = Vector3.zero;
                character.transform.position = new Vector3(0f, 1.49f, 0f);
                controller.enabled = true;
                Physics.SyncTransforms();
                Vector3 roofMotion = Vector3.forward * 0.1f;
                Check(Vector3.Distance(collision.Constrain(roofMotion, Quaternion.identity), roofMotion) < 0.001f,
                    "A character standing on the roof must not pin or push the chassis into the ground.");
                controller.enabled = false;
                character.transform.position = Vector3.right * 2f;
                controller.enabled = true;
                Physics.SyncTransforms();
                Quaternion turn = Quaternion.Euler(0f, 90f, 0f);
                corrected = collision.Constrain(Vector3.zero, turn);
                Check(corrected.x < -0.1f && (!Physics.ComputePenetration(box, corrected + Vector3.up, turn,
                    controller, character.transform.position, character.transform.rotation, out _, out depth) || depth < 0.001f),
                    "Rotating a compound chassis must not embed it in a standing character.");
                Check(body.isKinematic, "Observer collision constraints must preserve kinematic replication.");
                controller.enabled = false;
                body.isKinematic = false;
                body.useGravity = false;
                body.position = new Vector3(8192f, 1000f, 8192f);
                Vector3 slowVelocity = new Vector3(0.001f, 0f, 0.001f);
                body.velocity = slowVelocity;
                Vector3 unobstructedPosition = body.position;
                collision.ConstrainSimulation(0.05f);
                Check(body.velocity.Equals(slowVelocity) && body.position.Equals(unobstructedPosition),
                    "An unobstructed car must retain its physics velocity even below world-position precision.");
                body.velocity = new Vector3(2.31f, 0.47f, -1.19f);
                Vector3 movingVelocity = body.velocity;
                collision.ConstrainSimulation(0.05f);
                Check(body.velocity.Equals(movingVelocity) && body.position.Equals(unobstructedPosition),
                    "An unobstructed sweep must not introduce velocity or pose rounding corrections.");
                body.Sleep();
                Check(body.IsSleeping(), "The resting-body regression must start asleep.");
                collision.ConstrainSimulation(0.05f);
                Check(body.IsSleeping(), "Character collision queries must not wake an unobstructed resting car.");
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }

        private static void ValidatePlatforms()
        {
            var go = new GameObject("Platform contact regression") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                var support = go.transform;
                var platform = new EntityMovementPlatform();
                platform.RecordContact(Vector3.right, Vector3.up, support, 0.7f);
                support.position = Vector3.forward * 2f;
                Check((platform.ConsumeVelocity(true, 0.5f) - Vector3.forward * 4f).sqrMagnitude < 0.0001f,
                    "A grounded rider inherits platform translation.");
                support.position += Vector3.forward;
                Check(platform.ConsumeVelocity(true, 0.5f) == Vector3.zero,
                    "A missed contact must detach instead of dragging the character indefinitely.");
                support.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                platform.RecordContact(Vector3.right, Vector3.up, support, 0.7f);
                platform.RecordContact(Vector3.zero, Vector3.right, support, 0.7f);
                support.rotation = Quaternion.Euler(0f, 90f, 0f);
                Check((platform.ConsumeVelocity(true, 1f) - (Vector3.back - Vector3.right)).sqrMagnitude < 0.0001f,
                    "Rotation carries the support point; a later wall contact cannot erase the floor.");
                platform.RecordContact(Vector3.zero, Vector3.right, support, 0.7f);
                support.position += Vector3.up;
                Check(platform.ConsumeVelocity(true, 1f) == Vector3.zero, "Car side contacts must never attach riders.");
                platform.RecordContact(support.position, Vector3.up, support, 0.7f);
                support.position += Vector3.forward;
                Check(platform.ConsumeVelocity(false, 1f) == Vector3.zero, "Jumping or airborne riders detach.");
                platform.RecordContact(support.position, Vector3.up, support, 0.7f);
                platform.Reset();
                support.position += Vector3.forward * 100f;
                Check(platform.ConsumeVelocity(true, 1f) == Vector3.zero, "Teleport/ownership resets discard old support.");
                platform.RecordContact(support.position, Vector3.up, support, 0.7f);
                Check(platform.ConsumeVelocity(true, 0f) == Vector3.zero, "Paused movement cannot divide by zero.");
            }
            finally { Object.DestroyImmediate(go); }
        }

        private static void ValidateControlSessions()
        {
            var session = new UVCVehicleControlSession();
            session.UpdateDriver(10, 100);
            uint original = session.Generation;
            var throttle = new UVCVehicleInput { throttle = 1f, boost = true };
            Check(session.Accept(original, 1000, throttle, 10f), "Current driver input should be accepted.");
            Check(session.GetInput(10.1f, 0.5f, true).throttle == 1f, "Driver input must reach physics.");
            Check(session.GetInput(10.6f, 0.5f, true).handbrake, "Lost input must time out to parked.");
            Check(session.GetInput(10.1f, 0.5f, false).throttle == 0f, "Dead/blocked driver must not accelerate.");
            Check(session.Accept(original, 1002, UVCVehicleInput.Parked, 10.2f), "Focus/UI reset must be accepted.");
            Check(!session.Accept(original, 1001, throttle, 10.3f), "Reordered throttle must not override a reset.");
            session.ClearInput();
            Check(!session.Accept(original, 1001, throttle, 10.3f), "StopMove must preserve packet ordering.");
            session.UpdateDriver(-1, 0);
            Check(!session.Accept(original, 1003, throttle, 10.4f), "Exited driver cannot keep driving.");
            session.UpdateDriver(20, 200);
            Check(!session.Accept(original, 1004, throttle, 10.4f), "Previous ownership generation must be rejected.");
            Check(session.Accept(session.Generation, 1, throttle, 10.4f), "New driver's independent clock must be accepted.");
            session.UpdateDriver(-1, 0);
            session.UpdateDriver(10, 100);
            Check(!session.Accept(original, 1005, throttle, 10.5f), "Same driver re-entering must get a fresh generation.");
        }

        private static void ValidatePredictionAndTelemetry()
        {
            Check(UVCVehiclePrediction.CanPredict(true, false, true, true, 10, 100, 10, 100), "Current owner should predict.");
            Check(!UVCVehiclePrediction.CanPredict(true, false, false, true, 10, 100, 10, 100), "Passengers must not predict.");
            Check(!UVCVehiclePrediction.CanPredict(true, true, true, true, 10, 100, 10, 100), "Host must not simulate twice.");
            Check(!UVCVehiclePrediction.CanPredict(true, false, true, true, 20, 200, 10, 100), "Ownership handover must wait for matching snapshot.");
            Check(!UVCVehiclePrediction.CanPredict(false, false, true, true, 10, 100, 10, 100), "Prediction can be disabled.");
            Check(UVCVehiclePrediction.ExtrapolationTime(2f, 0.15f) == 0.15f, "Extrapolation must be bounded under packet loss.");
            var rotated = UVCVehiclePrediction.ExtrapolateRotation(Quaternion.identity, Vector3.up * Mathf.PI, 0.5f);
            Check(Quaternion.Angle(rotated, Quaternion.Euler(0, 90, 0)) < 0.01f, "Angular velocity uses radians per second.");
            var telemetry = new UVCVehicleTelemetry { rpm = 4500, acceleration = 0.7f, brake = 0.2f, turbo = 0.8f,
                boostAmount = 7.5f, engineLoad = 0.4f, gear = -1, engineOn = true, boosting = true, changingGear = true, handbrake = true };
            var writer = new NetDataWriter();
            telemetry.Write(writer);
            var reader = new NetDataReader(writer.CopyData());
            var read = UVCVehicleTelemetry.Read(reader);
            Check(read.rpm == telemetry.rpm && read.gear == -1 && read.turbo == telemetry.turbo &&
                read.boostAmount == telemetry.boostAmount && read.acceleration == telemetry.acceleration &&
                read.brake == telemetry.brake && read.engineLoad == telemetry.engineLoad && read.engineOn &&
                read.boosting && read.changingGear && read.handbrake && reader.AvailableBytes == 0,
                "Drivetrain/audio telemetry must roundtrip independently of local physics.");
        }

        private static void Check(bool condition, string message)
        {
            if (!condition)
                throw new InvalidOperationException("UVC validation: " + message);
        }

        private static void ValidateCrashDamageModel()
        {
            Check(UVCCrashDamageModel.Calculate(4f, 20f, 5f, 2.5f, 1000) == 0, "Slow/normal parking bumps must not damage.");
            Check(UVCCrashDamageModel.Calculate(20f, 0.2f, 5f, 2.5f, 1000) == 0, "Light props must not cause high crash damage.");
            Check(UVCCrashDamageModel.Calculate(15f, 15f, 5f, 2.5f, 1000) == 250, "A 15 m/s head-on stop should inflict 250 HP.");
            Check(UVCCrashDamageModel.Calculate(100f, 100f, 5f, 2.5f, 1000) == 1000, "Per-impact cap must bound damage.");
            Check(UVCCrashDamageModel.Calculate(float.NaN, 20f, 5f, 2.5f, 1000) == 0, "Invalid physics data must be ignored.");
            Check(UVCCrashDamageModel.GripMultiplier(0f) == 0.25f && UVCCrashDamageModel.SteeringMultiplier(0f) == 0.3f,
                "Broken wheels retain bounded grip/steering without changing geometry.");
        }

        private static void ValidateInput()
        {
            var writer = new NetDataWriter();
            new UVCVehicleInput { steering = -0.7f, throttle = 0.8f, brakeReverse = 0.3f,
                pitch = 0.2f, handbrake = true, boost = true }.Write(writer);
            var reader = new NetDataReader(writer.CopyData());
            var input = UVCVehicleInput.Read(reader);
            Check(input.throttle == 0.8f && input.brakeReverse == 0.3f && input.steering == -0.7f &&
                input.pitch == 0.2f && input.handbrake && input.boost && reader.AvailableBytes == 0,
                "Independent pedals and flags must survive serialization.");
            writer.Reset();
            // Simulate an untrusted packet bypassing the sender's sanitizer.
            writer.Put(float.NaN); writer.Put(float.PositiveInfinity); writer.Put(2f); writer.Put(-3f); writer.Put((byte)0);
            input = UVCVehicleInput.Read(new NetDataReader(writer.CopyData()));
            Check(input.steering == 0f && input.throttle == 0f && input.brakeReverse == 1f && input.pitch == -1f,
                "Received controls must be finite and clamped.");
            Check(UVCVehicleInput.Parked.handbrake && UVCVehicleInput.Parked.throttle == 0f, "Parked input must brake.");
        }

        private static void ValidateSnapshots()
        {
            var go = new GameObject("UVC snapshot validation");
            try
            {
                var body = go.AddComponent<Rigidbody>();
                body.isKinematic = true;
                var movement = go.AddComponent<UVCVehicleEntityMovement>();
                // Edit-mode components have not run Awake or joined a network session.
                SetField(movement, "<Body>k__BackingField", body);
                SetField(movement, "_wheels", Array.Empty<PG.Wheel>());
                SetField(movement, "_wheelColliders", Array.Empty<WheelCollider>());
                SetField(movement, "_wheelPositions", Array.Empty<Vector3>());
                SetField(movement, "_wheelRotations", Array.Empty<Quaternion>());
                SetField(movement, "<Car>k__BackingField", go.GetComponent<PG.CarController>());
                Quaternion rotation = Quaternion.Euler(23f, 85f, -17f);
                var first = Snapshot(0, new Vector3(1, 2, 3), rotation);
                movement.ReadServerStateAtClient(100, first);
                Check(first.AvailableBytes == 0 && Quaternion.Angle(body.rotation, rotation) < 0.01f,
                    "Full pitch/yaw/roll must survive snapshots.");
                var stale = Snapshot(0, Vector3.one * 20f, Quaternion.identity);
                movement.ReadServerStateAtClient(99, stale);
                Check(stale.AvailableBytes == 0 && body.position == new Vector3(1, 2, 3),
                    "Stale snapshots must be consumed without moving the replica.");
                var mismatch = Snapshot(0, Vector3.one * 30f, Quaternion.identity, 1);
                movement.ReadServerStateAtClient(101, mismatch);
                Check(mismatch.AvailableBytes == 0 && body.position == new Vector3(1, 2, 3),
                    "Mismatched wheel counts must not corrupt the following movement packet.");
                var visualMismatch = Snapshot(0, Vector3.one * 30f, Quaternion.identity, 0, 1);
                movement.ReadServerStateAtClient(101, visualMismatch);
                Check(visualMismatch.AvailableBytes == 0 && body.position == new Vector3(1, 2, 3),
                    "Mismatched additional visual counts must be consumed without accepting the snapshot.");
                var teleport = Snapshot(1, new Vector3(2, 2, 3), Quaternion.identity);
                movement.ReadServerStateAtClient(102, teleport);
                Check(body.position == new Vector3(2, 2, 3), "A new teleport revision must snap even over a short distance.");
            }
            finally { Object.DestroyImmediate(go); }
        }

        private static void SetField(object target, string name, object value) =>
            target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);

        private static NetDataReader Snapshot(uint revision, Vector3 position, Quaternion rotation, ushort wheels = 0, ushort visuals = 0)
        {
            var writer = new NetDataWriter();
            writer.Put(revision); writer.Put(1u); writer.Put(-1L); writer.Put(0u); writer.Put(true);
            writer.PutVector3(position); writer.PutQuaternion(rotation);
            writer.PutVector3(Vector3.zero); writer.PutVector3(Vector3.zero);
            default(UVCVehicleTelemetry).Write(writer);
            writer.Put(1f);
            writer.Put((uint)MovementState.IsGrounded); writer.Put(visuals); writer.Put(wheels);
            for (int i = 0; i < wheels; ++i)
            {
                writer.PutVector3(Vector3.zero); writer.PutQuaternion(Quaternion.identity);
                writer.Put(1f);
            }
            for (int i = 0; i < visuals; ++i)
            {
                writer.PutVector3(Vector3.zero); writer.PutQuaternion(Quaternion.identity);
            }
            return new NetDataReader(writer.CopyData());
        }

        private static void ValidateDemo()
        {
            const string root = UVCIntegrationDemoBuilder.Root + "/Demo/";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(root + "Prefabs/UVC_S34_Vehicle.prefab");
            Check(prefab != null, "Demo car prefab is missing.");
            var vehicle = prefab.GetComponent<VehicleEntity>();
            Check(prefab.GetComponent<UVCVehicleCrashDamage>() != null && vehicle.MaxHp == 1000 &&
                new SerializedObject(vehicle).FindProperty("canBeAttacked").boolValue, "Demo needs crash damage and 1000 HP.");
            foreach (var damage in prefab.GetComponentsInChildren<PG.VehicleDamageController>(true))
                Check(new SerializedObject(damage).FindProperty("DamageFactor").floatValue == 0f,
                    "Demo deformation must be disabled so predicted colliders match the server.");
            Check(prefab.GetComponent<Collider>() != null, "Kit interaction requires a collider on the entity root.");
            Check(vehicle != null && vehicle.Seats.Count == 2 && prefab.GetComponent<UVCVehicleEntityMovement>() != null &&
                prefab.GetComponent<PG.CarController>().Wheels.Length == 4, "Demo car requires two seats and four configured UVC wheels.");
            var db = AssetDatabase.LoadAssetAtPath<GameDatabase>(root + "GameData/GameDatabase_UVC.asset");
#if !EXCLUDE_PREFAB_REFS || DISABLE_ADDRESSABLES
            Check(db.vehicleEntities.Contains(vehicle), "Demo vehicle must be registered in its database.");
            Check(db.playerCharacterEntities.Length > 0 && db.playerCharacterEntities.All(entity =>
                entity != null && AssetDatabase.GetAssetPath(entity).StartsWith(root + "Prefabs/") &&
                entity.CharacterDatabases.Length > 0 && entity.CharacterDatabases.All(data => data != null && db.playerCharacters.Contains(data))),
                "Demo player entity prefabs must reference the demo classes and be registered in the database.");
#endif
            var map = AssetDatabase.LoadAssetAtPath<MapInfo>(root + "GameData/MapInfo_UVCDrivingDemo.asset");
            Check(db.mapInfos.Contains(map), "Demo map must be registered.");
            var controls = AssetDatabase.LoadAssetAtPath<GameObject>(root + "Prefabs/PlayerCharacterController_UVC.prefab");
            var mappings = new SerializedObject(controls.GetComponent<BasePlayerCharacterController>()).FindProperty("vehicleControllers");
            var motorcycle = AssetDatabase.LoadAssetAtPath<GameObject>(UVCMotorcycleDemoBuilder.PrefabPath);
            Check(mappings.arraySize >= 1 && mappings.GetArrayElementAtIndex(0).FindPropertyRelative("controllersForEachSeats").arraySize == 2,
                "Driver and passenger controller mappings are required.");
            if (motorcycle != null)
            {
                var bike = motorcycle.GetComponent<PG.BikeController>();
                var bikeEntity = motorcycle.GetComponent<VehicleEntity>();
                Check(bike != null && bike.Wheels.Length == 2 && bike.RearForkParent != null && bikeEntity.Seats.Count == 1,
                    "Motorcycle requires two wheels, an authored fork parent and one driver seat.");
                Check(bike.Bike.MaxSqrGForceForCrash == float.MaxValue && bike.Bike.MaxReverseSpeedForCrash == float.MaxValue,
                    "Motorcycle must use synchronized kit damage rather than UVC's local crash latch.");
                Check(new SerializedObject(motorcycle.GetComponent<UVCVehicleEntityMovement>()).FindProperty("_additionalVisuals").arraySize == 4,
                    "Motorcycle moving parts must be synchronized.");
                var bikeType = AssetDatabase.LoadAssetAtPath<VehicleType>(UVCMotorcycleDemoBuilder.TypePath);
                bool mapped = false;
                for (int i = 0; i < mappings.arraySize; ++i)
                {
                    var entry = mappings.GetArrayElementAtIndex(i);
                    if (entry.FindPropertyRelative("vehicleType").objectReferenceValue != bikeType) continue;
                    var seats = entry.FindPropertyRelative("controllersForEachSeats");
                    mapped = seats.arraySize == 1 && seats.GetArrayElementAtIndex(0).objectReferenceValue is UVCMotorcyclePlayerController;
                }
                Check(mapped, "Motorcycle must route driver input through its pitch-capable controller.");
#if !EXCLUDE_PREFAB_REFS || DISABLE_ADDRESSABLES
                Check(db.vehicleEntities.Contains(bikeEntity), "Motorcycle must be registered in the demo database.");
#endif
            }
            string scenePath = root + "Scenes/UVCDrivingDemo.unity";
            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(scenePath);
            bool alreadyLoaded = scene.IsValid() && scene.isLoaded;
            if (!alreadyLoaded)
                scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            try
            {
                var ids = scene.GetRootGameObjects().SelectMany(go => go.GetComponentsInChildren<LiteNetLibIdentity>(true)).ToArray();
                int expectedVehicles = motorcycle == null ? 2 : 3;
                Check(ids.Length == expectedVehicles && ids.Select(id => id.SceneObjectId).Distinct().Count() == expectedVehicles &&
                    ids.All(id => !string.IsNullOrEmpty(id.SceneObjectId)), "Demo cars require unique scene object identities.");
            }
            finally { if (!alreadyLoaded) EditorSceneManager.CloseScene(scene, true); }
        }
    }
}
