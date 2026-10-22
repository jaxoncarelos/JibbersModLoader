using System.Text.Json.Nodes;
using Jibbers.Sdk;
using Jibbers.Sdk.Game.Camera;
using Jibbers.Sdk.Interop;
using Jibbers.Sdk.Symbols;

namespace Jibbers.Sdk.Features.Fpv;

public sealed unsafe class DroneController : ModFeature
{
    public override string Id => "fpv";
    public override string Name => "FPV Drone";
    public override string Version => "0.2.0";
    public override string Author => "Jibbers mods";
    public override string Description => "Fly the drone camera in Acro, Angle or Cine mode. F9 or L3+R3 to take off.";
    public override IEnumerable<Setting> Settings => new[]
    {
        Setting.Choice("mode", "Flight mode", new[] { "Acro", "Angle", "Cine" }, "Angle").InSection("Flight"),
        Setting.Slider("twr", "Thrust to weight", 4.5, 2, 8, 0.1).InSection("Flight"),
        Setting.Slider("top_speed", "Top speed (m/s)", 38, 15, 60, 1).InSection("Flight"),
        Setting.Slider("max_angle", "Angle mode max tilt", 50, 20, 80, 1).InSection("Flight"),
        Setting.Toggle("altitude_hold", "Angle mode altitude hold", true).InSection("Flight"),
        Setting.Slider("uptilt", "Camera uptilt", 25, 0, 60, 1).InSection("Flight"),
        Setting.Slider("fov", "Field of view", 85, 50, 130, 1).InSection("Flight"),
        Setting.Toggle("hud", "Show the OSD", true).InSection("Display"),
        Setting.Toggle("sound", "Drone sound", true).InSection("Display"),
        Setting.Slider("volume", "Drone volume", 0.7, 0, 1, 0.05).InSection("Display"),
        Setting.Slider("sensitivity", "Stick sensitivity", 0.6, 0.1, 1.5, 0.05).InSection("Rates"),
        Setting.Slider("rate_center", "Centre rate (deg/s)", 150, 50, 500, 5).InSection("Rates"),
        Setting.Slider("rate_max", "Max rate (deg/s)", 550, 200, 1500, 10).InSection("Rates"),
        Setting.Slider("expo", "Expo", 0.54, 0, 1, 0.01).InSection("Rates"),
        Setting.Slider("cine_speed", "Cine speed (m/s)", 12, 3, 30, 0.5).InSection("Cine"),
        Setting.Toggle("follow", "Follow the locked rider", true).InSection("Cine"),
        Setting.Toggle("chase", "Chase from behind", false).InSection("Cine"),
        Setting.Slider("follow_time", "Follow smoothing (seconds)", 0.4, 0.1, 2, 0.05).InSection("Cine"),
        Setting.Slider("max_accel", "Max acceleration (m/s²)", 25, 5, 60, 1).InSection("Cine"),
        Setting.Slider("turn_speed", "Gimbal turn speed", 4, 1, 12, 0.5).InSection("Cine"),
        Setting.Toggle("collisions", "Collide with the map", true).InSection("Physics"),
        Setting.Toggle("crash", "Disarm on hard crashes", false).InSection("Physics"),
        Setting.Slider("vibration", "Prop vibration", 0.3, 0, 1, 0.05).InSection("Physics"),
        Setting.Choice("throttle", "Gamepad throttle", new[] { "Left stick (centre hover)", "Left stick (full range)", "Right trigger" }, "Left stick (centre hover)").InSection("Input"),
        Setting.Toggle("invert", "Invert pitch", false).InSection("Input"),
        Setting.Toggle("mouse_look", "Mouse look", true).InSection("Input"),
        Setting.Slider("mouse_sens", "Mouse sensitivity", 0.03, 0.005, 0.3, 0.005).InSection("Input"),
        Setting.Slider("deadzone", "Stick deadzone", 0.08, 0, 0.3, 0.01).InSection("Input"),
        Setting.KeyBinding("toggle_key", "Take off / land", (int)Key.F9).InSection("Hotkeys"),
        Setting.KeyBinding("mode_key", "Cycle flight mode", (int)Key.F10).InSection("Hotkeys"),
        Setting.KeyBinding("lock_key", "Lock target (hold: release)", (int)Key.MouseMiddle).InSection("Hotkeys"),
        Setting.KeyBinding("recall_key", "Return to rider", (int)Key.Home).InSection("Hotkeys"),
        Setting.KeyBinding("tilt_up_key", "Tilt camera up", (int)Key.PageUp).InSection("Hotkeys"),
        Setting.KeyBinding("tilt_down_key", "Tilt camera down", (int)Key.PageDown).InSection("Hotkeys"),
        Setting.KeyBinding("fov_up_key", "Widen view", (int)Key.Add).InSection("Hotkeys"),
        Setting.KeyBinding("fov_down_key", "Narrow view", (int)Key.Subtract).InSection("Hotkeys"),
    };
    static readonly FieldRef ModeField = new("camera.follow.mode"), OrbitField = new("fpv.cam_state.orbit");
    static readonly MethodRef SetMode = new("fpv.follow.set_mode");
    int dirty = 1, toggle;
    volatile bool live = true;
    string status = "waiting for camera";
    State? state;

