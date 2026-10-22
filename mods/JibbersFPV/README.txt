Jibbers FPV Drone 0.1.0

A flyable FPV quadcopter built on the game's own drone view. Taking off switches the
camera into the game's drone (orbit) camera, the same mode the game uses to show your
camera to other riders as a drone, and replaces its floaty movement with real quad
flight. Landing puts your normal camera back.

FLIGHT MODES (LB or F10 cycles)
Acro   Rate mode, like a real FPV quad: sticks spin the quad (Betaflight "actual"
       rates), throttle is thrust, gravity and drag do the rest. Flips and rolls work.
Angle  Self-levelling: stick deflection sets a tilt angle, and a centred throttle
       holds altitude (turn this off in settings for a direct throttle).
Cine   A smooth gimbal drone driven by the game's own Drone logic: speed towards a
       goal = min(distance / followTime, sqrt(2 * maxAccel * distance)), clamped
       acceleration, eased turning, a little sway from its tilt. Lock onto a rider
       and it follows and frames them; the sticks then orbit (right stick X),
       close in or back off (right stick Y) and raise or lower the shot (left stick Y).

CONTROLS
Gamepad (XInput, Mode 2):
  L3 + R3        take off / land
  Left stick     throttle (centre = hover) and yaw
  Right stick    pitch and roll (Cine: move / orbit the locked rider)
  LT (hold)      precision: half rates and speed
  LB             cycle flight mode
  RB tap / hold  lock onto the rider nearest the centre of view (tap again to cycle) / release
  Y              return to the rider
  D-pad up/down  camera uptilt +/- 5 deg (Cine: tilt the gimbal)
  D-pad L/R      field of view -/+ 5 deg
Keyboard and mouse:
  F9             take off / land
  W/S A/D        pitch / roll        Q/E or arrows   yaw
  Space / Ctrl   throttle up / down (C also lowers)
  Mouse          look (Acro: turns the quad; Angle: yaw; Cine: pan and tilt the gimbal)
  Right mouse or Left Shift (hold)   precision
  F10 mode, Middle mouse lock (hold to release), Home return to rider,
  PgUp/PgDn uptilt or gimbal, Numpad + / - field of view.
Every hotkey can be changed in Options > Mods > FPV Drone.

The gamepad throttle can also be "Left stick (full range)" (bottom = motors idle) or
"Right trigger" (LT/RT climb in Angle and Cine) in the Input settings.

OSD AND SOUND
An FPV-style OSD shows the flight mode, motor output as a throttle bar, speed, height above
the ground, flight time, the locked rider, DISARMED after a crash, a crosshair in Acro/Angle
and, for the first seconds, the controls. The quad sounds like one: four detuned motors whose
pitch and volume follow the throttle, wind that rises with speed and a thump on crashes. The
sound is synthesised in the mod (no audio files) and only you hear it. Both can be switched
off, and the volume set, under Display.

PHYSICS AND SAFETY
Sphere casts against the game's camera collision layers stop the drone going through the
map: it slides along walls and bounces off the ground (optional crash: a hard hit cuts the
motors for a moment). Ground effect gives a little extra lift near the ground. Lost drones
(out of the map, very far away, invalid state) return to the rider automatically. The
simulation freezes while the game is paused or the window is not focused.

Rider physics, tricks and replays are not modified. Taking off only works from the normal
follow camera (or the game's own drone camera). If the game leaves the drone view itself
(respawn, its camera key), the drone lands and the game's camera takes over. Filmer only
films in the follow camera, so the two never fight.

Lock-on reads the positions of riders your game is already showing (local and remote
RagdollController bodies) to aim the camera. It changes nothing about other players.

SETTINGS
Main menu > Options > Mods > FPV Drone: flight mode, thrust to weight, top speed, tilt limit,
altitude hold, uptilt, field of view, OSD, sound and volume, stick sensitivity (one slider
that scales every stick rotation in all modes), rates (centre, max, expo), Cine speed, follow style
(fixed offset or chase from behind), follow smoothing, acceleration, gimbal turn speed,
collisions, crashes, prop vibration, gamepad throttle, invert pitch, mouse look and
sensitivity, deadzone and hotkeys. Mode, uptilt and FOV changes made in flight are for the
session; editing settings restores the saved ones.

BUILD
From this folder: cargo build --release --offline
Copy target/release/jibbers_fpv.dll and build/jibbers_fpv.bundle into <game>/JibbersMods/
and restart the game. Without the bundle the drone flies silently (the log says why).

The sounds ship as a Unity asset bundle because the game's build strips AudioClip.Create and
most AudioSource setters. To rebuild it after changing src/synth.rs:
  cargo run --release --offline --bin write_sounds     (writes unity/Assets/Audio/*.wav)
  "C:\Program Files\Unity\Hub\Editor\6000.3.8f1\Editor\Unity.exe" -batchmode -quit -nographics
      -projectPath unity -executeMethod BuildFpv.Build      (writes build/jibbers_fpv.bundle)
Use the game's Unity version (6000.3.8f1) or the bundle won't load.
Tests: cargo test --release --offline (flight model, collisions, follow, input shaping)

GAME API
Game classes are reached through SDK symbols: camera.follow (late_update hook, mode,
target, lock_input, ground_layer), camera.cam_state.follow, player.ragdoll, plus the mod's
own fpv_symbols.json: fpv.cam_state.orbit (the drone view) and fpv.follow.set_mode /
target_ragdoll. set_mode uses nth 0 among three void (CamState) methods (setter, enter and
exit handlers); the mod checks the state changed and otherwise writes camera.follow.mode.
Mouse look uses Rewired's public API (ReInput.controllers.Mouse). Gamepads are read with
XInput, so PlayStation pads need Steam Input (or similar) to appear as an Xbox pad.

STATUS
Builds clean; automated flight tests pass. The symbol rules pass `sdk-symbols lint` but have
not been resolved against a dump of the running game. Not yet tested in game: flight feel,
whether the game keeps rider input away from the sticks in drone view, and how the drone
looks to other riders. The loader log reports "fpv: ready", take off / landing, mode
changes, locks and any missing API. `fpv.status` and `fpv.toggle` are available as commands.
