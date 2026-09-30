using LiteNetLib.Utils;
using UnityEngine;

namespace MultiplayerARPG
{
    public struct UVCVehicleTelemetry
    {
        public float rpm, acceleration, brake, turbo, boostAmount, engineLoad;
        public int gear;
        public bool engineOn, boosting, changingGear, handbrake;

        public static UVCVehicleTelemetry Capture(PG.CarController car) => new UVCVehicleTelemetry
        {
            rpm = car.EngineRPM, acceleration = car.CurrentAcceleration, brake = car.CurrentBrake,
            turbo = car.CurrentTurbo, boostAmount = car.BoostAmount, engineLoad = car.EngineLoad,
            gear = car.CurrentGear, engineOn = car.EngineIsOn, boosting = car.InBoost, changingGear = car.InChangeGear, handbrake = car.InHandBrake,
        };

        public void Write(NetDataWriter writer)
        {
            writer.Put(rpm); writer.Put(acceleration); writer.Put(brake); writer.Put(turbo);
            writer.Put(boostAmount); writer.Put(engineLoad); writer.Put(gear);
            writer.Put((byte)((engineOn ? 1 : 0) | (boosting ? 2 : 0) | (changingGear ? 4 : 0) | (handbrake ? 8 : 0)));
        }

        public static UVCVehicleTelemetry Read(NetDataReader reader)
        {
            var state = new UVCVehicleTelemetry
            {
                rpm = reader.GetFloat(), acceleration = reader.GetFloat(), brake = reader.GetFloat(),
                turbo = reader.GetFloat(), boostAmount = reader.GetFloat(), engineLoad = reader.GetFloat(), gear = reader.GetInt(),
            };
            byte flags = reader.GetByte();
            state.engineOn = (flags & 1) != 0;
            state.boosting = (flags & 2) != 0;
            state.changingGear = (flags & 4) != 0;
            state.handbrake = (flags & 8) != 0;
            return state;
        }
    }
}

namespace PG
{
    // UVC deliberately splits this class into partial files. This bridge must compile in the
    // same assembly as UVC (Assembly-CSharp in the supplied package); no reflection/vendor edits.
    public partial class CarController
    {
        public void StopUVCNetworkEngine()
        {
            ResetUVCNetworkTransientState();
            if (EngineIsOn) StopEngine();
        }

        public void RestoreUVCNetworkEngine()
        {
            ResetUVCNetworkTransientState();
            EngineIsOn = StartEngineInAwake;
            EngineRPM = EngineIsOn ? MinRPM : 0f;
            CurrentGear = 0;
        }
        public void ApplyUVCNetworkBoostAmount(float amount) { BoostAmount = amount; }
        public void ApplyUVCNetworkTelemetry(MultiplayerARPG.UVCVehicleTelemetry state, float blend, bool notify)
        {
            bool wasOn = EngineIsOn;
            if (StartEngineCoroutine != null)
            {
                StopCoroutine(StartEngineCoroutine);
                StartEngineCoroutine = null;
            }
            EngineIsOn = state.engineOn;
            EngineRPM = Mathf.Lerp(EngineRPM, state.rpm, blend);
            CurrentAcceleration = state.acceleration;
            CurrentBrake = state.brake;
            CurrentTurbo = Mathf.Lerp(CurrentTurbo, state.turbo, blend);
            BoostAmount = state.boostAmount;
            EngineLoad = state.engineLoad;
            InBoost = state.boosting;
            CurrentGear = Mathf.Clamp(state.gear, -1, Gearbox.GearsRatio.Length);
            ChangeGearTimer = state.changingGear ? Time.fixedDeltaTime * 2f : 0f;
            if (notify && wasOn != EngineIsOn)
            {
                if (EngineIsOn) OnStartEngineAction?.Invoke(0f);
                else OnStopEngineAction?.Invoke();
            }
        }

        public void ResetUVCNetworkTransientState()
        {
            if (StartEngineCoroutine != null)
                StopCoroutine(StartEngineCoroutine);
            StartEngineCoroutine = null;
            InCutOff = false;
            CutOffTimer = 0f;
            ChangeGearTimer = 0f;
            CurrentAcceleration = CurrentBrake = CurrentTurbo = 0f;
            InBoost = false;
        }
    }
}