    public override void OnLoad()
    {
        using var symbolsStream = typeof(DroneController).Assembly.GetManifestResourceStream("Fpv.symbols.json")!;
        using var reader = new StreamReader(symbolsStream);
        SymbolTable.Register(reader.ReadToEnd());
        Follow.LateUpdateMethod.Before((self, _) => Frame(new Follow(self)));
        Log("ready; F9 or L3+R3 to take off");
    }
    public override void OnSettingChanged(string key, JsonNode value)
    {
        Interlocked.Exchange(ref dirty, 1);
        // Disabled mods no longer receive hooks, so restore the camera explicitly.
        if (key == "enabled" && !Enabled) MainThread.Run(() => state?.Land(true));
    }
    public override JsonNode? OnCommand(string cmd, JsonObject request) => cmd switch
    {
        "fpv.toggle" => RequestToggle(),
        "fpv.status" => new JsonObject { ["hooked"] = live, ["status"] = status },
        _ => null,
    };
    JsonObject RequestToggle() { Interlocked.Exchange(ref toggle, 1); return new() { ["requested"] = true }; }
    public override void OnUnload()
    {
        live = false;
        MainThread.Run(() => { state?.Dispose(); state = null; });
    }
    void Frame(Follow follow)
    {
        if (!live || follow.IsNull) return;
        try
        {
            var owner = Unity.Call<int>(follow.Ptr, "GetInstanceID");
            if (state is null || state.Owner != owner)
            {
                state?.Dispose();
                state = new State(this, follow, owner);
                Interlocked.Exchange(ref dirty, 1);
            }
            if (Interlocked.Exchange(ref dirty, 0) != 0) state.Refresh();
            state.Frame(Interlocked.Exchange(ref toggle, 0) != 0);
            status = state.Drone is { } drone ? $"flying {drone.Mode} {drone.Velocity.Length * 3.6f:F0} km/h" : "landed";
        }
        catch (Exception exception)
        {
            live = false;
            try { state?.Dispose(); } catch (Exception cleanup) { Log("cleanup: " + cleanup.Message); }
            state = null; status = exception.Message;
            Log("stopped safely: " + exception.Message);
        }
    }
    sealed class State : IDisposable
    {
        readonly DroneController mod;
        readonly GameRef followRef, camera, transform;
        readonly Physics world;
        readonly PilotInput input = new();
        RewiredMouse? mouse;
        FlightConfig flightSettings = new();
        PilotConfig inputSettings = new();
        Mode mode = Mode.Angle;
        readonly int orbit;
        int previous;
        float fov, vibration, flightTime;
        (float Fov, float Near)? original;
        GameRef? locked;
        int lockedId;
        string? lockedName;
        V3 last, velocity;
        Effects? effects;
        public int Owner { get; }
        public Drone? Drone { get; private set; }
        Follow Follow => new(followRef.Target);
        public State(DroneController mod, Follow follow, int owner)
        {
            this.mod = mod; Owner = owner;
            orbit = OrbitField.Get<int>(0);
            world = new Physics(new FieldRef("camera.follow.ground_layer").Get<int>(follow.Ptr));
            var cameraObject = Unity.GetComponent(follow.Ptr, Unity.Class("UnityEngine.Camera"));
            if (Unity.IsNull(cameraObject)) throw new InvalidOperationException("FollowCam has no Camera");
            // Resolve fallible metadata before acquiring persistent object handles.
            try { mouse = new RewiredMouse(); } catch (Exception exception) { mod.Log("no mouse look: " + exception.Message); }
            followRef = new GameRef(follow.Ptr);
            try
            {
                camera = new GameRef(cameraObject);
                try { transform = new GameRef(Unity.Transform(follow.Ptr)); }
                catch { camera.Dispose(); throw; }
            }
            catch { followRef.Dispose(); throw; }
        }
        public void Refresh()
        {
            float F(string key) => (float)mod.GetDouble(key);
            flightSettings = new() { ThrustToWeight = F("twr"), TopSpeed = F("top_speed"), RateCenter = F("rate_center"), RateMax = F("rate_max"),
                Expo = F("expo"), Sensitivity = F("sensitivity"), MaxAngle = F("max_angle"), AltitudeHold = mod.GetBool("altitude_hold"),
                CineSpeed = F("cine_speed"), FollowTime = F("follow_time"), MaxAcceleration = F("max_accel"), TurnSpeed = F("turn_speed"),
                FollowTarget = mod.GetBool("follow"), Chase = mod.GetBool("chase"), Uptilt = F("uptilt"), Collisions = mod.GetBool("collisions"), Crash = mod.GetBool("crash") };
            var throttle = mod.GetString("throttle");
            inputSettings = new() { Throttle = throttle == "Right trigger" ? Throttle.Trigger : throttle == "Left stick (full range)" ? Throttle.FullRange : Throttle.CentreHover,
                InvertPitch = mod.GetBool("invert"), MouseLook = mod.GetBool("mouse_look"), MouseSensitivity = F("mouse_sens"), Deadzone = F("deadzone"),
                Keys = new[] { "toggle_key", "mode_key", "lock_key", "recall_key", "tilt_up_key", "tilt_down_key", "fov_up_key", "fov_down_key" }.Select(mod.GetKey).ToArray() };
            mode = Enum.TryParse<Mode>(mod.GetString("mode"), out var parsedMode) ? parsedMode : Mode.Angle;
            fov = F("fov"); vibration = F("vibration");
            Drone?.SetMode(mode, flightSettings);
            if (Drone is not null) RebuildEffects();
        }
        void CameraMode(int value)
        {
            var follow = Follow;
            try { SetMode.Invoke(follow.Ptr, (nint)(&value)); } catch (SymbolException) { }
            if (ModeField.Get<int>(follow.Ptr) != value) ModeField.Set(follow.Ptr, value);
        }
        public void Frame(bool requested)
        {
            var follow = Follow;
            var deltaTime = Unity.DeltaTime;
            var unscaledDeltaTime = Il2Cpp.Unbox<float>(Unity.CallStatic(Unity.Class("UnityEngine.Time"), "get_unscaledDeltaTime"));
            unscaledDeltaTime = float.IsFinite(unscaledDeltaTime) ? Math.Clamp(unscaledDeltaTime, 0, 0.1f) : 0;
            var focused = mod.Enabled && Input.GameFocused && !Input.Typing;
            Mouse mouseInput = default;
            if (Drone is not null && focused && mouse is not null)
            {
                try { mouseInput = mouse.Read(); } catch (Exception exception) { mouse = null; mod.Log("mouse look stopped: " + exception.Message); }
            }
            var (controls, actions) = input.Read(unscaledDeltaTime, focused, mouseInput, inputSettings);
            var toggle = requested || actions.Toggle;
            var current = ModeField.Get<int>(follow.Ptr);
            if (Drone is null)
            {
                if (!toggle || !focused || follow.LockInput || follow.PhotoMode || (current != (int)CamState.Follow && current != orbit)) return;
                TakeOff(current);
            }
            else if (toggle || current != orbit || !mod.Enabled) { Land(current == orbit); return; }
            if (Drone is not { } drone) return;
            if (actions.Mode) { mode = (Mode)(((int)drone.Mode + 1) % 3); drone.SetMode(mode, flightSettings); }
            if (drone.Mode != Mode.Cine) flightSettings = flightSettings with { Uptilt = Math.Clamp(flightSettings.Uptilt + actions.Tilt * 5, 0, 60) };
            fov = Math.Clamp(fov + actions.Fov * 5, 50, 130);
            if (actions.Unlock) Unlock();
            if (actions.Lock) PickTarget();
            if (actions.Recall) Recall();
            var paused = !focused || follow.LockInput || !float.IsFinite(deltaTime) || deltaTime < 1e-4f;
            var target = Track(paused ? 0 : deltaTime);
            var report = new Report();
            if (!paused)
            {
                flightTime += MathF.Min(deltaTime, 0.1f);
                report = drone.Step(deltaTime, controls, target, world, flightSettings);
                var rider = Rider();
                var lost = rider is { } r && ((drone.Position - r.Position).Length > 4000 || (report.Ground is null && drone.Position.Y - r.Position.Y < -150));
                if (report.Invalid || lost) Recall();
            }
            var cameraTransform = transform.Target; var cameraObject = camera.Target;
            if (Unity.IsNull(cameraTransform) || Unity.IsNull(cameraObject)) throw new InvalidOperationException("Camera destroyed");
            var position = new Vector3(drone.Position.X, drone.Position.Y, drone.Position.Z);
            var rotation = drone.Camera(flightSettings, vibration); var orientation = new Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W);
            Unity.Call(cameraTransform, "SetPositionAndRotation", (nint)(&position), (nint)(&orientation));
            var fieldOfView = fov; var near = 0.05f;
            Unity.Call(cameraObject, "set_fieldOfView", (nint)(&fieldOfView)); Unity.Call(cameraObject, "set_nearClipPlane", (nint)(&near));
            if (effects is not null)
            {
                try { effects.Update(unscaledDeltaTime, new(drone.Mode, drone.Power(flightSettings), drone.Velocity.Length,
                    world.Sweep(drone.Position, -V3.Up, 300, 0)?.Distance, flightTime, lockedName, drone.Disarmed > 0, paused, input.PadConnected), (float)mod.GetDouble("volume"), report.Crashed); }
                catch (Exception exception) { mod.Log("OSD/audio stopped: " + exception.Message); effects.Dispose(); effects = null; }
            }
        }
        void TakeOff(int current)
        {
            var cameraObject = camera.Target; var cameraTransform = transform.Target;
            if (Unity.IsNull(cameraObject) || Unity.IsNull(cameraTransform)) throw new InvalidOperationException("Camera destroyed");
            var position = Unity.Call<Vector3>(cameraTransform, "get_position"); var orientation = Unity.Call<Quaternion>(cameraTransform, "get_rotation");
            original = (Unity.Call<float>(cameraObject, "get_fieldOfView"), Unity.Call<float>(cameraObject, "get_nearClipPlane"));
            previous = current;
            // Record ownership first, so partial failure also restores the lens and mode.
            Drone = new(new(position.X, position.Y, position.Z), new Quat(orientation.X, orientation.Y, orientation.Z, orientation.W).Normalized, mode);
            if (current != orbit) CameraMode(orbit);
            flightTime = 0; RebuildEffects(); mod.Log("take off in " + mode);
        }
        void RebuildEffects()
        {
            effects?.Dispose(); effects = null;
            try { effects = new Effects(mod.GetBool("hud"), mod.GetBool("sound"), mod.Log); }
            catch (Exception exception) { mod.Log("no OSD/audio: " + exception.Message); }
        }
        public void Land(bool restore)
        {
            try
            {
                var cameraObject = camera.Target;
                if (original is { } lens && !Unity.IsNull(cameraObject))
                {
                    var fieldOfView = lens.Fov; var near = lens.Near;
                    Unity.Call(cameraObject, "set_fieldOfView", (nint)(&fieldOfView)); Unity.Call(cameraObject, "set_nearClipPlane", (nint)(&near));
                }
                if (restore && Drone is not null && !Unity.IsNull(followRef.Target) && ModeField.Get<int>(followRef.Target) == orbit && previous != orbit) CameraMode(previous);
            }
            finally { original = null; Drone = null; Unlock(); effects?.Dispose(); effects = null; }
        }
        Target? Rider()
        {
            var cameraTransform = Follow.Target;
            if (Unity.IsNull(cameraTransform)) return null;
            var position = Unity.Call<Vector3>(cameraTransform, "get_position"); var forward = Unity.Call<Vector3>(cameraTransform, "get_forward");
            var pos = new V3(position.X, position.Y, position.Z);
            return pos.Finite ? new(pos, new(forward.X, forward.Y, forward.Z)) : null;
        }
        void Recall()
        {
            var target = locked is not null ? new Target(last, velocity) : Rider();
            if (target is { } destination) Drone?.Recall(destination.Position, destination.Velocity);
        }
        void Unlock() { locked?.Dispose(); locked = null; lockedName = null; Drone?.Lock(null); }
        void PickTarget()
        {
            if (Drone is not { } drone) return;
            var local = Follow.Target;
            var candidates = new List<(nint Transform, string Name)>();
            if (!Unity.IsNull(local)) candidates.Add((local, "you"));
            // Only visible scene transforms are read for framing; no network state or rider physics is changed.
            var own = new FieldRef("fpv.follow.target_ragdoll").GetObject(Follow.Ptr);
            foreach (var rider in Unity.FindObjects(SymbolTable.Class("player.ragdoll")))
            {
                if (Unity.IsNull(rider) || rider == own) continue;
                var bones = Unity.GetComponentsInChildren(rider, Unity.Class("UnityEngine.Transform"), true);
                var point = new[] { "pelvis", "hips", "hip", "spine", "torso", "root" }
                    .SelectMany(boneName => bones.Where(target => Unity.Name(target).Contains(boneName, StringComparison.OrdinalIgnoreCase))).FirstOrDefault();
                if (point == 0) point = Unity.Transform(rider);
                if (!Unity.IsNull(local) && (Unity.Position(point) - Unity.Position(local)).Magnitude < 1.5f) continue;
                candidates.Add((point, Unity.Name(rider)));
            }
            var list = candidates.Select(candidate =>
            {
                var position = Unity.Position(candidate.Transform); var targetPosition = new V3(position.X, position.Y, position.Z);
                return (candidate.Transform, candidate.Name, Position: targetPosition, Id: Unity.Call<int>(candidate.Transform, "GetInstanceID"),
                    Score: drone.Camera(flightSettings, 0).ForwardDir.Dot((targetPosition - drone.Position).Unit));
            }).Where(candidate => candidate.Position.Finite).OrderByDescending(candidate => candidate.Score).ToList();
            if (list.Count == 0) return;
            var index = locked is null ? 0 : (list.FindIndex(candidate => candidate.Id == lockedId) + 1) % list.Count;
            var selected = list[index];
            Unlock(); locked = new GameRef(selected.Transform); lockedId = selected.Id;
            lockedName = selected.Name; last = selected.Position; velocity = default; drone.Lock(last);
        }
        Target? Track(float deltaTime)
        {
            if (locked is null) return null;
            var cameraTransform = locked.Target;
            if (Unity.IsNull(cameraTransform)) { Unlock(); return null; }
            var position = Unity.Position(cameraTransform); var targetPosition = new V3(position.X, position.Y, position.Z);
            if (!targetPosition.Finite) { Unlock(); return null; }
            if (deltaTime > 1e-4f)
            {
                var moved = targetPosition - last;
                velocity = moved.Length > 80 * deltaTime + 5 ? default : velocity + (moved * (1 / deltaTime) - velocity) * DroneMath.Blend(deltaTime, 0.15f);
            }
            last = targetPosition; return new(targetPosition, velocity);
        }
        public void Dispose()
        {
            try { Land(true); }
            finally { camera.Dispose(); transform.Dispose(); followRef.Dispose(); }
        }
    }
}
