# Mod update overhead

## Scope

This pass targets repeated discovery and allocation in the public mods and shared C# input
API. It does not reduce rendering quality, skip physics, reduce Rewind's capture rate or
shorten its six-second history. The affected player reported that disabling Rewind stopped
the remaining periodic hitch; there is no live before/after benchmark of these changes yet.

## Changes

- Rewind: stable riding no longer rebuilds the entire Rewired binding inventory every 750 ms.
  Scene/configuration, controller-count and pause/UI context changes refresh the inventory.
  Physics recording reuses scene adapters, target lists, history nodes and scalar buffers.
  Idle frames do not query typing focus or probe XInput slots already covered by Rewired.
- Shared input: typing detection reads the EventSystem's selected object's input field,
  rather than enumerating every TMP input field in the scene once per second. Focus is
  checked live, and retained objects are rooted and validated. The exact non-generic
  GetComponentInParent(Type) overload is used.
- Shared gamepads: indexed reads and automatic selection share connection backoff. Multiple
  mods cannot each probe the same disconnected XInput slot every frame. Connected states
  are shared for at most one millisecond; disconnected slots retry after two seconds.
- Chair Lift: discovery of map lifts and the local rider is retained between proximity
  checks. New maps reset discovery, and a deliberate ride press also refreshes lifts so
  features added after the initial map spawn remain usable. Roots are released on unload.
- Join Spawn: the selected live nameplate is checked locally for name reuse instead of
  rescanning every TMP text in the scene once per second. Missing players retry at most
  once per second. Hidden/replaced nameplates still use the existing discovery fallback.
- SLVSH and Rumble: local spawner/follow-camera lookups retain a validated object and back
  off when absent. Scene/player-spawn events invalidate the lookup, and unload releases it.
- Hank's pack: HUD discovery has a wall-clock limit even when visibility resets its frame
  countdown. The disabled-HUD restore window refreshes once per second. Temporary menu
  visibility hides the existing HUD root instead of destroying/recreating its textures and
  flame objects. Free Look retains its spawner lookup; flame selection avoids a LINQ walk.

Alpine Vision, FPV, Slow Motion, Async Content and Render Distance were inspected and their
regressions were run. Their existing scene scans serve startup, map/settings changes or
explicit player actions; this pass does not change their rendering/physics behavior. Their
XInput callers benefit from the shared SDK backoff.

## Validation and limits

Native-free tests exercise warm recording allocation, independent scalar snapshot buffers,
binding refresh/backoff, existing playback/typing behavior, object-root ownership, dead and
missing object rediscovery, live focus changes and shared XInput polling/hotplug. All public
mod test suites are run, and the Rewind-only release is additionally tested against the
SDK extracted from the signed 0.3.126 runtime. CI builds/packages the complete channel.

These tests check behavior and removal of known repeated work. Actual frame-time gains,
controller reconnect behavior and scene transitions still need observation in the game.
