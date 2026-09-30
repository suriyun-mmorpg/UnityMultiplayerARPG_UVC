#if UNITY_EDITOR
namespace MultiplayerARPG
{
    // Test-only destruction boundary: no real network-spawned entity exists in the editor fixture.
    public class UVCCrashDamageValidationProbe : UVCVehicleCrashDamage
    {
        public bool DestroyRequested { get; private set; }
        protected override void DestroyVehicle() { DestroyRequested = true; }
    }
}

#endif
