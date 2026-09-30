using Insthync.CameraAndInput;
using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>Motorcycle lean/pitch input in addition to the shared vehicle controls.</summary>
    public class UVCMotorcyclePlayerController : UVCVehiclePlayerController
    {
        [Tooltip("Optional configured -1..1 input axis. Empty uses Q (back) and E (forward).")]
        [SerializeField] private string _pitchAxis;
        protected override float ReadPitch()
        {
            if (!InputManager.IsUseNonMobileInput()) return 0f;
            if (!string.IsNullOrEmpty(_pitchAxis)) return InputManager.GetAxis(_pitchAxis, false);
#if ENABLE_INPUT_SYSTEM
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            return keyboard == null ? 0f : (keyboard.eKey.isPressed ? 1f : 0f) - (keyboard.qKey.isPressed ? 1f : 0f);
#elif ENABLE_LEGACY_INPUT_MANAGER
            return (Input.GetKey(KeyCode.E) ? 1f : 0f) - (Input.GetKey(KeyCode.Q) ? 1f : 0f);
#else
            return 0f;
#endif
        }
    }
}
