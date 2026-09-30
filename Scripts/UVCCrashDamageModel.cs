using UnityEngine;

namespace MultiplayerARPG
{
    public static class UVCCrashDamageModel
    {
        // Normal speed rejects glancing scrapes; impulse/mass rejects collisions with very light props.
        public static int Calculate(float normalSpeed, float deltaVelocity, float threshold, float damageScale, int maximum)
        {
            if (float.IsNaN(normalSpeed) || float.IsInfinity(normalSpeed) ||
                float.IsNaN(deltaVelocity) || float.IsInfinity(deltaVelocity))
                return 0;
            float excess = Mathf.Max(0f, Mathf.Min(normalSpeed, deltaVelocity) - Mathf.Max(0f, threshold));
            return Mathf.CeilToInt(Mathf.Min(Mathf.Max(0, maximum), excess * excess * Mathf.Max(0f, damageScale)));
        }

        public static float Condition(float value) => float.IsNaN(value) || float.IsInfinity(value) ? 0f : Mathf.Clamp01(value);
        public static float GripMultiplier(float condition) => Mathf.Lerp(0.25f, 1f, Condition(condition));
        public static float SteeringMultiplier(float condition) => Mathf.Lerp(0.3f, 1f, Condition(condition));
    }
}
