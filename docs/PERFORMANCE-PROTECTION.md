# Performance protection

The SDK includes **Performance protection** in Options > Mods. **Pause costly mods
automatically** defaults on. When a mod is suspended, an on-screen notice names it and
explains that it returns next game launch. The mod's saved settings and enabled choice are
not changed. Its options report the session suspension and cannot call unloaded resources.

Protection uses measured, exclusive game-thread callback time across SDK hooks, events,
queued main-thread jobs and supported typed detours. Shared feature dispatchers can use
`ModCallbacks.Invoke(owner, callback, argument)` to preserve each feature's attribution.
Calls to a detour's native original, nested owners and .NET GC pauses are excluded from
the caller's charge. Warm timing scopes do not allocate samples.

The default decision requires at least four frames of 80 ms or worse in a five-second
window, with an optional mod accounting for at least 35% (and 20 ms) of three stalled
frames and at least 150 ms of measured work. Only the most costly eligible mod is paused;
there is a 12-second cooldown before another decision. Startup warms up for 20 seconds.
Scene/spawn changes, lost focus, pause and loading gaps reset a 15-second grace period.

Networking/party services, Async Content, non-toggleable coordinators, active party-channel
owners and mods with untracked native detours are protected. A mod can explicitly opt out
with `AllowPerformanceSuspension => false`. This is a C# callback safeguard: GPU-only,
base-game, uninstrumented native and unattributed GC stalls do not justify guessing which
mod to disable.

Suspension first gates future callbacks and disables owned native entry points without
freeing trampolines. Cleanup waits at least two frames and until tracked callbacks/native
originals have unwound. It then uses normal SDK unload cleanup once, including camera,
input, event, HUD and resource ownership cleanup. Queued game work from the suspended mod
is cancelled; synchronous unload cleanup can still run. The next process launch loads the
saved enabled setting normally.

Native-free regression tests cover sustained stalls, loading/focus grace, healthy frames,
protected work, cooldown, original-call exclusion, nested attribution, native callback ABI,
manual disabled-state compatibility, deferred cleanup, queued-work cancellation, failed
suspension and settings persistence across a simulated next launch. In-game frame-time
improvement still needs measurement on affected hardware.
