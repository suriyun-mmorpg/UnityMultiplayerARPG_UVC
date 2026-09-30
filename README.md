# Universal Vehicle Controller integration

Connects PG's Universal Vehicle Controller cars to MMORPG KIT vehicles and its per-seat player-controller routing. Requires the kit core with `BaseVehiclePlayerController` support and the separately installed `Assets/UniversalVehicleController` package. Vendor scripts, meshes, textures and settings remain dependencies; they are not copied into this submodule.

## Demo

1. Open `Demo/Scenes/00Init_UVC.unity`, then enter Play Mode.
2. Use the kit's Single Player or Host flow and create a **new character** using one of the demo classes. Their starting map is `UVCDrivingDemo`; existing saved characters keep their previous map.
3. Approach either car and use the kit's normal vehicle interaction. Seat 0 drives; seat 1 is a passenger. Both use the vehicle camera/exit controller.

| Input binding | Default keyboard input | Action |
| --- | --- | --- |
| Horizontal | A / D | Steering |
| Vertical, positive | W | Throttle |
| Vertical, negative | S | Brake / reverse, using UVC gearbox logic |
| Jump | Space | Handbrake |
| Sprint | Shift | Boost (enabled on the demo car) |
| ExitVehicle | Project-configured binding | Leave seat |
| CameraRotate | Project-configured binding | Camera control inherited from the kit |

The demo includes two instances of the S34 vehicle prefab variant, driver/passenger controller mappings, a driving pad, slalom markers, a ramp, a separate database and four cloned starting classes. Generated scenes are added to Build Settings alongside the existing home scene. For a demo build, put `00Init_UVC` first in the enabled scene list.

## Runtime setup

- Start with a **configured UVC car prefab**, including Rigidbody, CarController, center of mass, wheels and colliders.
- Add `LiteNetLibIdentity`, `VehicleEntity` and `UVCVehicleEntityMovement` to the root. Use this adapter as the sole movement/transform authority. It disables a root `LiteNetLibTransform` and competing root UVC input components.
- Add a root trigger collider covering the chassis for kit interaction (the editor factory creates one). UVC's child colliders continue handling physical collisions. Set the vehicle type, seats, passenger/exit transforms and camera targets on `VehicleEntity`. Register the vehicle in your game database. Give each scene instance a unique network scene identity.
- Add `UVCVehiclePlayerController` components to your player-controller prefab and assign them under the matching VehicleType and seat indices. Only the driver sends driving input; passengers retain camera and exit support.
- Optional separate 0..1 throttle/brake axis bindings support pedals. Public `SetSteering`, `SetThrottle`, `SetBrakeReverse`, `SetHandbrake` and `SetBoost` methods accept mobile UI events; release events must send zero/false. `UseBoundInputs` restores normal bindings.

## Networking

The server simulates the Rigidbody, UVC car and WheelColliders and remains the gameplay authority. Drivers send sanitized controls through the kit's existing movement channel, which verifies connection ownership. A server-issued control generation rejects delayed packets from an earlier driver session, including the same driver exiting and re-entering. Controls clear on ownership changes; a 0.5-second input timeout applies the handbrake. UI/focus blocking and exiting use the base vehicle controller. Stale throttle packets cannot override newer reset packets.

The owning driver predicts with local UVC physics as soon as it receives a snapshot matching its current seat and owner. Position, full rotation, velocity and angular velocity reconcile toward the server with bounded extrapolation (0.15 seconds by default). This is approximate PhysX prediction with smooth corrections, not deterministic rollback/input replay. High latency or divergent collisions can still produce corrections. Disable **Enable Prediction** on the movement component to use server interpolation for the driver too.

Passengers and observers stay kinematic and interpolate body and wheel poses. Teleports carry a revision so even short teleports snap immediately, including on a predicting driver. Ownership loss and component disable stop local simulation. Snapshot loss parks the predicted controls after the timeout. Stale snapshots and mismatched wheel counts are fully consumed to preserve subsequent entries in batched movement packets.

