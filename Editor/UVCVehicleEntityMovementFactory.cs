using UnityEngine;

namespace MultiplayerARPG
{
    public class UVCVehicleEntityMovementFactory : IEntityMovementFactory
    {
        public string Name => "Universal Vehicle Controller (server authoritative)";
        public DimensionType DimensionType => DimensionType.Dimension3D;
        public bool ValidateSourceObject(GameObject obj) => obj != null && obj.GetComponent<PG.CarController>() != null;
        public IEntityMovementComponent Setup(GameObject obj, ref Bounds bounds)
        {
            if (!ValidateSourceObject(obj))
                throw new System.ArgumentException("Use a configured UVC car prefab with CarController on its root.");
            var movement = obj.GetComponent<UVCVehicleEntityMovement>() ?? obj.AddComponent<UVCVehicleEntityMovement>();
            bounds = movement.GetMovementBounds();
            EnsureInteractionCollider(obj, bounds);
            return movement;
        }

        public static void EnsureInteractionCollider(GameObject obj, Bounds bounds)
        {
            if (obj.GetComponent<Collider>() != null)
                return;
            // Kit interaction looks for IActivatableEntity on the collider's own object.
            // UVC's physical chassis colliders are children, so add a root trigger only.
            var collider = obj.AddComponent<BoxCollider>();
            collider.isTrigger = true;
            Bounds localBounds = new Bounds(obj.transform.InverseTransformPoint(bounds.center), Vector3.zero);
            for (int x = -1; x <= 1; x += 2)
                for (int y = -1; y <= 1; y += 2)
                    for (int z = -1; z <= 1; z += 2)
                        localBounds.Encapsulate(obj.transform.InverseTransformPoint(bounds.center +
                            Vector3.Scale(bounds.extents, new Vector3(x, y, z))));
            collider.center = localBounds.center;
            collider.size = localBounds.size;
        }
    }
}
