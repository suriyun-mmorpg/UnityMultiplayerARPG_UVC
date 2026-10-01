# Universal Vehicle Controller integration

Connects PG's Universal Vehicle Controller cars to MMORPG KIT vehicles and its per-seat player-controller routing. Requires the kit core with `BaseVehiclePlayerController` support and the separately installed `Assets/UniversalVehicleController` package. Vendor scripts, meshes, textures and settings remain dependencies; they are not copied into this submodule.

## Demo

1. Open `Demo/Scenes/00Init_UVC.unity`, then enter Play Mode.
2. Use the kit's Single Player or Host flow and create a **new character** using one of the demo classes. Their starting map is `UVCDrivingDemo`; existing saved characters keep their previous map.
3. Approach either car or the motorcycle parked beside them and use the kit's normal vehicle interaction. Cars have a driver and passenger seat; the motorcycle has one driver seat. All use the vehicle camera/exit controller.

| Input binding | Default keyboard input | Action |
| --- | --- | --- |
| Horizontal | A / D | Steering |
| Vertical, positive | W | Throttle |
| Vertical, negative | S | Brake / reverse, using UVC gearbox logic |
| Jump | Space | Handbrake |
| Sprint | Shift | Boost (enabled on the demo car) |
| ExitVehicle | Project-configured binding | Leave seat |
| CameraRotate | Project-configured binding | Camera control inherited from the kit |
| Motorcycle pitch | Q / E | Lean back / forward on the ground; pitch in the air |

The demo includes two S34 cars and one motorcycle prefab variant, seat-specific controller mappings, a driving pad, slalom markers, a ramp, a separate database and four cloned starting classes. Generated scenes are added to Build Settings alongside the existing home scene. For a demo build, put `00Init_UVC` first in the enabled scene list.

### Motorcycle

`Demo/Prefabs/UVC_Motorcycle_Vehicle.prefab` uses the vendor `PG.BikeController` with the same network movement, prediction, engine telemetry, crash damage and character hit damage as the cars. Its type is `uvc_motorcycle`, routed to `UVCMotorcyclePlayerController`. Q/E supplies the bike's pitch control; a configured Pitch Axis can replace these keys. Mobile UI can call the inherited `SetPitch(-1..1)` and must send zero on release.

Body lean, wheels, handlebars and forks synchronize to observers. The rear fork parent is authored in the prefab so the vendor does not create a different hierarchy at runtime. The local UVC crash latch is disabled through its two thresholds because its private state is not replicated; the motorcycle uses the kit's HP, engine/wheel condition and destruction path instead. UVC's balancing and steering remain active. The rider is hidden until a suitable riding animation/IK pose is provided. The bike retains 1,000 HP and boost for this demo.

Use **Tools > MMORPG KIT > UVC Integration > Add Motorcycle Demo** to add the bike to an existing driving demo. Rerunning it preserves the motorcycle prefab and avoids duplicate scene vehicles, database entries and controller mappings.

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

The movement wire format changed: update the server and all clients together. Snapshots include engine and per-wheel condition plus an ordered array of additional visual poses for motorcycle forks/handlebars. The server and clients must use matching visual arrays. UVC crash deformation/debris, tire-surface audio, trailers and manual gear controls are not replicated. The demo disables vendor crash deformation so server and predicted colliders stay consistent. For custom vehicles, also disable unsynchronized deformation before enabling prediction.

## Crash damage

The demo car has **1,000 HP** and `UVCVehicleCrashDamage`. Server collisions apply environmental HP damage using impact speed into the surface, limited by impulse divided by vehicle mass. The default threshold is 5 m/s; damage is 2.5 times the squared speed above that threshold, capped at 1,000 per impact. A 15 m/s impact with sufficient impulse causes 250 damage. Only the strongest contact in a physics step counts, with a 0.2-second cooldown. Glancing scrapes and light objects therefore cause less damage. Vehicle invincibility and safe areas are respected; combat armor is not applied to crashes.

Each crash reduces normalized engine condition and, when close enough, the nearest wheel's condition. Engine damage reduces torque and can stop the engine. Wheel damage reduces its torque, grip and steering response. Both server simulation and driver prediction use the replicated conditions; clients cannot submit damage or repairs. These mechanical conditions are separate from UVC's local damageable-part health and never detach wheels or deform colliders.

At zero HP the adapter calls the kit's normal vehicle destruction path, including occupant removal and scene-vehicle respawn. The demo uses a 3-second destruction delay and a further 10-second respawn delay. Respawn resets mechanical condition. An authorized server action can call `UVCVehicleCrashDamage.Repair()` to restore HP and condition on a surviving vehicle; no client repair command or repair UI is provided. Impact settings and a server-side crash damage UnityEvent are exposed on the component.

## Hitting characters

`UVCVehicleHitDamage` is enabled on the demo car. Server chassis overlaps, sweeps between physics positions, and collision contacts detect character body colliders and trigger damage hitboxes. The configurable local-space hit volume should closely fit the chassis; select the component to see its red wireframe. Target Layers filters eligible colliders independently of the physics collision matrix. The kit's interaction trigger does not itself cause damage.

