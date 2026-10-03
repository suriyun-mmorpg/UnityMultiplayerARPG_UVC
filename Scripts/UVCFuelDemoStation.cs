using System.Collections.Generic;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>Free demo pump. A production station can charge through the same server fuel API.</summary>
    public class UVCFuelDemoStation : MonoBehaviour
    {
        [SerializeField, Min(0.1f)] private float _radius = 3f;
        [SerializeField, Min(0f)] private float _litresPerSecond = 2f;
        private readonly Collider[] _overlaps = new Collider[64];
        private readonly HashSet<VehicleFuelComponent> _vehicles = new HashSet<VehicleFuelComponent>();
        private float _elapsed;

        private void FixedUpdate()
        {
            _elapsed += Time.fixedDeltaTime;
            if (_elapsed < 0.5f)
                return;
            float deltaTime = _elapsed;
            _elapsed = 0f;
            _vehicles.Clear();
            int count = Physics.OverlapSphereNonAlloc(transform.position, _radius, _overlaps,
                Physics.AllLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; ++i)
            {
                VehicleFuelComponent fuel = _overlaps[i].GetComponentInParent<VehicleFuelComponent>();
                if (fuel == null || !fuel.IsServer || !_vehicles.Add(fuel) ||
                    (fuel.transform.position - transform.position).sqrMagnitude > _radius * _radius)
                    continue;
                Rigidbody body = fuel.GetComponent<Rigidbody>();
                if (body == null)
                    continue;
#if UNITY_6000_0_OR_NEWER
                if (body.linearVelocity.sqrMagnitude > 0.25f)
#else
                if (body.velocity.sqrMagnitude > 0.25f)
#endif
                    continue;
                fuel.ServerRefuel(Mathf.Max(0f, _litresPerSecond) * deltaTime, out _);
            }
        }
    }
}
