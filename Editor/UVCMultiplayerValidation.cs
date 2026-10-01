using System;
using System.Collections.Generic;
using System.Reflection;
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MultiplayerARPG
{
    /// <summary>Isolated edit-mode server + two client replicas using real adapter packet methods.
    /// Packets cross localhost UDP sockets; this does not simulate live gameplay/PhysX.</summary>
    public static class UVCMultiplayerValidation
    {
        private sealed class Peer
        {
            public LiteNetLibGameManager manager;
            public VehicleEntity vehicle;
            public UVCVehicleEntityMovement movement;
            public UVCVehicleCrashDamage damage;
            public BaseGameEntity driverA, driverB;
        }

        public static void Validate(string prefabPath = null)
        {
            var previous = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                using var network = new UVCValidationTransport();
                SceneManager.SetActiveScene(scene);
                var server = CreatePeer(true, -1, scene, prefabPath);
                var clientA = CreatePeer(false, 10, scene, prefabPath);
                var clientB = CreatePeer(false, 20, scene, prefabPath);
                Seat(server, 10); Seat(clientA, 10); Seat(clientB, 10);
                ValidatePlatformReplication(server, clientB, network);
                ValidateNotSecureRelay(server, clientB, network);
                var serverVisuals = (Transform[])Field(server.movement, "_additionalVisuals").GetValue(server.movement);
                var remoteVisuals = (Transform[])Field(clientB.movement, "_additionalVisuals").GetValue(clientB.movement);
                foreach (var visual in serverVisuals)
                    visual.localRotation *= Quaternion.Euler(3f, 12f, 0f);
                server.movement.Car.ApplyUVCNetworkTelemetry(new UVCVehicleTelemetry
                    { rpm = 4500, turbo = 0.6f, boostAmount = 8, gear = 2, engineOn = true, boosting = true }, 1f, false);
                Snapshot(server, clientA, 100, network, 1);
                Snapshot(server, clientB, 100, network, 2);
                Invoke(clientA.movement, "RefreshSimulation");
                Invoke(clientB.movement, "RefreshSimulation");
                Check(clientA.movement.IsPredicting && !clientA.movement.Body.isKinematic, "Driver A should predict locally.");
                Check(!clientB.movement.IsPredicting && clientB.movement.Body.isKinematic, "Passenger B must stay kinematic.");
                Check(clientB.movement.Body.interpolation == RigidbodyInterpolation.None,
                    "Observers must not apply PhysX interpolation on top of network smoothing.");
                Check(clientA.movement.Body.interpolation == (RigidbodyInterpolation)Field(clientA.movement, "_simulationInterpolation").GetValue(clientA.movement),
                    "Prediction must restore the authored physics interpolation mode.");
                // Exercise observer presentation with delayed packets and a long packet-loss gap.
                Vector3 snapshotPosition = clientB.movement.Body.position;
                Field(clientB.movement, "_serverVelocity").SetValue(clientB.movement, Vector3.forward * 20f);
                Field(clientB.movement, "_snapshotTransitTime").SetValue(clientB.movement, 0.05f);
                Field(clientB.movement, "_snapshotTime").SetValue(clientB.movement, Time.unscaledTime - 0.05f);
                typeof(UVCVehicleEntityMovement).GetMethod("UpdateRemoteMovement", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(clientB.movement, new object[] { 1f });
                Check(clientB.movement.Body.position.z > snapshotPosition.z + 1.5f,
                    "Observers must account for snapshot age and transit time instead of following old positions.");
                Field(clientB.movement, "_snapshotTime").SetValue(clientB.movement, Time.unscaledTime - 10f);
                typeof(UVCVehicleEntityMovement).GetMethod("UpdateRemoteMovement", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(clientB.movement, new object[] { 1f });
                Check(clientB.movement.Body.position.z <= snapshotPosition.z + 3.001f,
                    "Packet loss must bound observer extrapolation to 150 milliseconds.");
                Snapshot(server, clientB, 101, network, 2);
                Invoke(clientB.movement, "LateUpdate");
                Invoke(clientB.movement, "ApplyRemoteVisuals");
                for (int i = 0; i < serverVisuals.Length; ++i)
                    Check(Quaternion.Angle(Quaternion.Inverse(server.movement.transform.rotation) * serverVisuals[i].rotation,
                        Quaternion.Inverse(clientB.movement.transform.rotation) * remoteVisuals[i].rotation) < 0.01f,
                        "Motorcycle fork and handlebar poses must replicate to observers.");
                // Apply exactly once at full blend to verify the vendor audio inputs, independent of editor deltaTime.
                clientB.movement.Car.ApplyUVCNetworkTelemetry(clientB.movement.Telemetry, 1f, false);
                Check(clientB.movement.Car.EngineRPM == 4500 && clientB.movement.Car.CurrentGear == 2 &&
                    clientB.movement.Car.InBoost && clientB.movement.Car.CurrentTurbo == 0.6f,
                    "Passenger's vendor engine/audio state must match server telemetry.");

                var writer = new NetDataWriter();
                var input = new UVCVehicleInput { throttle = 1, boost = true };
                clientB.movement.SetInput(input);
                Check(!clientB.movement.WriteClientState(101, writer, out _), "Passenger input must not be sent.");
                clientA.movement.SetInput(input);
                Invoke(clientA.movement, "FixedUpdate");
                Check(clientA.movement.Acceleration == 1 && clientA.movement.Boost,
                    "Owner prediction must apply controls before a server roundtrip.");
                Check(clientA.movement.WriteClientState(101, writer, out _), "Driver input should be sent.");
                byte[] oldPacket = writer.CopyData();
                server.movement.ReadClientStateAtServer(101, new NetDataReader(network.Transfer(1, 0, oldPacket)));
                Check(Session(server).GetInput(Time.unscaledTime, 0.5f, true).throttle == 1, "Driver input must reach server.");
                Invoke(server.movement, "FixedUpdate");
                Check(server.movement.Acceleration == 1, "Server physics must receive accepted driver controls.");
                server.vehicle.CurrentHp = 0;
                Invoke(server.movement, "FixedUpdate");
                Check(server.movement.HandBrake && server.movement.Acceleration == 0, "Dead vehicles must reject acceleration.");
                server.vehicle.CurrentHp = 1;

                // Exercise the actual vehicle player-controller focus/reset hook.
                var playerControllerObject = new GameObject("UVC focus validation");
                var playerController = playerControllerObject.AddComponent<PlayerCharacterController>();
                var seatController = playerControllerObject.AddComponent<UVCVehiclePlayerController>();
                Property(seatController, "PlayerController", playerController);
                Property(seatController, "Vehicle", clientA.vehicle);
                Property(seatController, "SeatIndex", (byte)0);
                Field(seatController, "_movement").SetValue(seatController, clientA.movement);
                seatController.SetThrottle(1f);
                seatController.SetBoost(true);
                seatController.SetPitch(1f);
                typeof(BaseVehiclePlayerController).GetMethod("OnApplicationFocus", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(seatController, new object[] { false });
                Check(((UVCVehicleInput)Field(seatController, "_mobileInput").GetValue(seatController)).throttle == 0f,
                    "Focus loss must clear held mobile controls.");
                Check(((UVCVehicleInput)Field(seatController, "_mobileInput").GetValue(seatController)).pitch == 0f,
                    "Focus loss must clear motorcycle pitch controls.");
                Property(seatController, "PlayerController", null);
                writer.Reset();
                clientA.movement.WriteClientState(102, writer, out _);
                server.movement.ReadClientStateAtServer(102, new NetDataReader(network.Transfer(1, 0, writer.CopyData())));
                server.movement.ReadClientStateAtServer(101, new NetDataReader(network.Transfer(1, 0, oldPacket)));
                Check(Session(server).GetInput(Time.unscaledTime, 0.5f, true).handbrake,
                    "Reordered throttle after a reset must not restart the car.");

                // Exit, then give the passenger the driver seat.
                Seat(server, -1); Seat(clientA, -1); Seat(clientB, -1);
                Snapshot(server, clientA, 103, network, 1); Snapshot(server, clientB, 103, network, 2);
                Invoke(clientA.movement, "RefreshSimulation");
                Check(!clientA.movement.IsPredicting && clientA.movement.Body.isKinematic, "Exiting must stop local simulation.");
                Seat(server, 20); Seat(clientA, 20); Seat(clientB, 20);
                Snapshot(server, clientA, 104, network, 1); Snapshot(server, clientB, 104, network, 2);
                Invoke(clientB.movement, "RefreshSimulation");
                Check(clientB.movement.IsPredicting, "Driver B must take over prediction.");
                server.movement.ReadClientStateAtServer(999, new NetDataReader(network.Transfer(1, 0, oldPacket)));
                Check(Session(server).GetInput(Time.unscaledTime, 0.5f, true).throttle == 0,
                    "Old driver packet must be rejected even with a newer timestamp.");
                writer.Reset();
                clientB.movement.SetInput(input);
                Check(clientB.movement.WriteClientState(1, writer, out _), "New driver's clock starts independently.");
                server.movement.ReadClientStateAtServer(1, new NetDataReader(network.Transfer(2, 0, writer.CopyData())));
                Check(Session(server).GetInput(Time.unscaledTime, 0.5f, true).throttle == 1, "New driver input must be accepted.");

                server.movement.Teleport(new Vector3(1, 3, 2), Quaternion.Euler(10, 20, 30), false);
                Snapshot(server, clientB, 105, network, 2);
                Check(Vector3.Distance(clientB.movement.Body.position, new Vector3(1, 3, 2)) < 0.001f,
                    "Predicted owner must snap to the server teleport.");
                ValidateCrashDamage(server, clientA, clientB, network);
                server.movement.enabled = false;
                Invoke(server.movement, "OnDisable");
                Snapshot(server, clientB, 120, network, 2);
                Invoke(clientB.movement, "RefreshSimulation");
                Check(!clientB.movement.IsPredicting, "Disabling the server movement must stop owner prediction.");
                clientB.movement.enabled = false;
                Invoke(clientB.movement, "OnDisable");
                Check(clientB.movement.Body.isKinematic && !clientB.movement.IsPredicting, "Disabling must stop prediction.");
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static void ValidateCrashDamage(Peer server, Peer clientA, Peer clientB, UVCValidationTransport network)
        {
            server.vehicle.CurrentHp = 1000;
            Vector3 hit = server.movement.Car.Wheels[0].transform.position;
            QueueCrash(server.damage, 15f, 15f, hit);
            QueueCrash(server.damage, 15f, 15f, hit);
            Invoke(server.damage, "ApplyPendingImpact");
            Check(server.vehicle.CurrentHp == 750, "Compound contacts must apply one crash, not double HP damage.");
            QueueCrash(server.damage, 15f, 15f, hit);
            Invoke(server.damage, "ApplyPendingImpact");
            Check(server.vehicle.CurrentHp == 750, "Contact cooldown must prevent repeated scrape damage.");
            Snapshot(server, clientA, 110, network, 1); Snapshot(server, clientB, 110, network, 2);
            Check(clientA.damage.EngineCondition == server.damage.EngineCondition && clientB.damage.EngineCondition == server.damage.EngineCondition &&
                clientA.damage.WheelConditions[0] == server.damage.WheelConditions[0] && clientB.damage.WheelConditions[0] == server.damage.WheelConditions[0],
                "Mechanical damage must replicate to both the observer and predicting owner.");
            Check(server.damage.WheelConditions[0] < 1f && server.damage.WheelConditions[1] == 1f,
                "Only the nearest wheel within impact radius should be damaged.");

            var serverWheel = server.movement.Car.Wheels[0].GetComponent<WheelCollider>();
            var clientWheel = clientB.movement.Car.Wheels[0].GetComponent<WheelCollider>();
            serverWheel.motorTorque = clientWheel.motorTorque = 100f;
            serverWheel.steerAngle = clientWheel.steerAngle = 20f;
            Invoke(server.damage, "ApplyHandling"); Invoke(clientB.damage, "ApplyHandling");
            Check(serverWheel.motorTorque < 100f && Mathf.Approximately(serverWheel.motorTorque, clientWheel.motorTorque) &&
                Mathf.Approximately(serverWheel.steerAngle, clientWheel.steerAngle), "Prediction and server must use identical damage modifiers.");
            float hp = clientB.vehicle.CurrentHp;
            QueueCrash(clientB.damage, 100f, 100f, hit);
            Invoke(clientB.damage, "ApplyPendingImpact");
            Check(clientB.vehicle.CurrentHp == hp && !clientB.damage.Repair(), "Client collisions/repair must not change authoritative health.");

            server.vehicle.IsInvincible = true;
            Field(server.damage, "_lastImpactTime").SetValue(server.damage, float.NegativeInfinity);
            QueueCrash(server.damage, 100f, 100f, hit);
            Invoke(server.damage, "ApplyPendingImpact");
            Check(server.vehicle.CurrentHp == 750, "Crash damage must respect invincibility.");
            server.vehicle.IsInvincible = false;
            Check(server.damage.Repair() && server.vehicle.CurrentHp == 1000 && server.damage.EngineCondition == 1f,
                "Authorized server repair must restore HP and mechanical condition.");
            Snapshot(server, clientA, 111, network, 1); Snapshot(server, clientB, 111, network, 2);
            Check(clientA.damage.WheelConditions[0] == 1f && clientB.damage.EngineCondition == 1f, "Replicas must receive repairs.");

            Object.DestroyImmediate(server.damage);
            var probe = server.vehicle.gameObject.AddComponent<UVCCrashDamageValidationProbe>();
            server.damage = probe;
            Property(server.movement, "CrashDamage", probe);
            probe.OnIdentityInitialize();
            QueueCrash(probe, 100f, 100f, hit);
            Invoke(probe, "ApplyPendingImpact");
            Check(server.vehicle.CurrentHp == 0 && probe.EngineCondition == 0f && probe.DestroyRequested,
                "Lethal crashes must request the kit's vehicle destruction path exactly at zero HP.");
            Check(!probe.Repair(), "Repair must not resurrect destroyed vehicles.");
            server.vehicle.CurrentHp = 1000;
            probe.OnIdentityInitialize();
            Check(probe.EngineCondition == 1f && probe.WheelConditions[0] == 1f, "Respawn must clear old damage.");
        }

        private sealed class PlatformRiderMovement : IBuiltInEntityMovement3D
        {
            private readonly Transform _transform;
            public PlatformRiderMovement(Transform transform) { _transform = transform; }
            public bool isActiveAndEnabled => true;
            public bool GroundCheck() => true;
            public bool AirborneCheck() => false;
            public void SetPosition(Vector3 position) => _transform.position = position;
            public void Move(MovementState state, ExtraMovementState extra, Vector3 motion, float deltaTime) => _transform.position += motion;
            public void RotateY(float yAngle) => _transform.rotation = Quaternion.Euler(0f, yAngle, 0f);
            public void OnJumpForceApplied(float verticalVelocity) { }
            public Bounds GetMovementBounds() => new Bounds(_transform.position, Vector3.one);
            public Vector3 GetSnapToGroundMotion(Vector3 motion, Vector3 platformMotion, Vector3 forceMotion) => Vector3.zero;
        }

        private static void ValidatePlatformReplication(Peer server, Peer observer, UVCValidationTransport network)
        {
            // Use actual character movement serialization/interpolation against differently placed vehicle replicas.
            foreach (Peer peer in new[] { server, observer })
            {
                Field(peer.manager, "_logicUpdater").SetValue(peer.manager, new LogicUpdater(0.05));
                if (peer.manager.Assets == null)
                    Property(peer.manager, "Assets", peer.manager.GetComponent<LiteNetLibAssets>());
                ((Dictionary<uint, LiteNetLibIdentity>)Field(peer.manager.Assets, "SpawnedObjects").GetValue(peer.manager.Assets))
                    [peer.vehicle.ObjectId] = peer.vehicle.Identity;
                Property(peer.vehicle, "Movement", peer.movement);
                Property(peer.driverA, "Movement", peer.movement);
            }
            var receiver = new BuiltInEntityMovementFunctions3D(observer.driverA, null,
                new PlatformRiderMovement(observer.driverA.transform));
            var relay = new BuiltInEntityMovementFunctions3D(server.driverA, null,
                new PlatformRiderMovement(server.driverA.transform));
            relay.EntityStart();
            Field(relay, "_isServerWaitingTeleportConfirm").SetValue(relay, false);
            Vector3 point = server.vehicle.transform.InverseTransformPoint(server.movement.GetMovementBounds().center);
            var state = new EntityMovementPlatformState { objectId = server.vehicle.ObjectId, localPosition = point };
            var writer = new NetDataWriter();
            // Owning character's world position is deliberately far from the observer car.
            writer.PutPackedUInt((uint)MovementState.IsGrounded);
            writer.Put((byte)ExtraMovementState.None);
            writer.PutVector3(new Vector3(100f, 10f, 100f));
            writer.PutPackedInt(0);
            state.Write(writer);
            var reader = new NetDataReader(network.Transfer(1, 0, writer.CopyData()));
            relay.ReadClientStateAtServer(200, reader);
            Check(reader.AvailableBytes == 0, "Server consumes platform data from owner packets.");
            writer.Reset();
            Check(relay.WriteServerState(200, writer, out _), "Server relays the rider snapshot.");
            reader = new NetDataReader(network.Transfer(0, 2, writer.CopyData()));
            receiver.ReadServerStateAtClient(200, reader);
            Check(reader.AvailableBytes == 0, "Observer consumes complete rider packet.");
            observer.movement.Body.position += new Vector3(8f, 0f, -12f);
            observer.movement.Body.rotation = Quaternion.Euler(0f, 70f, 0f);
            receiver.UpdateInterpolate(1f);
            Check(Vector3.Distance(observer.driverA.transform.position, observer.vehicle.transform.TransformPoint(point)) < 0.001f,
                "A remote rider stays on the displayed vehicle despite world-position delay and rotation.");
            // No new rider packets: a moving vehicle must still carry the replica.
            observer.movement.Body.position += Vector3.forward * 5f;
            receiver.UpdateInterpolate(0.02f);
            Check(Vector3.Distance(observer.driverA.transform.position, observer.vehicle.transform.TransformPoint(point)) < 0.001f,
                "Rider follows vehicle between character snapshots.");
            // A stale detach cannot replace the latest attachment.
            writer.Reset();
            writer.PutPackedUInt((uint)MovementState.None); writer.Put((byte)ExtraMovementState.None);
            writer.PutVector3(Vector3.zero); writer.PutPackedInt(0);
            writer.PutList(new List<EntityMovementForceApplier>());
            default(EntityMovementPlatformState).Write(writer);
            receiver.ReadServerStateAtClient(199, new NetDataReader(writer.CopyData()));
            receiver.UpdateInterpolate(1f);
            Check(Vector3.Distance(observer.driverA.transform.position, observer.vehicle.transform.TransformPoint(point)) < 0.001f,
                "Out-of-order detach must not remove support.");
            receiver.ReadServerStateAtClient(201, new NetDataReader(writer.CopyData()));
            receiver.UpdateInterpolate(1f);
            observer.movement.Body.position += Vector3.forward * 5f;
            receiver.UpdateInterpolate(1f);
            Check(observer.driverA.transform.position == Vector3.zero, "A current detach returns to world interpolation.");
            state.localPosition = Vector3.one * 10000f;
            Check(!state.TryResolve(observer.manager, out _, true), "Reject out-of-bounds support claims.");
            state.localPosition = new Vector3(float.NaN, 0f, 0f);
            Check(!state.TryResolve(observer.manager, out _), "Reject nonfinite support claims.");
            // Network sends may happen after the vehicle advances, but before its rider's next Update.
            // Sample the relative pose when movement finishes, rather than against that newer vehicle pose.
            observer.driverA.transform.position = observer.vehicle.transform.TransformPoint(point);
            receiver.OnControllerColliderHit(observer.driverA.transform.position, Vector3.up, observer.vehicle.transform, 0.7f);
            receiver.AfterMovementUpdate(0.02f);
            observer.movement.Body.position += Vector3.right * 4f;
            observer.movement.Body.rotation *= Quaternion.Euler(0f, 30f, 0f);
            var captured = (EntityMovementPlatformState)typeof(BuiltInEntityMovementFunctions3D)
                .GetMethod("GetPlatformState", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(receiver, null);
            Check(captured.objectId == observer.vehicle.ObjectId && Vector3.Distance(captured.localPosition, point) < 0.001f,
                "Vehicle movement between character update and packet write must not change the rider's local offset.");
            receiver.ResetPlatform();
            captured = (EntityMovementPlatformState)typeof(BuiltInEntityMovementFunctions3D)
                .GetMethod("GetPlatformState", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(receiver, null);
            Check(captured.objectId == 0, "Resetting support clears the captured local pose.");
            observer.movement.Body.position = server.movement.Body.position;
            observer.movement.Body.rotation = server.movement.Body.rotation;
        }

        private static void AllowValidationMovement(BaseGameEntity entity, ref bool allowed) => allowed = true;

        private static void ValidateNotSecureRelay(Peer server, Peer observer, UVCValidationTransport network)
        {
            server.driverA.onCanMoveValidated += AllowValidationMovement;
            try
            {
                foreach (bool host in new[] { true, false })
                {
                    Property(server.manager, "IsClient", host);
                    Property(server.manager, "ClientConnectionId", host ? 30L : -1L);
                    server.driverA.transform.position = Vector3.zero;
                    var relay = new BuiltInEntityMovementFunctions3D(server.driverA, null,
                        new PlatformRiderMovement(server.driverA.transform));
                    relay.EntityStart();
                    var receiver = new BuiltInEntityMovementFunctions3D(observer.driverA, null,
                        new PlatformRiderMovement(observer.driverA.transform));
                    foreach (Vector3 target in new[] { Vector3.right * 2f, new Vector3(4f, -3f, 1f) })
                    {
                        long timestamp = target.x == 2f ? 300 : 350;
                        var input = new NetDataWriter();
                        input.PutPackedUInt((uint)MovementState.None); input.Put((byte)ExtraMovementState.None);
                        input.PutVector3(target); input.PutPackedInt(EntityMovementFunctions.GetCompressedAngle(75f));
                        default(EntityMovementPlatformState).Write(input);
                        relay.ReadClientStateAtServer(timestamp, new NetDataReader(network.Transfer(1, 0, input.CopyData())));
                        relay.UpdateInterpolate(0.01f); // Send while the host is only part way through presentation smoothing.
                        Vector3 presentation = server.driverA.transform.position;
                        var output = new NetDataWriter();
                        Check(relay.WriteServerState(timestamp, output, out _), "Remote NotSecure snapshot exists.");
                        var payload = network.Transfer(0, 2, output.CopyData());
                        var read = new NetDataReader(payload);
                        read.ClientReadSyncTransformMessage3D(out _, out _, out Vector3 received, out float yaw, out _);
                        var support = EntityMovementPlatformState.Read(read);
                        Check(Vector3.Distance(received, target) < 0.001f,
                            "NotSecure relay must transmit the accepted owner position, not the server's smoothed presentation (host=" + host + ").");
                        Check(Mathf.Abs(Mathf.DeltaAngle(yaw, 75f)) < 1f, "Relay preserves accepted owner rotation.");
                        Check(support.objectId == 0 && read.AvailableBytes == 0, "World snapshots retain the existing packet layout.");
                        receiver.ReadServerStateAtClient(timestamp, new NetDataReader(payload));
                        receiver.UpdateInterpolate(1f);
                        Check(Vector3.Distance(observer.driverA.transform.position, target) < 0.001f,
                            "Other clients converge to the owner pose, including falling after leaving a vehicle.");
                        if (host)
                            Check(Vector3.Distance(presentation, target) > 0.01f, "Test must exercise a host pose still being smoothed.");
                        else
                            Check(Vector3.Distance(presentation, target) < 0.001f, "Dedicated server must not rewind accepted positions through presentation smoothing.");
                    }
                }
            }
            finally
            {
                server.driverA.onCanMoveValidated -= AllowValidationMovement;
                Property(server.manager, "IsClient", false);
                Property(server.manager, "ClientConnectionId", -1L);
            }
        }

        private static void QueueCrash(UVCVehicleCrashDamage damage, float speed, float deltaVelocity, Vector3 point) =>
            typeof(UVCVehicleCrashDamage).GetMethod("QueueImpact", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(damage, new object[] { speed, deltaVelocity, point });

        private static Peer CreatePeer(bool server, long clientId, Scene scene, string prefabPath)
        {
            var peer = new Peer();
            peer.manager = new GameObject("UVC validation peer").AddComponent<LiteNetLibGameManager>();
            Property(peer.manager, "IsServer", server);
            Property(peer.manager, "IsClient", !server);
            Property(peer.manager, "ClientConnectionId", clientId);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath ?? UVCIntegrationDemoBuilder.Root + "/Demo/Prefabs/UVC_S34_Vehicle.prefab");
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            peer.vehicle = go.GetComponent<VehicleEntity>();
            peer.vehicle.CurrentHp = 1;
            peer.movement = go.GetComponent<UVCVehicleEntityMovement>();
            Identity(peer.vehicle.Identity, peer.manager, 50, -1);
            Invoke(peer.movement, "Awake");
            Field(peer.movement, "_initialized").SetValue(peer.movement, true);
            peer.damage = go.GetComponent<UVCVehicleCrashDamage>();
            peer.damage.OnIdentityInitialize();
            peer.driverA = new GameObject("driver A").AddComponent<VehicleEntity>();
            peer.driverB = new GameObject("driver B").AddComponent<VehicleEntity>();
            Identity(peer.driverA.Identity, peer.manager, 100, 10);
            Identity(peer.driverB.Identity, peer.manager, 200, 20);
            return peer;
        }

        private static void Seat(Peer peer, long owner)
        {
            var passengers = (Dictionary<byte, BaseGameEntity>)Field(peer.vehicle, "_passengers").GetValue(peer.vehicle);
            passengers.Clear();
            if (owner >= 0)
            {
                passengers[0] = owner == 10 ? peer.driverA : peer.driverB;
                passengers[1] = owner == 10 ? peer.driverB : peer.driverA;
            }
            Property(peer.vehicle.Identity, "ConnectionId", owner);
            peer.movement.OnSetOwnerClient(owner >= 0 && peer.manager.ClientConnectionId == owner);
        }

        private static UVCVehicleControlSession Session(Peer peer) => (UVCVehicleControlSession)Field(peer.movement, "_controls").GetValue(peer.movement);
        private static void Snapshot(Peer server, Peer client, long timestamp, UVCValidationTransport network, int clientIndex)
        {
            var writer = new NetDataWriter();
            Check(server.movement.WriteServerState(timestamp, writer, out _), "Server snapshot missing.");
            var reader = new NetDataReader(network.Transfer(0, clientIndex, writer.CopyData()));
            client.movement.ReadServerStateAtClient(timestamp, reader);
            Check(reader.AvailableBytes == 0, "Snapshot must consume its complete packet.");
        }
        private static void Identity(LiteNetLibIdentity identity, LiteNetLibGameManager manager, uint id, long owner)
        {
            Property(identity, "Manager", manager); Property(identity, "ObjectId", id); Property(identity, "ConnectionId", owner);
        }
        private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("UVC multiplayer validation: " + message); }
        private static FieldInfo Field(object target, string name)
        {
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null) return field;
            }
            throw new MissingFieldException(target.GetType().Name, name);
        }
        private static void Property(object target, string name, object value)
        {
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (property == null) continue;
                property.SetValue(target, value);
                return;
            }
            throw new MissingMemberException(target.GetType().Name, name);
        }
        private static void Invoke(object target, string name)
        {
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                var method = type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (method == null) continue;
                method.Invoke(target, null);
                return;
            }
            throw new MissingMethodException(target.GetType().Name, name);
        }
    }
}