The default minimum closing speed is 3 m/s. Raw body damage is `2 * (closingSpeed - 3)^2`, capped at 250, with a 0.75-second cooldown shared by all colliders of a character. The standard combat pipeline then applies body mitigation, armor, hit/miss/block/critical rules, combat text, aggro and kill credit. A 10 m/s direct hit supplies 98 raw damage; final HP loss depends on the combat rules. An empty Damage Element uses the kit's default element. Optional Knockback Speed uses the kit's movement-force system after a successful non-lethal hit; it defaults to zero.

The current living driver is the instigator. Unoccupied cars, stationary overlaps, the car's own occupants, dead or invincible targets, and safe-area drivers/vehicles/targets are excluded. PvP, party, faction, guild and subchannel permissions go through `CanReceiveDamageFrom`; enabling vehicle hits does not enable PvP on a PvE map. The component never accepts client damage reports. Character HP and combat events use the kit's existing replication. Damage to characters is independent of whether the car itself can be attacked.

Explicit movement-adapter teleports reset sweep history; large external position jumps are also rejected. Overlap relative velocity supports Unity CharacterController, NavMeshAgent and Rigidbody characters; custom movement implementations without these fall back to zero target velocity. Sweeps use the previous chassis orientation and skip large rotation jumps, so live tuning remains necessary for extreme angular speeds and latency.

## Editor tools and verification

Under **Tools > MMORPG KIT > UVC Integration**:

- **Create Demo Assets** builds the demo through Unity APIs when demo scenes do not already exist. It preserves existing authored demo scenes.
- **Add Motorcycle Demo** adds and registers the motorcycle prefab, its single-seat controller mapping and one scene instance without duplicating existing entries.
- **Create Player Entity Prefabs** creates demo variants of the source database's available player entity prefabs, assigns their matching UVC class assets, and registers them in the demo database. It also repairs this setup in an existing demo.
- **Configure Demo Networking** disables unsynchronized crash deformation and configures the existing demo prefab with crash damage, character hit damage and 1,000 HP.
- **Validate Integration** checks serialization, driver generations, input ordering/timeouts, prediction gating, telemetry, rotation/teleports, database registration, seats and scene identities. It also runs an isolated server/two-client replica test using the real adapter packet methods over temporary localhost UDP sockets: passenger rejection, focus reset, ownership handover, stale input, predicted teleports and disable. Run outside Play Mode.

Validation also covers impact thresholds/caps, duplicate contacts, cooldown, invincibility, client rejection, condition replication, matching handling modifiers, repairs, the lethal-damage destruction request and condition reset on respawn. The destruction test intercepts the network-destruction boundary; it does not run the kit's full live despawn/respawn cycle.

Character-hit validation exercises actual overlap/sweep queries and the combat dispatch with an editor-only character fixture, plus the kit's PvE/PvP and party rules. It checks attribution, duplicate colliders, safe areas, passengers, client rejection and lethal dispatch. The fixture substitutes damage calculation and received-damage callbacks, so full live armor calculations, kill rewards, knockback and network death events still need gameplay testing.

The server/two-client replica validation also runs against the motorcycle prefab, checking its two-wheel packet layout and moving-part poses. This remains an edit-mode transport test; live motorcycle balance, handling, audio and riding under latency require gameplay verification.

The entity creator also exposes the UVC movement factory for configured car prefabs. Add `UVCVehicleCrashDamage` and enable `VehicleEntity.canBeAttacked` to opt custom vehicles into crash damage.

Editor compilation and integration validation run in Unity 2022.3.62f3. The replica test exercises real localhost transport with synthetic impacts, but does not simulate a live two-player PhysX session. Real collision tuning, handling under latency, the complete destruction/respawn cycle and audible engine mixing still require gameplay verification.

## Layout

`Scripts` contains runtime code; `Editor` contains the factory, builder and validation code; `Demo/Scenes`, `Demo/Prefabs` and `Demo/GameData` contain the sample assets.

### Characters standing on vehicles

The built-in `CharacterControllerEntityMovement` now sends the supporting vehicle ID and a vehicle-local feet position in its movement snapshots. Observers interpolate the character relative to their displayed vehicle, so independently delayed car and character packets do not slide a standing character across the roof. Jumping, leaving contact, and teleporting clear support. Static platforms still use ordinary world-position snapshots.

This requires the accompanying Core changes. **Rebuild the server and every client together:** the built-in character movement packet format includes new platform data. Previously built executables use the old format. Vehicle Samples and UVC both advance observer chassis poses before character movement and use bounded snapshot extrapolation without additional Rigidbody interpolation.

Regression checks cover owner-to-server-to-observer character packets, moving/rotating observer vehicles, packet gaps, stale detach packets, and returning to world interpolation. These are editor regression tests, not a substitute for a two-client gameplay test.

For `NotSecure` character movement, the server relays the latest accepted owner pose rather than its interpolated display pose. Hosts smooth only their local presentation; dedicated servers apply accepted positions without display interpolation. Vehicle-relative offsets are captured at the end of character movement, so a vehicle update between movement and packet serialization cannot change the sent offset. These corrections do not add fields to the platform-aware packet format described above.
