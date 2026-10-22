using Jibbers.Sdk.Interop;

namespace Jibbers.Sdk.Features.Fpv;

sealed unsafe class RewiredMouseInput
{
    readonly Il2CppMethod controllers, ready, mouse, axis, button;
    public RewiredMouseInput()
    {
        var inputClass = Il2Cpp.RequireClass("Rewired_Core.dll", "Rewired.ReInput");
        controllers = inputClass.Method("get_controllers", 0);
        ready = inputClass.Method("get_isReady", 0);
        // Resolve the helper through the public property's return type, not an obfuscated name.
        if (controllers.IsNull || ready.IsNull)
            throw new InvalidOperationException("Rewired API missing");
        var helper = new Il2CppClass(Il2CppApi.class_from_type(Il2CppApi.method_get_return_type(controllers.Ptr)));
        mouse = helper.Method("get_Mouse", 0);
        axis = Il2Cpp.RequireClass("Rewired_Core.dll", "Rewired.ControllerWithAxes").Method("GetAxisRaw", "System.Int32");
        button = Il2Cpp.RequireClass("Rewired_Core.dll", "Rewired.Controller").Method("GetButton", "System.Int32");
        if (controllers.IsNull || ready.IsNull || mouse.IsNull || axis.IsNull || button.IsNull)
            throw new InvalidOperationException("Rewired mouse API missing");
    }

    public MouseInput Read()
    {
        if (!ready.Invoke<bool>(0))
            return default;
        var helper = controllers.Invoke(0);
        if (helper == 0)
            return default;
        var mouseInstance = mouse.Invoke(helper);
        if (mouseInstance == 0)
            return default;
        var horizontalAxis = 0;
        var verticalAxis = 1;
        return new(axis.Invoke<float>(mouseInstance, (nint)(&horizontalAxis)), axis.Invoke<float>(mouseInstance, (nint)(&verticalAxis)), button.Invoke<bool>(mouseInstance, (nint)(&verticalAxis)));
    }
}