Snapshots also carry RPM, gear, engine state/load, throttle/brake, turbo, boost fuel and active boost/handbrake flags. Remote replicas feed these into the vendor `CarSFX` state without running drivetrain physics. Local prediction retains immediate engine response while boost fuel remains server-owned. `Telemetry` and `CurrentMoveSpeed` expose state for HUDs.

The telemetry bridge extends UVC's partial `PG.CarController` in this submodule. It must compile in the same assembly as the vendor partials (the supplied package uses `Assembly-CSharp`). It uses neither reflection nor changes to vendor files. If you add assembly definitions, keep all these partials together.

The movement wire format changed: update the server and all clients together. Snapshots include engine and per-wheel condition. UVC crash deformation/debris, tire-surface audio, trailers and manual gear controls are not replicated. The demo disables vendor crash deformation so server and predicted colliders stay consistent. For custom vehicles, also disable unsynchronized deformation before enabling prediction.

## Crash damage

The demo car has **1,000 HP** and `UVCVehicleCrashDamage`. Server collisions apply environmental HP damage using impact speed into the surface, limited by impulse divided by vehicle mass. The default threshold is 5 m/s; damage is 2.5 times the squared speed above that threshold, capped at 1,000 per impact. A 15 m/s impact with sufficient impulse causes 250 damage. Only the strongest contact in a physics step counts, with a 0.2-second cooldown. Glancing scrapes and light objects therefore cause less damage. Vehicle invincibility and safe areas are respected; combat armor is not applied to crashes.

Each crash reduces normalized engine condition and, when close enough, the nearest wheel's condition. Engine damage reduces torque and can stop the engine. Wheel damage reduces its torque, grip and steering response. Both server simulation and driver prediction use the replicated conditions; clients cannot submit damage or repairs. These mechanical conditions are separate from UVC's local damageable-part health and never detach wheels or deform colliders.

At zero HP the adapter calls the kit's normal vehicle destruction path, including occupant removal and scene-vehicle respawn. The demo uses a 3-second destruction delay and a further 10-second respawn delay. Respawn resets mechanical condition. An authorized server action can call `UVCVehicleCrashDamage.Repair()` to restore HP and condition on a surviving vehicle; no client repair command or repair UI is provided. Impact settings and a server-side crash damage UnityEvent are exposed on the component.

## Editor tools and verification

Under **Tools > MMORPG KIT > UVC Integration**:

- **Create Demo Assets** builds the demo through Unity APIs when demo scenes do not already exist. It preserves existing authored demo scenes.
- **Create Player Entity Prefabs** creates demo variants of the source database's available player entity prefabs, assigns their matching UVC class assets, and registers them in the demo database. It also repairs this setup in an existing demo.
- **Configure Demo Networking** disables unsynchronized crash deformation and configures the existing demo prefab with crash damage and 1,000 HP.
- **Validate Integration** checks serialization, driver generations, input ordering/timeouts, prediction gating, telemetry, rotation/teleports, database registration, seats and scene identities. It also runs an isolated server/two-client replica test using the real adapter packet methods over temporary localhost UDP sockets: passenger rejection, focus reset, ownership handover, stale input, predicted teleports and disable. Run outside Play Mode.

Validation also covers impact thresholds/caps, duplicate contacts, cooldown, invincibility, client rejection, condition replication, matching handling modifiers, repairs, the lethal-damage destruction request and condition reset on respawn. The destruction test intercepts the network-destruction boundary; it does not run the kit's full live despawn/respawn cycle.

The entity creator also exposes the UVC movement factory for configured car prefabs. Add `UVCVehicleCrashDamage` and enable `VehicleEntity.canBeAttacked` to opt custom vehicles into crash damage.

Editor compilation and integration validation run in Unity 2022.3.62f3. The replica test exercises real localhost transport with synthetic impacts, but does not simulate a live two-player PhysX session. Real collision tuning, handling under latency, the complete destruction/respawn cycle and audible engine mixing still require gameplay verification.

## Layout

`Scripts` contains runtime code; `Editor` contains the factory, builder and validation code; `Demo/Scenes`, `Demo/Prefabs` and `Demo/GameData` contain the sample assets.
