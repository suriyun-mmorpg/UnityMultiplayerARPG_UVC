using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MultiplayerARPG
{
    /// <summary>Vehicle-only collision queries, including replicas whose controller is disabled.</summary>
    public sealed class UVCVehicleCharacterCollision
    {
        private readonly Rigidbody _body;
        private readonly Collider[] _chassis;
        private Collider[] _overlaps = new Collider[32];
        private const float ContactMargin = 0.005f;

        public UVCVehicleCharacterCollision(Rigidbody body)
        {
            _body = body;
            var chassis = new List<Collider>();
            foreach (Collider collider in body.GetComponentsInChildren<Collider>())
                if (collider.attachedRigidbody == body && !collider.isTrigger && !(collider is WheelCollider))
                    chassis.Add(collider);
            _chassis = chassis.ToArray();
        }

        private bool Blocks(Collider chassis, Collider character, bool replicasOnly)
        {
            if (!chassis.enabled || character == null || !character.enabled ||
                character.transform == _body.transform ||
                Physics.GetIgnoreLayerCollision(chassis.gameObject.layer, character.gameObject.layer) ||
                Physics.GetIgnoreCollision(chassis, character)) return false;
            if (character is CharacterController controller)
                return !replicasOnly && controller.detectCollisions;
            // Only the kit's root movement capsule stands in for a disabled controller.
            // Interaction and damage triggers must never become vehicle obstacles.
            if (!(character is CapsuleCollider)) return false;
            var movement = character.GetComponent<CharacterControllerEntityMovement>();
            if (movement == null || !movement.isActiveAndEnabled || movement.CacheCapsuleCollider != character ||
                movement.CacheCharacterController.enabled || !movement.CacheCharacterController.detectCollisions ||
                Physics.GetIgnoreCollision(chassis, movement.CacheCharacterController)) return false;
            var entity = character.GetComponent<BaseGameEntity>();
            return entity != null && entity.PassengingVehicleEntity == null;
        }

        public Vector3 Constrain(Vector3 target, Quaternion rotation, bool replicasOnly = false)
        {
            Vector3 motion = target - _body.position;
            float distance = motion.magnitude;
            if (distance > 0.0001f)
            {
                // Sweep the current chassis, so extrapolation cannot cross a character between snapshots.
                float allowed = distance;
                foreach (RaycastHit hit in _body.SweepTestAll(motion / distance, distance, QueryTriggerInteraction.Collide))
                {
                    var character = hit.collider;
                    if (character == null || hit.normal.y < -0.5f) continue;
                    foreach (Collider chassis in _chassis)
                    {
                        if (!Blocks(chassis, character, replicasOnly)) continue;
                        allowed = Mathf.Min(allowed, Mathf.Max(0f, hit.distance - ContactMargin));
                        break;
                    }
                }
                target = _body.position + motion / distance * allowed;
            }

            // Sweeps do not resolve initial penetration or the change in chassis orientation.
            // Use physics poses rather than interpolated Rigidbody transforms for compound shapes.
            Quaternion inverseBody = Quaternion.Inverse(_body.transform.rotation);
            PhysicsScene physics = _body.gameObject.scene.GetPhysicsScene();
            for (int iteration = 0; iteration < 4; ++iteration)
            {
                bool corrected = false;
                foreach (Collider chassis in _chassis)
                {
                    if (!chassis.enabled) continue;
                    Vector3 offset = inverseBody * (chassis.transform.position - _body.transform.position);
                    Quaternion localRotation = inverseBody * chassis.transform.rotation;
                    // A bounding sphere around the body covers any rotation of this shape.
                    float radius = Vector3.Distance(chassis.bounds.center, _body.transform.position) + chassis.bounds.extents.magnitude;
                    int count;
                    while (true)
                    {
                        count = physics.OverlapSphere(target, radius, _overlaps, Physics.AllLayers, QueryTriggerInteraction.Collide);
                        if (count < _overlaps.Length || _overlaps.Length >= 1024) break;
                        System.Array.Resize(ref _overlaps, _overlaps.Length * 2);
                    }
                    for (int i = 0; i < count; ++i)
                    {
                        var character = _overlaps[i];
                        if (!Blocks(chassis, character, replicasOnly)) continue;
                        if (!Physics.ComputePenetration(chassis, target + rotation * offset, rotation * localRotation,
                            character, character.transform.position, character.transform.rotation,
                            out Vector3 normal, out float penetration)) continue;
                        // A rider on the roof is a support contact, not an obstacle to horizontal driving.
                        if (normal.y < -0.5f) continue;
                        target += normal * (penetration + ContactMargin);
                        corrected = true;
                    }
                }
                if (!corrected) break;
            }
            return target;
        }

        public void ConstrainSimulation(float deltaTime)
        {
            if (_body.isKinematic || deltaTime <= 0f) return;
            Vector3 position = Constrain(_body.position, _body.rotation, true);
            _body.position = position;
            Vector3 travel = _body.velocity * deltaTime;
            Vector3 next = Constrain(position + travel, _body.rotation, true);
            // The replica capsule is a trigger, so PhysX cannot stop the car against it.
            // Limit car velocity only; never move or enable the replicated character controller.
            _body.velocity += (next - position - travel) / deltaTime;
        }
    }
}
