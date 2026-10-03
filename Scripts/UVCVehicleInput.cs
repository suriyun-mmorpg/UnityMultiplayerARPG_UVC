using LiteNetLib.Utils;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>Independent pedals: brake/reverse follows UVC's automatic gearbox semantics.</summary>
    public struct UVCVehicleInput
    {
        public float steering;
        public float throttle;
        public float brakeReverse;
        public float pitch;
        public bool handbrake;
        public bool boost;
        public bool horn;

        public static UVCVehicleInput Parked => new UVCVehicleInput { handbrake = true };

        public UVCVehicleInput Sanitize()
        {
            return new UVCVehicleInput
            {
                steering = ClampFinite(steering, -1f, 1f),
                throttle = ClampFinite(throttle, 0f, 1f),
                brakeReverse = ClampFinite(brakeReverse, 0f, 1f),
                pitch = ClampFinite(pitch, -1f, 1f),
                handbrake = handbrake,
                boost = boost,
                horn = horn,
            };
        }

        private static float ClampFinite(float value, float min, float max) =>
            float.IsNaN(value) || float.IsInfinity(value) ? 0f : Mathf.Clamp(value, min, max);

        public void Write(NetDataWriter writer)
        {
            UVCVehicleInput input = Sanitize();
            writer.Put(input.steering);
            writer.Put(input.throttle);
            writer.Put(input.brakeReverse);
            writer.Put(input.pitch);
            writer.Put((byte)((input.handbrake ? 1 : 0) | (input.boost ? 2 : 0) | (input.horn ? 4 : 0)));
        }

        public static UVCVehicleInput Read(NetDataReader reader)
        {
            var input = new UVCVehicleInput
            {
                steering = reader.GetFloat(), throttle = reader.GetFloat(),
                brakeReverse = reader.GetFloat(), pitch = reader.GetFloat(),
            };
            byte flags = reader.GetByte();
            input.handbrake = (flags & 1) != 0;
            input.boost = (flags & 2) != 0;
            input.horn = (flags & 4) != 0;
            return input.Sanitize();
        }
    }
}
