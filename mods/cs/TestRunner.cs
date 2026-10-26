// Minimal test runner shared by the mod test projects (same conventions as Jibbers.Sdk.Tests):
// `dotnet run` runs every [Test] method; the exit code is the number of failures.

using System.Reflection;
using System.Runtime.CompilerServices;

[AttributeUsage(AttributeTargets.Method)]
sealed class TestAttribute : Attribute;

static class Assert
{
    public static void True(bool c, string what = "", [CallerArgumentExpression(nameof(c))] string expr = "")
    {
        if (!c)
        {
            throw new Exception($"expected true: {expr} {what}");
        }
    }

    public static void Equal<T>(T expected, T actual, [CallerArgumentExpression(nameof(actual))] string expr = "")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new Exception($"{expr}: expected {expected}, got {actual}");
        }
    }

    public static void Throws<TE>(Action a, [CallerArgumentExpression(nameof(a))] string expr = "") where TE : Exception
    {
        try
        {
            a();
        }
        catch (TE)
        {
            return;
        }
        throw new Exception($"expected {typeof(TE).Name}: {expr}");
    }
}

static class Program
{
    static int Main()
    {
        var tests = typeof(Program).Assembly.GetTypes().SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(m => m.GetCustomAttribute<TestAttribute>() is not null).OrderBy(m => m.DeclaringType!.Name + m.Name).ToList();
        var failed = 0;
        foreach (var t in tests)
        {
            try
            {
                t.Invoke(null, null);
                Console.WriteLine($"ok    {t.DeclaringType!.Name}.{t.Name}");
            }
            catch (TargetInvocationException e)
            {
                failed++;
                Console.WriteLine($"FAIL  {t.DeclaringType!.Name}.{t.Name}: {e.InnerException?.Message}");
            }
        }
        Console.WriteLine($"{tests.Count - failed} passed, {failed} failed");
        return failed;
    }
}

