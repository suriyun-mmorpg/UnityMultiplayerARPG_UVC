using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>Bounded dead reckoning for PhysX prediction reconciliation (not deterministic rollback).</summary>
    public static class UVCVehiclePrediction
    {
        public static Quaternion ExtrapolateRotation(Quaternion rotation, Vector3 angularVelocity, float seconds)
        {
            float speed = angularVelocity.magnitude;
            return speed > 0.0001f ? Quaternion.AngleAxis(speed * seconds * Mathf.Rad2Deg, angularVelocity / speed) * rotation : rotation;
        }

        public static float ExtrapolationTime(float snapshotAge, float maximum) => Mathf.Clamp(snapshotAge, 0f, maximum);

        public static bool CanPredict(bool enabled, bool server, bool localDriver, bool hasSnapshot,
            long ownerId, uint driverId, long snapshotOwnerId, uint snapshotDriverId) =>
            enabled && !server && localDriver && hasSnapshot && driverId != 0 &&
            ownerId == snapshotOwnerId && driverId == snapshotDriverId;
    }
}
