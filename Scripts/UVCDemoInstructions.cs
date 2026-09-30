using UnityEngine;

namespace MultiplayerARPG
{
    /// <summary>Small dependency-free help overlay used only by the driving demo.</summary>
    public class UVCDemoInstructions : MonoBehaviour
    {
        private void OnGUI()
        {
            GUI.Box(new Rect(12, 12, 390, 165), "UVC driving demo");
            GUI.Label(new Rect(24, 38, 370, 130),
                "Approach a vehicle and use the kit's Activate control.\n" +
                "A/D: steer   W: accelerator   S: brake / reverse\n" +
                "Space (Jump): handbrake   Shift (Sprint): boost\n" +
                "Use ExitVehicle to leave. CameraRotate: look around.\n" +
                "Cars: two seats. Motorcycle: one driver seat.\n" +
                "Motorcycle Q/E: lean back/forward; pitch in air.\n" +
                "Hard crashes damage HP, engine and nearby wheels.");
        }
    }
}
