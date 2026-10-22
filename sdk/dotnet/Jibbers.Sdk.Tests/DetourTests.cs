using System.Runtime.InteropServices;
using Jibbers.Sdk;
using Jibbers.Sdk.Runtime;

// A native-table fixture exercises real delegate/function-pointer transitions without patching game code.
static unsafe class DetourTests
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void Step(nint instance, nint method);

    sealed class Bridge : IDisposable
    {
        readonly NativeApi* previous;
        readonly NativeApi* api;
        public int Created, Enabled, Destroyed, OriginalCalls, Logged;
        public nint Instance, Method, Replacement;
        public bool FailEnable;
        public Bridge()
        {
            active = this;
            previous = Native.Api;
            api = (NativeApi*)NativeMemory.AllocZeroed((nuint)sizeof(NativeApi));
            api->Version = 1;
            api->DetourCreate = &Create; api->DetourEnable = &Enable;
            api->DetourDisable = &Disable; api->DetourDestroy = &Destroy;
            api->Log = &Log; api->LastError = &Error;
            Native.Api = api;
        }
        public void Call(nint instance = 17, nint method = 29)
            => ((delegate* unmanaged<nint, nint, void>)Replacement)(instance, method);
        public void Dispose() { Native.Api = previous; NativeMemory.Free(api); active = null; }
    }
    static Bridge? active;
    [UnmanagedCallersOnly] static nint Create(nint target, nint replacement, nint* original)
    {
        active!.Created++; active.Replacement = replacement;
        *original = (nint)(delegate* unmanaged<nint, nint, void>)&Original;
        return 1;
    }
    [UnmanagedCallersOnly] static int Enable(nint handle) { active!.Enabled++; return active.FailEnable ? 1 : 0; }
    [UnmanagedCallersOnly] static int Disable(nint handle) => 0;
    [UnmanagedCallersOnly] static void Destroy(nint handle) => active!.Destroyed++;
    [UnmanagedCallersOnly] static void Log(byte* text, nuint length) => active!.Logged++;
    [UnmanagedCallersOnly] static nuint Error(byte* text, nuint capacity) => 0;
    [UnmanagedCallersOnly] static void Original(nint instance, nint method)
    {
        active!.OriginalCalls++; active.Instance = instance; active.Method = method;
    }

    [Test] static void AroundCanContinueOrSkipAndForwardsTheRealInvocation()
    {
        using var bridge = new Bridge();
        var skip = false;
        using var hook = MethodHook.Create(123, (instance, original) =>
        {
            Assert.Equal((nint)17, instance);
            if (!skip) original();
        });
        Assert.True(hook.Enabled);
        bridge.Call(); Assert.Equal(1, bridge.OriginalCalls);
        Assert.Equal((nint)17, bridge.Instance); Assert.Equal((nint)29, bridge.Method);
        skip = true; bridge.Call(); Assert.Equal(1, bridge.OriginalCalls);
        hook.Disable(); Assert.True(!hook.Enabled); hook.Enable(); Assert.True(hook.Enabled);
    }
    [Test] static void ExceptionsFallThroughOnceAndStopCallingTheFaultedHandler()
    {
        using var bridge = new Bridge(); var calls = 0;
        using var hook = MethodHook.Create(123, (_, _) => { calls++; throw new Exception("test fault"); });
        bridge.Call(); bridge.Call();
        Assert.Equal(2, bridge.OriginalCalls); Assert.Equal(1, calls); Assert.Equal(1, bridge.Logged);
    }
    [Test] static void ErrorAfterCallingOriginalDoesNotRunTheGameMethodTwice()
    {
        using var bridge = new Bridge();
        using var hook = MethodHook.Create(123, (_, original) => { original(); throw new Exception("after original"); });
        bridge.Call(); Assert.Equal(1, bridge.OriginalCalls);
        bridge.Call(); Assert.Equal(2, bridge.OriginalCalls);
    }
    [Test] static void OriginalCannotEscapeItsInvocation()
    {
        using var bridge = new Bridge(); Action? escaped = null;
        using var hook = MethodHook.Create(123, (_, original) => escaped = original);
        bridge.Call(); Assert.Throws<InvalidOperationException>(() => escaped!());
        Assert.Equal(0, bridge.OriginalCalls);
    }
    [Test] static void OriginalCannotBeMovedToAnotherThread()
    {
        using var bridge = new Bridge();
        using var hook = MethodHook.Create(123, (_, original) =>
        {
            Exception? error = null;
            Task.Run(() => { try { original(); } catch (Exception e) { error = e; } }).GetAwaiter().GetResult();
            Assert.True(error is InvalidOperationException);
            original();
        });
        bridge.Call(); Assert.Equal(1, bridge.OriginalCalls); Assert.Equal(0, bridge.Logged);
    }
    sealed class TestMod : JibbersMod
    {
        public override string Id => "around_lifecycle_test";
        public override string Name => "Around lifecycle test";
    }
    [Test] static void DisabledModsBypassTheHandlerAndOwnershipSupportsUnloadCleanup()
    {
        using var bridge = new Bridge(); var calls = 0;
        var mod = new TestMod(); mod.Store = new SettingsStore(mod.Settings, mod.Name, null);
        ModContext.Add(mod);
        var hook = ModContext.With(mod, () => MethodHook.Create(123, (_, original) => { calls++; original(); }));
        try
        {
            bridge.Call(); Assert.Equal(1, calls);
            mod.Store.Set("enabled", System.Text.Json.Nodes.JsonValue.Create(false));
            bridge.Call(); Assert.Equal(1, calls); Assert.Equal(2, bridge.OriginalCalls);
            Detour.RemoveOwner(mod.Id); Assert.Equal(1, bridge.Destroyed); Assert.True(!hook.Enabled);
        }
        finally { hook.Dispose(); }
        Assert.Equal(1, bridge.Destroyed);
    }
    [Test] static void TypedOriginalIsCallableAndDisposedDetoursCannotBeReenabled()
    {
        using var bridge = new Bridge();
        using var hook = Detour<Step>.Create(123, (_, _) => { });
        Assert.True(!hook.Enabled); hook.Enable();
        hook.Original(37, 41);
        Assert.Equal(1, bridge.OriginalCalls); Assert.Equal((nint)37, bridge.Instance); Assert.Equal((nint)41, bridge.Method);
        hook.Dispose();
        Assert.Throws<ObjectDisposedException>(() => hook.Enable());
        Assert.Throws<ObjectDisposedException>(() => _ = hook.Original);
        Assert.Equal(1, bridge.Destroyed);
    }
    [Test] static void FailedEnableReleasesTheNativeHook()
    {
        using var bridge = new Bridge { FailEnable = true };
        Assert.Throws<InvalidOperationException>(() => MethodHook.Create(123, (_, _) => { }));
        Assert.Equal(1, bridge.Created); Assert.Equal(1, bridge.Destroyed);
    }
    [Test] static void SimpleHooksRejectUnsupportedSignatures()
    {
        MethodHook.ValidateSignature(false, "System.Void", 0);
        Assert.Throws<ArgumentException>(() => MethodHook.ValidateSignature(true, "System.Void", 0));
        Assert.Throws<ArgumentException>(() => MethodHook.ValidateSignature(false, "System.Boolean", 0));
        Assert.Throws<ArgumentException>(() => MethodHook.ValidateSignature(false, "System.Void", 1));
    }
    [Test] static void TypedFactoriesFailWithAnOrdinaryErrorOutsideTheGame()
    {
        Assert.True(!Native.Ready);
        Assert.Throws<InvalidOperationException>(() => Detour<Step>.Create(123, (_, _) => { }));
        Assert.Throws<InvalidOperationException>(() => Detour.Create<Step>(123, (_, _) => { }));
    }
}
