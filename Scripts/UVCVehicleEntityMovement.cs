using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using LiteNetLib.Utils;
using LiteNetLibManager;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>
    /// Server-authoritative UVC adapter. Clients send controls through the kit's movement channel;
    /// the server simulates UVC and replicates body/wheel poses and drivetrain telemetry.
    /// Owning drivers optionally predict physics with bounded server reconciliation.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    [RequireComponent(typeof(Rigidbody), typeof(PG.CarController))]
    [DisallowMultipleComponent]
    public class UVCVehicleEntityMovement : BaseNetworkedGameEntityComponent<BaseGameEntity>,
        IEntityMovementComponent, IEntityMovementDataHandler, PG.ICarControl
    {
        [SerializeField, Min(0.1f)] private float _inputTimeout = 0.5f;
        [SerializeField, Min(1f)] private float _interpolationSpeed = 15f;
        [SerializeField, Min(0.1f)] private float _snapDistance = 10f;
        [SerializeField, Min(0f)] private float _stoppingDistance = 1f;
        [Header("Driver prediction")]
        [SerializeField] private bool _enablePrediction = true;
        [SerializeField, Min(0f)] private float _maxExtrapolation = 0.15f;
        [SerializeField, Min(0.1f)] private float _reconciliationSpeed = 3f;
        [SerializeField, Min(0.1f)] private float _predictionSnapDistance = 5f;
        [Tooltip("Additional moving visual parts, ordered parent before child (e.g. motorcycle forks and handlebars).")]
        [SerializeField] private Transform[] _additionalVisuals = System.Array.Empty<Transform>();

        public PG.CarController Car { get; private set; }
        public Rigidbody Body { get; private set; }
        public UVCVehicleCrashDamage CrashDamage { get; private set; }
        public VehicleFuelComponent Fuel { get; private set; }
        public VehicleHornComponent Horn { get; private set; }
        public float StoppingDistance => _stoppingDistance;
        public MovementState MovementState { get; private set; }
        public ExtraMovementState ExtraMovementState => ExtraMovementState.None;
        public DirectionVector2 Direction2D { get; set; }
        public float CurrentMoveSpeed => IsServer || _predicting ? Body.velocity.magnitude : _serverVelocity.magnitude;
        public bool IsPredicting => _predicting;
        public UVCVehicleTelemetry Telemetry => IsServer || _predicting ? UVCVehicleTelemetry.Capture(Car) : _telemetry;
        public bool CanUseEnginePower => (Fuel == null || !Fuel.IsEmpty) &&
            (CrashDamage == null || !CrashDamage.enabled || CrashDamage.EngineCondition > 0f);
        public float Acceleration => CanUseEnginePower ? _simulationInput.throttle : 0f;
        public float BrakeReverse => Car is PG.BikeController && Car.CurrentGear < 0 &&
            !CanUseEnginePower ? 0f : _simulationInput.brakeReverse;
        public float ServiceBrake => Car.Gearbox.AutomaticGearBox && Car.CurrentGear < 0
            ? _simulationInput.throttle : _simulationInput.brakeReverse;
        public float Horizontal => _simulationInput.steering;
        public float Pitch => _simulationInput.pitch;
        public bool HandBrake => _simulationInput.handbrake;
        public bool Boost => _simulationInput.boost && CanUseEnginePower;

        private UVCVehicleInput _localInput = UVCVehicleInput.Parked;
        private UVCVehicleInput _simulationInput = UVCVehicleInput.Parked;
        private readonly UVCVehicleControlSession _controls = new UVCVehicleControlSession();
        private PG.Wheel[] _wheels;
        private WheelCollider[] _wheelColliders;
        private Vector3[] _wheelPositions;
        private Quaternion[] _wheelRotations;
        private Vector3[] _visualPositions;
        private Quaternion[] _visualRotations;
        private Vector3 _serverPosition;
        private Quaternion _serverRotation;
        private Vector3 _serverVelocity;
        private Vector3 _serverAngularVelocity;
        private UVCVehicleTelemetry _telemetry;
        private float _snapshotTime;
        private float _snapshotTransitTime;
        private bool _predicting;
        private bool _simulating;
        private UVCVehicleCharacterCollision _characterCollision;
        private RigidbodyInterpolation _simulationInterpolation;
        private bool _serverSimulationEnabled;
        private long _snapshotOwnerId = long.MinValue;
        private uint _snapshotDriverId;
        private uint _receivedGeneration;
        private bool _initialized;
        private bool _hasSnapshot;
        private long _snapshotTimestamp = long.MinValue;
        private float _lastLocalInputTime = float.NegativeInfinity;
        private uint _teleportRevision;
        private uint _receivedTeleportRevision;
        private readonly List<EntityMovementForceApplier> _forces = new List<EntityMovementForceApplier>();

        private bool HasDriver => Entity is IVehicleEntity vehicle && vehicle.HasDriver;
        private uint DriverId => HasDriver ? ((IVehicleEntity)Entity).GetPassenger(0).ObjectId : 0;
        private bool IsLocalDriver => IsOwnerClient && HasDriver && ((IVehicleEntity)Entity).GetPassenger(0).IsOwnerClient;
        private bool MatchesSnapshotDriver => _hasSnapshot && ConnectionId == _snapshotOwnerId && DriverId == _snapshotDriverId;
        private bool CanDriveNow => HasDriver && Entity.CanMove() && !(Entity is IDamageableEntity damageable && damageable.IsDead());

        private void Awake()
        {
            Car = GetComponent<PG.CarController>();
            Body = GetComponent<Rigidbody>();
            _characterCollision = new UVCVehicleCharacterCollision(Body);
            _simulationInterpolation = Body.interpolation;
            CrashDamage = GetComponent<UVCVehicleCrashDamage>();
            Fuel = GetComponent<VehicleFuelComponent>();
            Horn = GetComponent<VehicleHornComponent>();
            _wheels = Car.Wheels;
            _wheelColliders = GetComponentsInChildren<WheelCollider>(true);
            _wheelPositions = new Vector3[_wheels.Length];
            _wheelRotations = new Quaternion[_wheels.Length];
            _visualPositions = new Vector3[_additionalVisuals.Length];
            _visualRotations = new Quaternion[_additionalVisuals.Length];
            // Vendor player/AI inputs must not compete with the network adapter.
            foreach (MonoBehaviour component in GetComponents<MonoBehaviour>())
            {
                if (component != this && component is PG.ICarControl)
                    component.enabled = false;
            }
            Car.CarControl = this;
            LiteNetLibTransform legacyTransform = GetComponent<LiteNetLibTransform>();
            if (legacyTransform != null)
                legacyTransform.enabled = false;
            SetSimulation(false);
        }

        private void OnEnable()
        {
            if (Body != null)
                RefreshSimulation();
        }

        private void OnDisable()
        {
            ResetControls();
            if (IsServer) _controls.Invalidate();
            _forces.Clear();
            if (Body != null)
                SetSimulation(false);
        }

        public override void OnIdentityInitialize()
        {
            _initialized = true;
            _hasSnapshot = false;
            _snapshotTimestamp = long.MinValue;
            ResetControls();
            _forces.Clear();
            CurrentGameManager.EntityMovementDataHandlers[ObjectId] = this;
            _controls.Invalidate();
            RefreshInputOwner();
            SetSimulation(IsServer && enabled);
        }

        public override void OnNetworkDestroy(byte reasons)
        {
            CurrentGameManager.EntityMovementDataHandlers.TryRemove(ObjectId, out _);
            _initialized = false;
            _hasSnapshot = false;
            ResetControls();
            _forces.Clear();
            SetSimulation(false);
        }

        public override void OnSetOwnerClient(bool isOwnerClient)
        {
            ResetControls();
            if (IsServer) _controls.Invalidate();
            if (Car != null)
            {
                Car.IsPlayerVehicle = isOwnerClient;
                Car.ResetUVCNetworkTransientState();
                RefreshSimulation();
            }
        }

        private void ResetControls()
        {
            _localInput = _simulationInput = UVCVehicleInput.Parked;
            _lastLocalInputTime = float.NegativeInfinity;
            _controls.ClearInput();
            Horn?.ResetLocalInput();
            if (IsServer) Horn?.ServerSetHorn(false);
        }

        private void RefreshSimulation()
        {
            bool predict = _initialized && enabled && UVCVehiclePrediction.CanPredict(_enablePrediction && _serverSimulationEnabled,
                IsServer, IsLocalDriver, _hasSnapshot, ConnectionId, DriverId, _snapshotOwnerId, _snapshotDriverId);
            if (predict != _predicting)
            {
                _predicting = predict;
                Car.ResetUVCNetworkTransientState();
                // Start each owner from a known server pose; old local forces never survive a handover.
                if (_hasSnapshot && !IsServer)
                {
                    Body.position = _serverPosition;
                    Body.rotation = _serverRotation;
                    Car.ApplyUVCNetworkTelemetry(_telemetry, 1f, false);
                }
            }
            bool simulate = _initialized && enabled && (IsServer || predict);
            if (simulate != _simulating)
            {
                SetSimulation(simulate);
                if (predict)
                {
                    Body.velocity = _serverVelocity;
                    Body.angularVelocity = _serverAngularVelocity;
                }
            }
        }

        private void SetSimulation(bool simulate)
        {
            _simulating = simulate;
            if (!simulate) _predicting = false;
            if (!simulate && !Body.isKinematic)
            {
                Body.velocity = Vector3.zero;
                Body.angularVelocity = Vector3.zero;
            }
            Body.isKinematic = !simulate;
            // Remote poses already have network smoothing; PhysX interpolation would present a different support pose.
            Body.interpolation = simulate ? _simulationInterpolation : RigidbodyInterpolation.None;
            Car.IsLocalVehicle = simulate;
            Car.enabled = simulate;
            foreach (PG.Wheel wheel in _wheels)
            {
                if (wheel != null)
                    wheel.enabled = simulate;
            }
            foreach (WheelCollider wheel in _wheelColliders)
                wheel.enabled = simulate;
            // Disabling the colliders recreates the PhysX vehicle and discards Wheel.Awake's substeps.
            // Restore UVC's configuration after all wheels are enabled (once per vehicle).
            if (simulate && _wheelColliders.Length > 0)
                _wheelColliders[0].ConfigureVehicleSubsteps(40f, 100, 20);
        }

        private void RefreshInputOwner()
        {
            if (IsServer && _controls.UpdateDriver(ConnectionId, DriverId))
            {
                ResetControls();
            }
        }

        private void FixedUpdate()
        {
            if (!_initialized)
                return;
            RefreshSimulation();
            if (!IsServer && !_predicting)
                return;
            RefreshInputOwner();
            _simulationInput = IsServer ? _controls.GetInput(Time.unscaledTime, _inputTimeout, CanDriveNow)
                : CanDriveNow && Time.unscaledTime - _lastLocalInputTime <= _inputTimeout ? _localInput : UVCVehicleInput.Parked;
            if (IsServer) Horn?.ServerSetHorn(_simulationInput.horn);
            if (_predicting)
                ReconcilePrediction();
            MovementState = Car.VehicleIsGrounded ? MovementState.IsGrounded : MovementState.None;
            if (Body.velocity.sqrMagnitude > 0.01f)
                MovementState |= Vector3.Dot(Body.velocity, transform.forward) < 0f ? MovementState.Backward : MovementState.Forward;
            _forces.UpdateForces(Time.fixedDeltaTime, 0f, out Vector3 forceVelocity, out EntityMovementForceApplier replacement);
            if (replacement != null)
                Body.velocity = replacement.Velocity + forceVelocity;
            else if (forceVelocity.sqrMagnitude > 0f)
                Body.AddForce(forceVelocity * Time.fixedDeltaTime, ForceMode.VelocityChange);
            _characterCollision.ConstrainSimulation(Time.fixedDeltaTime);
        }

        private void Update()
        {
            if (!_initialized || IsServer || !_hasSnapshot)
                return;
            RefreshSimulation();
            if (_predicting)
                return;
            UpdateRemoteMovement(Time.deltaTime);
        }

        private void UpdateRemoteMovement(float deltaTime)
        {
            float factor = 1f - Mathf.Exp(-_interpolationSpeed * deltaTime);
            Car.ApplyUVCNetworkTelemetry(_telemetry, factor, true);
            // Advance the chassis before character movement so supporting contacts use this frame's pose.
            // Match the driver's bounded server-time estimate instead of chasing an old packet position.
            float lead = _serverSimulationEnabled
                ? UVCVehiclePrediction.ExtrapolationTime(Time.unscaledTime - _snapshotTime + _snapshotTransitTime, _maxExtrapolation)
                : 0f;
            Vector3 target = _serverPosition + _serverVelocity * lead;
            Quaternion rotation = UVCVehiclePrediction.ExtrapolateRotation(_serverRotation, _serverAngularVelocity, lead);
            Quaternion nextRotation = Quaternion.Slerp(Body.rotation, rotation, factor);
            Body.position = _characterCollision.Constrain(Vector3.Lerp(Body.position, target, factor), nextRotation);
            Body.rotation = nextRotation;
        }

        private void LateUpdate()
        {
            if (_initialized && !IsServer && _hasSnapshot && !_predicting)
                ApplyRemoteVisuals();
        }

        private void ApplyRemoteVisuals()
        {
            // Parent fork poses must be applied before the wheel views they carry.
            for (int i = 0; i < _additionalVisuals.Length; ++i)
            {
                if (_additionalVisuals[i] == null) continue;
                _additionalVisuals[i].SetPositionAndRotation(transform.TransformPoint(_visualPositions[i]),
                    transform.rotation * _visualRotations[i]);
            }
            for (int i = 0; i < _wheels.Length; ++i)
            {
                if (_wheels[i] == null || _wheels[i].WheelView == null)
                    continue;
                Transform view = _wheels[i].WheelView;
                view.SetPositionAndRotation(transform.TransformPoint(_wheelPositions[i]), transform.rotation * _wheelRotations[i]);
            }
        }

        public void SetInput(UVCVehicleInput input)
        {
            if (!_initialized || !enabled || !IsLocalDriver)
                return;
            RefreshInputOwner();
            _localInput = input.Sanitize();
            Horn?.SetLocalPresentation(_localInput.horn);
            _lastLocalInputTime = Time.unscaledTime;
            if (IsServer)
                _controls.SetLocal(_localInput, Time.unscaledTime);
        }

        public bool WriteClientState(long writeTimestamp, NetDataWriter writer, out bool shouldSendReliably)
        {
            shouldSendReliably = false;
            if (!_initialized || !IsLocalDriver || !MatchesSnapshotDriver)
                return false;
            writer.Put(_receivedGeneration);
            (enabled && Time.unscaledTime - _lastLocalInputTime <= _inputTimeout ? _localInput : UVCVehicleInput.Parked).Write(writer);
            return true;
        }

        public void ReadClientStateAtServer(long peerTimestamp, NetDataReader reader)
        {
            uint generation = reader.GetUInt();
            UVCVehicleInput input = UVCVehicleInput.Read(reader);
            if (!IsServer || !enabled || !HasDriver)
                return;
            RefreshInputOwner();
            // BaseGameNetworkManager already validates the packet's connection against this owner.
            if (((IVehicleEntity)Entity).GetPassenger(0).ConnectionId != ConnectionId)
                return;
            _controls.Accept(generation, peerTimestamp, input, Time.unscaledTime);
        }

        public bool WriteServerState(long writeTimestamp, NetDataWriter writer, out bool shouldSendReliably)
        {
            shouldSendReliably = false;
            if (!_initialized || !IsServer)
                return false;
            RefreshInputOwner();
            writer.Put(_teleportRevision);
            writer.Put(_controls.Generation);
            writer.Put(_controls.OwnerId);
            writer.Put(_controls.DriverId);
            writer.Put(enabled);
            writer.PutVector3(Body.position);
            writer.PutQuaternion(Body.rotation);
            writer.PutVector3(Body.velocity);
            writer.PutVector3(Body.angularVelocity);
            UVCVehicleTelemetry.Capture(Car).Write(writer);
            bool hasDamage = CrashDamage != null && CrashDamage.enabled;
            writer.Put(hasDamage ? CrashDamage.EngineCondition : 1f);
            writer.Put((uint)MovementState);
            writer.Put((ushort)_additionalVisuals.Length);
            writer.Put((ushort)_wheels.Length);
            for (int i = 0; i < _wheels.Length; ++i)
            {
                PG.Wheel wheel = _wheels[i];
                Transform view = wheel != null ? wheel.WheelView : null;
                writer.PutVector3(view != null ? transform.InverseTransformPoint(view.position) : Vector3.zero);
                writer.PutQuaternion(view != null ? Quaternion.Inverse(transform.rotation) * view.rotation : Quaternion.identity);
                writer.Put(hasDamage ? CrashDamage.GetWheelCondition(i) : 1f);
            }
            foreach (Transform visual in _additionalVisuals)
            {
                writer.PutVector3(visual != null ? transform.InverseTransformPoint(visual.position) : Vector3.zero);
                writer.PutQuaternion(visual != null ? Quaternion.Inverse(transform.rotation) * visual.rotation : Quaternion.identity);
            }
            return true;
        }

        public void ReadServerStateAtClient(long peerTimestamp, NetDataReader reader)
        {
            uint revision = reader.GetUInt();
            uint generation = reader.GetUInt();
            long ownerId = reader.GetLong();
            uint driverId = reader.GetUInt();
            bool serverSimulationEnabled = reader.GetBool();
            Vector3 position = reader.GetVector3();
            Quaternion rotation = reader.GetQuaternion();
            Vector3 velocity = reader.GetVector3();
            Vector3 angularVelocity = reader.GetVector3();
            UVCVehicleTelemetry telemetry = UVCVehicleTelemetry.Read(reader);
            float engineCondition = reader.GetFloat();
            MovementState movementState = (MovementState)reader.GetUInt();
            int visualCount = reader.GetUShort();
            int wheelCount = reader.GetUShort();
            bool accept = !IsServer && peerTimestamp > _snapshotTimestamp && wheelCount == _wheels.Length && visualCount == _additionalVisuals.Length;
            for (int i = 0; i < wheelCount; ++i)
            {
                Vector3 wheelPosition = reader.GetVector3();
                Quaternion wheelRotation = reader.GetQuaternion();
                float wheelCondition = reader.GetFloat();
                if (accept)
                {
                    _wheelPositions[i] = wheelPosition;
                    _wheelRotations[i] = wheelRotation;
                    if (CrashDamage != null) CrashDamage.SetReplicatedWheelCondition(i, wheelCondition);
                }
            }
            for (int i = 0; i < visualCount; ++i)
            {
                Vector3 visualPosition = reader.GetVector3();
                Quaternion visualRotation = reader.GetQuaternion();
                if (!accept) continue;
                _visualPositions[i] = visualPosition;
                _visualRotations[i] = visualRotation;
            }
            if (!accept)
                return;
            if (CrashDamage != null) CrashDamage.SetReplicatedEngineCondition(engineCondition);
            _snapshotTimestamp = peerTimestamp;
            bool newSession = !_hasSnapshot || generation != _receivedGeneration;
            bool teleport = !_hasSnapshot || revision != _receivedTeleportRevision;
            if (newSession)
                ResetControls();
            if (teleport || newSession || Vector3.Distance(Body.position, position) > (_predicting ? _predictionSnapDistance : _snapDistance))
            {
                Body.position = position;
                Body.rotation = rotation;
                if (!Body.isKinematic)
                {
                    Body.velocity = velocity;
                    Body.angularVelocity = angularVelocity;
                }
                Car.ResetUVCNetworkTransientState();
            }
            _receivedTeleportRevision = revision;
            _receivedGeneration = generation;
            _snapshotOwnerId = ownerId;
            _snapshotDriverId = driverId;
            _serverSimulationEnabled = serverSimulationEnabled;
            _serverPosition = position;
            _serverRotation = rotation;
            _serverVelocity = velocity;
            _serverAngularVelocity = angularVelocity;
            _telemetry = telemetry;
            if (!_predicting)
                _simulationInput = new UVCVehicleInput { throttle = telemetry.acceleration, brakeReverse = telemetry.brake,
                    handbrake = telemetry.handbrake, boost = telemetry.boosting };
            _snapshotTime = Time.unscaledTime;
            _snapshotTransitTime = Entity != null && CurrentGameManager != null ? Mathf.Min(_maxExtrapolation, CurrentGameManager.Rtt * 0.0005f) : 0f;
            MovementState = movementState;
            _hasSnapshot = true;
            if (_predicting && (teleport || newSession))
                Car.ApplyUVCNetworkTelemetry(telemetry, 1f, false);
        }

        private void ReconcilePrediction()
        {
            float age = Time.unscaledTime - _snapshotTime;
            // A disconnected owner must not keep predicting indefinitely or keep the throttle held.
            if (age > _inputTimeout)
            {
                _simulationInput = UVCVehicleInput.Parked;
                return;
            }
            float lead = UVCVehiclePrediction.ExtrapolationTime(age + _snapshotTransitTime, _maxExtrapolation);
            Vector3 target = _serverPosition + _serverVelocity * lead;
            Quaternion rotation = UVCVehiclePrediction.ExtrapolateRotation(_serverRotation, _serverAngularVelocity, lead);
            float blend = 1f - Mathf.Exp(-_reconciliationSpeed * Time.fixedDeltaTime);
            if (Vector3.Distance(Body.position, target) > _predictionSnapDistance)
                blend = 1f;
            Quaternion nextRotation = Quaternion.Slerp(Body.rotation, rotation, blend);
            Body.position = _characterCollision.Constrain(Vector3.Lerp(Body.position, target, blend), nextRotation);
            Body.rotation = nextRotation;
            Body.velocity = Vector3.Lerp(Body.velocity, _serverVelocity, blend);
            Body.angularVelocity = Vector3.Lerp(Body.angularVelocity, _serverAngularVelocity, blend);
            // Keep local engine response, but the server owns consumable boost fuel.
            Car.ApplyUVCNetworkBoostAmount(_telemetry.boostAmount);
        }

        public Bounds GetMovementBounds()
        {
            Bounds bounds = new Bounds(transform.position, Vector3.zero);
            foreach (Collider collider in GetComponentsInChildren<Collider>())
            {
                if (!collider.isTrigger && !(collider is WheelCollider))
                    bounds.Encapsulate(collider.bounds);
            }
            return bounds;
        }

        public void StopMove()
        {
            ResetControls();
        }

        // Generic locomotion commands do not steer a car or rotate its physical body.
        // UVCVehiclePlayerController exclusively supplies SetInput instead.
        public void KeyMovement(Vector3 moveDirection, MovementState moveState) { }
        public void PointClickMovement(Vector3 position) { }
        public void SetExtraMovementState(ExtraMovementState state) { }
        public void SetLookRotation(Quaternion rotation, bool immediately) { }
        public Quaternion GetLookRotation() => Body.rotation;
        public void SetSmoothTurnSpeed(float speed) { }
        public float GetSmoothTurnSpeed() => 0f;

        public void Teleport(Vector3 position, Quaternion rotation, bool stillMoveAfterTeleport)
        {
            if (!IsServer)
                return;
            Body.position = position;
            Body.rotation = rotation;
            GetComponent<UVCVehicleHitDamage>()?.ResetSweepHistory();
            if (!stillMoveAfterTeleport)
            {
                StopMove();
                Body.velocity = Vector3.zero;
                Body.angularVelocity = Vector3.zero;
                _forces.Clear();
            }
            ++_teleportRevision;
        }

        public bool FindGroundedPosition(Vector3 fromPosition, float findDistance, out Vector3 result)
        {
            result = fromPosition;
            return false; // Keep the requested chassis height; snapping its origin buries the wheels.
        }

        public void ApplyForce(ApplyMovementForceMode mode, Vector3 direction, ApplyMovementForceSourceType sourceType,
            int sourceDataId, int sourceLevel, float force, float deceleration, float duration, bool clearForces)
        {
            if (!IsServer)
                return;
            if (clearForces)
                _forces.Clear();
            _forces.Add(new EntityMovementForceApplier().Apply(mode, direction, sourceType, sourceDataId, sourceLevel, force, deceleration, duration));
        }

        public EntityMovementForceApplier FindForceByActionKey(ApplyMovementForceSourceType sourceType, int sourceDataId) => _forces.FindBySource(sourceType, sourceDataId);
        public void ClearAllForces() { if (IsServer) _forces.Clear(); }
        public bool AllowToJump() => false;
        public bool AllowToDash() => false;
        public bool AllowToCrouch() => false;
        public bool AllowToCrawl() => false;
        public bool AllowToStand() => true;
        // Teleport revisions are server authoritative; clients apply them from movement snapshots.
        public UniTask WaitClientTeleportConfirm() => UniTask.CompletedTask;
        public bool IsWaitingClientTeleportConfirm() => false;
    }
}
