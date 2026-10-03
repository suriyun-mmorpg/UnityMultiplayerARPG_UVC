using UnityEngine;

namespace MultiplayerARPG
{
    // Run after UVC updates its engine and wheels, before the physics step.
    [DefaultExecutionOrder(100)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(UVCVehicleEntityMovement), typeof(VehicleFuelComponent))]
    public class UVCVehicleFuelAdapter : MonoBehaviour
    {
        private UVCVehicleEntityMovement _movement;
        private VehicleFuelComponent _fuel;
        private bool _wasEmpty;

        private void Awake()
        {
            _movement = GetComponent<UVCVehicleEntityMovement>();
            _fuel = GetComponent<VehicleFuelComponent>();
        }

        private void FixedUpdate()
        {
            if (!_fuel.IsInitialized || !_fuel.UsesFuel || _movement.Car == null || !_movement.Car.enabled ||
                (!_movement.IsServer && !_movement.IsPredicting))
                return;
            if (_movement.IsServer)
                _fuel.ServerConsumeFuel(_movement.Car.EngineIsOn, _movement.Car.CurrentAcceleration, Time.fixedDeltaTime);
            if (_fuel.IsEmpty)
            {
                _movement.Car.ApplyUVCFuelCutoff(_movement.ServiceBrake, Time.fixedDeltaTime);
                _wasEmpty = true;
            }
            else if (_wasEmpty)
            {
                _movement.Car.ResetUVCFuelCutoff();
                _wasEmpty = false;
            }
        }
    }
}

namespace PG
{
    // Same assembly as UVC's existing partial controller. No vendor source changes.
    public partial class CarController
    {
        private float[] _uvcFuelBrakeTorques;

        public void ResetUVCFuelCutoff()
        {
            if (_uvcFuelBrakeTorques != null)
                System.Array.Clear(_uvcFuelBrakeTorques, 0, _uvcFuelBrakeTorques.Length);
        }

        public void ApplyUVCFuelCutoff(float serviceBrake, float deltaTime)
        {
            StopUVCNetworkEngine();
            EngineRPM = 0f;
            CurrentBrake = Mathf.Clamp01(serviceBrake);
            EngineLoad = 0f;
            TargetRPM = 0f;
            ABSIsActive = false;
            if (_uvcFuelBrakeTorques == null || _uvcFuelBrakeTorques.Length != Wheels.Length)
                _uvcFuelBrakeTorques = new float[Wheels.Length];
            for (int i = 0; i < Wheels.Length; ++i)
            {
                Wheel wheel = Wheels[i];
                if (wheel == null || wheel.IsDead || wheel.WheelCollider == null || !wheel.WheelCollider.enabled)
                    continue;
                wheel.SetMotorTorque(0f, true);
                float brake = InHandBrake ? (wheel.HandBrakeWheel ? 1f : 0f) : CurrentBrake;
                if (!InHandBrake && Steer.ABS > 0f && brake > 0f &&
                    wheel.ForwardSlipNormalized > 2.8f - Steer.ABS * 1.2f)
                {
                    brake = 0f;
                    ABSIsActive = true;
                }
                wheel.SetBrakeTorque(brake);
                float target = brake * wheel.MaxBrakeTorque;
                float previous = Mathf.Max(_uvcFuelBrakeTorques[i], wheel.WheelCollider.brakeTorque);
                // UVC clears its brake target while the engine is off. Preserve its
                // normal brake ramp here without re-running wheel physics or suspension.
                _uvcFuelBrakeTorques[i] = target > previous
                    ? Mathf.Lerp(previous, target, Mathf.Clamp01(2f * deltaTime)) : target;
                wheel.WheelCollider.brakeTorque = _uvcFuelBrakeTorques[i];
            }
        }
    }
}
