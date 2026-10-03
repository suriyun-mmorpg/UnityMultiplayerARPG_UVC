using Insthync.CameraAndInput;
using UnityEngine;

namespace MultiplayerARPG
{
    public class UVCVehiclePlayerController : BaseVehiclePlayerController
    {
        [Header("Kit input bindings")]
        [SerializeField] private string _steeringAxis = "Horizontal";
        [SerializeField] private string _pedalAxis = "Vertical";
        [Tooltip("Optional separate 0..1 pedal axes; leave empty to use positive/negative Vertical.")]
        [SerializeField] private string _throttleAxis;
        [SerializeField] private string _brakeReverseAxis;
        [SerializeField] private string _handbrakeButton = "Jump";
        [SerializeField] private string _boostButton = "Sprint";
        [Tooltip("Optional configured input button. The keyboard fallback can be changed or set to None.")]
        [SerializeField] private string _hornButton;
        [SerializeField] private KeyCode _hornKey = KeyCode.H;
        private UVCVehicleEntityMovement _movement;
        private UVCVehicleInput _mobileInput;
        private bool _useMobileInput;

        protected override void OnActivated()
        {
            _movement = Vehicle.Entity.GetComponent<UVCVehicleEntityMovement>();
            _useMobileInput = false;
            _mobileInput = default;
            if (_movement == null)
                Debug.LogError("UVC vehicle controls require UVCVehicleEntityMovement on the vehicle root.", this);
            ResetInput();
        }

        protected override void UpdateControls(float deltaTime)
        {
            if (!CanDrive || _movement == null)
                return;
            float pedals = ReadAxis(_pedalAxis);
            UVCVehicleInput input = _useMobileInput ? _mobileInput : new UVCVehicleInput
            {
                steering = ReadAxis(_steeringAxis),
                throttle = string.IsNullOrEmpty(_throttleAxis) ? Mathf.Max(0f, pedals) : ReadAxis(_throttleAxis),
                brakeReverse = string.IsNullOrEmpty(_brakeReverseAxis) ? Mathf.Max(0f, -pedals) : ReadAxis(_brakeReverseAxis),
                handbrake = !string.IsNullOrEmpty(_handbrakeButton) && InputManager.GetButton(_handbrakeButton),
                boost = !string.IsNullOrEmpty(_boostButton) && InputManager.GetButton(_boostButton),
                pitch = ReadPitch(),
            };
            input.horn = (_movement.Horn != null && _movement.Horn.LocalInput) ||
                InputManager.GetButton(_hornButton) || InputManager.GetKey(_hornKey);
            _movement.SetInput(input);
        }

        private static float ReadAxis(string name) => string.IsNullOrEmpty(name) ? 0f : InputManager.GetAxis(name, false);
        protected virtual float ReadPitch() => 0f;

        protected override void ResetInput()
        {
            _mobileInput = default;
            if (_movement != null) _movement.Horn?.ResetLocalInput();
            if (_movement != null && CanDrive)
                _movement.SetInput(UVCVehicleInput.Parked);
        }

        protected override void OnDeactivated() { _movement = null; _useMobileInput = false; }

        // Suitable for EventTrigger/slider UnityEvents. Pointer-up must send zero/false.
        public void SetSteering(float value) { _useMobileInput = true; _mobileInput.steering = value; }
        public void SetThrottle(float value) { _useMobileInput = true; _mobileInput.throttle = value; }
        public void SetBrakeReverse(float value) { _useMobileInput = true; _mobileInput.brakeReverse = value; }
        public void SetHandbrake(bool value) { _useMobileInput = true; _mobileInput.handbrake = value; }
        public void SetBoost(bool value) { _useMobileInput = true; _mobileInput.boost = value; }
        public void SetPitch(float value) { _useMobileInput = true; _mobileInput.pitch = value; }
        public void SetHorn(bool value) { if (_movement != null && CanDrive) _movement.Horn?.SetLocalInput(value); }
        public void UseBoundInputs() { _useMobileInput = false; _mobileInput = default; }
    }
}
