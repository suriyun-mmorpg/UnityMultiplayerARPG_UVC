namespace MultiplayerARPG
{
    /// <summary>Server-side input lifetime. A new driver/owner invalidates queued old input.</summary>
    public sealed class UVCVehicleControlSession
    {
        public long OwnerId { get; private set; } = long.MinValue;
        public uint DriverId { get; private set; }
        public uint Generation { get; private set; }
        private long _timestamp = long.MinValue;
        private float _lastInputTime = float.NegativeInfinity;
        private UVCVehicleInput _input = UVCVehicleInput.Parked;

        public bool UpdateDriver(long ownerId, uint driverId)
        {
            if (OwnerId == ownerId && DriverId == driverId)
                return false;
            OwnerId = ownerId;
            DriverId = driverId;
            Invalidate();
            return true;
        }

        public void Invalidate()
        {
            unchecked { ++Generation; }
            _timestamp = long.MinValue;
            ClearInput();
        }

        public void ClearInput()
        {
            _lastInputTime = float.NegativeInfinity;
            _input = UVCVehicleInput.Parked;
        }

        public bool Accept(uint generation, long timestamp, UVCVehicleInput input, float now)
        {
            if (DriverId == 0 || OwnerId < 0 || generation != Generation || timestamp <= _timestamp)
                return false;
            _timestamp = timestamp;
            SetLocal(input, now);
            return true;
        }

        public void SetLocal(UVCVehicleInput input, float now)
        {
            _input = input.Sanitize();
            _lastInputTime = now;
        }

        public UVCVehicleInput GetInput(float now, float timeout, bool canDrive) =>
            canDrive && DriverId != 0 && OwnerId >= 0 && now - _lastInputTime <= timeout
                ? _input : UVCVehicleInput.Parked;
    }
}
