using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace MultiplayerARPG
{
    /// <summary>Server-only environmental HP damage; replicated mechanical condition never detaches wheels or deforms colliders.</summary>
    [DefaultExecutionOrder(50)] // Apply handling modifiers after UVC has set torque, steering and tire friction.
    [DisallowMultipleComponent]
    [RequireComponent(typeof(VehicleEntity), typeof(UVCVehicleEntityMovement))]
    public class UVCVehicleCrashDamage : BaseNetworkedGameEntityComponent<VehicleEntity>
    {
        [SerializeField, Min(0f)] private float _minimumImpactSpeed = 5f;
        [SerializeField, Min(0f)] private float _damagePerSpeedSquared = 2.5f;
        [SerializeField, Min(1)] private int _maximumDamagePerImpact = 1000;
        [SerializeField, Min(0f)] private float _impactCooldown = 0.2f;
        [SerializeField, Min(0f)] private float _engineDamageScale = 1.25f;
        [SerializeField, Min(0f)] private float _wheelDamageScale = 1.75f;
        [SerializeField, Min(0f)] private float _wheelDamageRadius = 1.25f;
        [SerializeField] private UnityEvent<int> _onCrashDamage = new UnityEvent<int>();

        public float EngineCondition { get; private set; } = 1f;
        public IReadOnlyList<float> WheelConditions { get { EnsureInitialized(); return _wheelConditions; } }

        private UVCVehicleEntityMovement _movement;
        private PG.CarController _car;
        private Rigidbody _body;
        private WheelCollider[] _colliders;
        private float[] _wheelConditions;
        private bool _initialized;
        private float _lastImpactTime = float.NegativeInfinity;
        private int _pendingDamage;
        private Vector3 _pendingPoint;

        private void Awake() => EnsureInitialized();

        private void EnsureInitialized()
        {
            if (_car != null) return;
            _movement = GetComponent<UVCVehicleEntityMovement>();
            _car = GetComponent<PG.CarController>();
            _body = GetComponent<Rigidbody>();
            _wheelConditions = new float[_car.Wheels.Length];
            _colliders = new WheelCollider[_car.Wheels.Length];
            for (int i = 0; i < _wheelConditions.Length; ++i)
            {
                _wheelConditions[i] = 1f;
                if (_car.Wheels[i] != null) _colliders[i] = _car.Wheels[i].GetComponent<WheelCollider>();
            }
        }

        public override void OnIdentityInitialize()
        {
            EnsureInitialized();
            _initialized = true;
            ResetCondition();
            if (IsServer)
            {
                _car.RestoreUVCNetworkEngine();
                _car.ApplyUVCNetworkBoostAmount(_car.Engine.BoostAmount);
            }
        }

        public override void OnNetworkDestroy(byte reasons)
        {
            _initialized = false;
            _pendingDamage = 0;
        }

        private void OnDisable() { _pendingDamage = 0; }

        private void ResetCondition()
        {
            EngineCondition = 1f;
            for (int i = 0; i < _wheelConditions.Length; ++i) _wheelConditions[i] = 1f;
            _pendingDamage = 0;
            _lastImpactTime = float.NegativeInfinity;
        }

        /// <summary>Call only from an authorized server repair action. Does not revive a destroyed vehicle.</summary>
        public bool Repair()
        {
            if (!_initialized || !IsServer || Entity.IsDead()) return false;
            ResetCondition();
            Entity.CurrentHp = Entity.MaxHp;
            _car.RestoreUVCNetworkEngine();
            return true;
        }

        private void OnCollisionEnter(Collision collision) => RecordCollision(collision);
        private void OnCollisionStay(Collision collision) => RecordCollision(collision);

        private void RecordCollision(Collision collision)
        {
            if (!_initialized || !IsServer || !enabled || collision.rigidbody == _body) return;
            float normalSpeed = 0f;
            Vector3 point = transform.position;
            for (int i = 0; i < collision.contactCount; ++i)
            {
                ContactPoint contact = collision.GetContact(i);
                float speed = Mathf.Abs(Vector3.Dot(collision.relativeVelocity, contact.normal));
                if (speed > normalSpeed) { normalSpeed = speed; point = contact.point; }
            }
            QueueImpact(normalSpeed, collision.impulse.magnitude / Mathf.Max(1f, _body.mass), point);
        }

        private void QueueImpact(float normalSpeed, float deltaVelocity, Vector3 point)
        {
            if (!_initialized || !IsServer || !enabled || Entity.IsDead() || Entity.IsInvincible ||
                Time.unscaledTime - _lastImpactTime < _impactCooldown) return;
            int damage = UVCCrashDamageModel.Calculate(normalSpeed, deltaVelocity, _minimumImpactSpeed,
                _damagePerSpeedSquared, _maximumDamagePerImpact);
            // One strongest impact per physics step prevents compound colliders multiplying the damage.
            if (damage <= _pendingDamage) return;
            _pendingDamage = damage;
            _pendingPoint = point;
        }

        private void ApplyPendingImpact()
        {
            int requested = _pendingDamage;
            _pendingDamage = 0;
            if (requested <= 0 || !IsServer || Entity.IsDead() || Entity.IsInvincible) return;
            int damage = Mathf.Min(requested, Entity.CurrentHp);
            float fraction = damage / (float)Mathf.Max(1, Entity.MaxHp);
            EngineCondition = Mathf.Clamp01(EngineCondition - fraction * _engineDamageScale);
            int closest = -1;
            float distance = _wheelDamageRadius * _wheelDamageRadius;
            for (int i = 0; i < _car.Wheels.Length; ++i)
            {
                if (_car.Wheels[i] == null) continue;
                float next = (_car.Wheels[i].transform.position - _pendingPoint).sqrMagnitude;
                if (next > distance) continue;
                distance = next;
                closest = i;
            }
            if (closest >= 0) _wheelConditions[closest] = Mathf.Clamp01(_wheelConditions[closest] - fraction * _wheelDamageScale);
            _lastImpactTime = Time.unscaledTime;
            Entity.CurrentHp -= damage;
            if (Entity.IsDead())
            {
                EngineCondition = 0f;
                _movement.StopMove();
                DestroyVehicle();
            }
            _onCrashDamage.Invoke(damage);
        }

        protected virtual void DestroyVehicle() => Entity.Destroy(); // Kit ejects occupants, sends destruction RPC, despawns and handles scene respawn.

        private void FixedUpdate()
        {
            if (!_initialized) return;
            if (IsServer) ApplyPendingImpact();
            if ((!IsServer && !_movement.IsPredicting) || _body.isKinematic) return;
            ApplyHandling();
        }

        private void ApplyHandling()
        {
            float engine = Entity.IsDead() ? 0f : EngineCondition;
            if (engine <= 0f) _car.StopUVCNetworkEngine();
            for (int i = 0; i < _colliders.Length; ++i)
            {
                WheelCollider wheel = _colliders[i];
                if (wheel == null || !wheel.enabled) continue;
                float condition = _wheelConditions[i];
                wheel.motorTorque *= engine * condition;
                wheel.steerAngle *= UVCCrashDamageModel.SteeringMultiplier(condition);
                // UVC rewrites stiffness each fixed step, so these factors do not accumulate.
                var forward = wheel.forwardFriction;
                forward.stiffness *= UVCCrashDamageModel.GripMultiplier(condition);
                wheel.forwardFriction = forward;
                var sideways = wheel.sidewaysFriction;
                sideways.stiffness *= UVCCrashDamageModel.GripMultiplier(condition);
                wheel.sidewaysFriction = sideways;
            }
        }

        internal float GetWheelCondition(int index)
        {
            EnsureInitialized();
            return index < _wheelConditions.Length ? _wheelConditions[index] : 1f;
        }

        internal void SetReplicatedEngineCondition(float condition)
        {
            if (!IsServer) EngineCondition = UVCCrashDamageModel.Condition(condition);
        }

        internal void SetReplicatedWheelCondition(int index, float condition)
        {
            EnsureInitialized();
            if (!IsServer && index < _wheelConditions.Length)
                _wheelConditions[index] = UVCCrashDamageModel.Condition(condition);
        }
    }
}
