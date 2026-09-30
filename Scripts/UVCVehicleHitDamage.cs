using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace MultiplayerARPG
{
    /// <summary>Server-authoritative character impacts, independent of vehicle self-damage.</summary>
    [DefaultExecutionOrder(-40)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(VehicleEntity), typeof(UVCVehicleEntityMovement))]
    public class UVCVehicleHitDamage : BaseNetworkedGameEntityComponent<VehicleEntity>
    {
        [Header("Chassis hit volume (local space)")]
        [SerializeField] private Vector3 _center = new Vector3(0f, 0.7f, 0f);
        [SerializeField] private Vector3 _size = new Vector3(1.8f, 1.2f, 4.4f);
        [SerializeField] private LayerMask _targetLayers = ~0;
        [Header("Damage before combat mitigation")]
        [SerializeField, Min(0f)] private float _minimumImpactSpeed = 3f;
        [SerializeField, Min(0f)] private float _damagePerSpeedSquared = 2f;
        [SerializeField, Min(1)] private int _maximumDamage = 250;
        [SerializeField, Min(0.02f)] private float _targetCooldown = 0.75f;
        [SerializeField] private DamageElement _damageElement;
        [Tooltip("Optional movement force after a successful non-lethal hit. Zero disables knockback.")]
        [SerializeField, Min(0f)] private float _knockbackSpeed;

        private readonly Dictionary<BaseCharacterEntity, float> _nextHits = new Dictionary<BaseCharacterEntity, float>();
        private readonly List<BaseCharacterEntity> _expired = new List<BaseCharacterEntity>();
        private Collider[] _overlaps = new Collider[32];
        private RaycastHit[] _sweepHits = new RaycastHit[32];
        private Rigidbody _body;
        private bool _initialized;
        private bool _hasHistory;
        private Vector3 _previousCenter, _previousVelocity;
        private Quaternion _previousRotation;
        private BaseCharacterEntity _previousDriver;

        public override void OnIdentityInitialize()
        {
            _body = GetComponent<Rigidbody>();
            _initialized = true;
            _nextHits.Clear();
            ResetSweepHistory();
        }
        public override void OnNetworkDestroy(byte reasons) { _initialized = false; _nextHits.Clear(); ResetSweepHistory(); }
        private void OnDisable() { _nextHits.Clear(); ResetSweepHistory(); }
        public void ResetSweepHistory() => _hasHistory = false;

        private bool TryGetDriver(out BaseCharacterEntity driver)
        {
            driver = null;
            if (!_initialized || !IsServer || !isActiveAndEnabled || Entity.IsDead() || Entity.IsInSafeArea || !Entity.HasDriver)
                return false;
            driver = Entity.GetPassenger(0) as BaseCharacterEntity;
            return driver != null && !driver.IsDead() && !driver.IsInSafeArea;
        }

        private void FixedUpdate()
        {
            if (!TryGetDriver(out BaseCharacterEntity driver)) { ResetSweepHistory(); return; }
            if (_previousDriver != driver) ResetSweepHistory();
            _previousDriver = driver;
            PruneCooldowns();
            Vector3 center = transform.TransformPoint(_center);
            Vector3 scale = transform.lossyScale;
            Vector3 half = Vector3.Scale(_size, new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z))) * 0.5f;
            Quaternion rotation = transform.rotation;
            Vector3 velocity = _body.velocity;
            Vector3 travel = center - _previousCenter;
            // Skip discontinuities from external teleports as well as explicit adapter teleports.
            bool continuous = _hasHistory && travel.magnitude <= _previousVelocity.magnitude * Time.fixedDeltaTime + 1f;
            if (continuous && _previousVelocity.magnitude > _minimumImpactSpeed && travel.sqrMagnitude > 0.000001f &&
                Quaternion.Angle(rotation, _previousRotation) < 45f)
            {
                int count;
                do
                {
                    count = Physics.BoxCastNonAlloc(_previousCenter, half, travel.normalized, _sweepHits,
                        _previousRotation, travel.magnitude, _targetLayers, QueryTriggerInteraction.Collide);
                    if (count < _sweepHits.Length) break;
                    System.Array.Resize(ref _sweepHits, _sweepHits.Length * 2);
                } while (true);
                for (int i = 0; i < count; ++i)
                {
                    // Initial overlaps have no usable surface normal; handled by the overlap query.
                    if (_sweepHits[i].distance <= 0f) continue;
                    TryHit(_sweepHits[i].collider, _previousVelocity, -_sweepHits[i].normal);
                }
            }
            if (velocity.magnitude > _minimumImpactSpeed && (!_hasHistory || continuous))
            {
                int count;
                do
                {
                    count = Physics.OverlapBoxNonAlloc(center, half, _overlaps, rotation, _targetLayers, QueryTriggerInteraction.Collide);
                    if (count < _overlaps.Length) break;
                    System.Array.Resize(ref _overlaps, _overlaps.Length * 2);
                } while (true);
                for (int i = 0; i < count; ++i)
                    TryHit(_overlaps[i], velocity, (_overlaps[i].bounds.center - center).normalized);
            }
            _previousCenter = center;
            _previousRotation = rotation;
            _previousVelocity = velocity;
            _hasHistory = true;
        }

        private void OnCollisionEnter(Collision collision) => HitCollision(collision);
        private void OnCollisionStay(Collision collision) => HitCollision(collision);
        private void HitCollision(Collision collision)
        {
            if (!TryGetDriver(out BaseCharacterEntity driver) || driver != _previousDriver || !_hasHistory ||
                _previousVelocity.magnitude <= _minimumImpactSpeed || collision.contactCount == 0) return;
            // relativeVelocity is measured before the solver stops the car.
            float speed = 0f;
            Vector3 direction = Vector3.zero;
            for (int i = 0; i < collision.contactCount; ++i)
            {
                Vector3 normal = collision.GetContact(i).normal;
                float candidate = Mathf.Abs(Vector3.Dot(collision.relativeVelocity, normal));
                if (candidate <= speed) continue;
                speed = candidate;
                direction = -normal;
            }
            TryHit(collision.collider, direction * speed, direction, true);
        }

        private void TryHit(Collider collider, Vector3 velocity, Vector3 direction, bool relativeVelocity = false)
        {
            if (collider == null || !TryGetDriver(out BaseCharacterEntity driver) ||
                (_targetLayers.value & (1 << collider.gameObject.layer)) == 0) return;
            // Only actual damage hitboxes or a character's root controller/body, never nearby interaction triggers.
            var hitBox = collider.GetComponent<DamageableHitBox>();
            var target = hitBox != null ? hitBox.DamageableEntity as BaseCharacterEntity :
                (!collider.isTrigger ? collider.GetComponent<BaseCharacterEntity>() : null);
            if (target == null || !target.IsServer || target == driver || target.IsDead() || target.IsInvincible ||
                target.IsHitBoxesOverridedByVehicle()) return;
            if (Entity.GetAllPassengers().Contains(target)) return;
            EntityInfo instigator = driver.GetInfo();
            if (!target.CanReceiveDamageFrom(instigator)) return;
            if (_nextHits.TryGetValue(target, out float next) && Time.unscaledTime < next) return;
            Vector3 relative = relativeVelocity ? velocity : velocity - TargetVelocity(target);
            float speed = Mathf.Max(0f, Vector3.Dot(relative, direction.normalized));
            int damage = UVCCrashDamageModel.Calculate(speed, speed, _minimumImpactSpeed, _damagePerSpeedSquared, _maximumDamage);
            if (damage <= 0) return;
            _nextHits[target] = Time.unscaledTime + Mathf.Max(Time.fixedDeltaTime, _targetCooldown);
            var amounts = new DamageElementMinMaxFloatAmounts();
            int slot = _damageElement == null ? 0 : RuntimeGameDataSlots.GetSlot(_damageElement);
            amounts[slot] = new MinMaxFloat { min = damage, max = damage };
            int beforeHp = target.CurrentHp;
            ApplyHit(target, instigator, amounts);
            if (_knockbackSpeed > 0f && target.CurrentHp < beforeHp && !target.IsDead())
                target.ApplyForce(ApplyMovementForceMode.Default, direction.normalized, ApplyMovementForceSourceType.None,
                    0, 0, _knockbackSpeed, _knockbackSpeed * 4f, 0.25f, false);
        }

        protected virtual void ApplyHit(BaseCharacterEntity target, EntityInfo driver, DamageElementMinMaxFloatAmounts amounts)
        {
            // Body damage avoids arbitrary headshot multipliers and collider-order-dependent damage.
            // ApplyDamage drives armor, hit/miss/block, combat text, aggro, kill credit and death handling.
            target.ApplyDamage(HitBoxPosition.Body, transform.position, driver, amounts, default, null, 0,
                Random.Range(0, int.MaxValue));
        }

        private static Vector3 TargetVelocity(BaseCharacterEntity target)
        {
            if (target.TryGetComponent(out CharacterController controller) && controller.enabled) return controller.velocity;
            if (target.TryGetComponent(out NavMeshAgent agent) && agent.enabled && agent.isOnNavMesh) return agent.velocity;
            if (target.TryGetComponent(out Rigidbody body)) return body.velocity;
            return Vector3.zero;
        }

        private void PruneCooldowns()
        {
            _expired.Clear();
            foreach (var pair in _nextHits)
                if (pair.Key == null || pair.Value <= Time.unscaledTime) _expired.Add(pair.Key);
            foreach (var target in _expired) _nextHits.Remove(target);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = Color.red;
            Gizmos.DrawWireCube(_center, _size);
        }
    }
}
