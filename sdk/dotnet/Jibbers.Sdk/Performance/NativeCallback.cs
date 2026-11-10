using System.Linq.Expressions;
using System.Reflection;

namespace Jibbers.Sdk;

// Build a strongly typed thunk once, preserving the delegate's native ABI. No object[] or
// reflection invocation on the callback path. Original game work is explicitly excluded.
internal sealed class NativeCallback<T>(T callback, string? owner) where T : Delegate
{
    public T Original = null!;
    public T Build()
    {
        var invoke = typeof(T).GetMethod("Invoke")!;
        var args = invoke.GetParameters().Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name)).ToArray();
        var scope = Expression.Variable(typeof(ModContext.Scope), "ownerScope");
        var enter = typeof(ModContext).GetMethod(nameof(ModContext.Enter))!;
        var enabled = Expression.Call(typeof(ModContext).GetMethod(nameof(ModContext.NativeCallbackAllowed))!, Expression.Constant(owner, typeof(string)));
        var body = Expression.Condition(enabled, Expression.Invoke(Expression.Constant(callback), args),
            Expression.Invoke(Expression.Field(Expression.Constant(this), nameof(Original)), args));
        var block = Expression.Block([scope], Expression.Assign(scope, Expression.Call(enter, Expression.Constant(owner, typeof(string)))),
            Expression.TryFinally(body, Expression.Call(scope, nameof(IDisposable.Dispose), Type.EmptyTypes)));
        return Expression.Lambda<T>(block, args).Compile();
    }
    public static T ExcludeOriginal(T original, string? owner)
    {
        var invoke = typeof(T).GetMethod("Invoke")!;
        var args = invoke.GetParameters().Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name)).ToArray();
        var scope = Expression.Variable(typeof(ModTiming.Scope), "nativeScope");
        var begin = typeof(ModTiming).GetMethod(nameof(ModTiming.Begin))!;
        var block = Expression.Block([scope], Expression.Assign(scope, Expression.Call(begin, Expression.Constant(owner, typeof(string)), Expression.Constant(true))),
            Expression.TryFinally(Expression.Invoke(Expression.Constant(original), args), Expression.Call(scope, nameof(IDisposable.Dispose), Type.EmptyTypes)));
        return Expression.Lambda<T>(block, args).Compile();
    }
}
