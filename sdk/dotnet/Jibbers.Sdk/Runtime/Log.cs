using System.Text;

namespace Jibbers.Sdk;

/// <summary>Writes to <c>jibbers_hotload.log</c> beside <c>Jibbers.exe</c>.</summary>
/// <remarks>Any thread. Log failures and state changes, not every frame.</remarks>
public static unsafe class Log
{
    /// <summary>One line in the log.</summary>
    public static void Info(string message)
    {
        if (!Runtime.Native.Ready)
        {
            Console.WriteLine(message);
            return;
        }
        var b = Encoding.UTF8.GetBytes(message);
        fixed (byte* p = b)
        {
            Runtime.Native.Api->Log(p, (nuint)b.Length);
        }
    }

    /// <summary>A line prefixed with a source name: <c>[source] message</c>.</summary>
    public static void Info(string source, string message) => Info($"{source}: {message}");

    /// <summary>An exception with its type and message (and the stack trace's first lines).</summary>
    public static void Error(string source, Exception e)
    {
        var trace = (e.StackTrace ?? "").Split('\n').Take(4).Select(l => l.Trim());
        Info($"{source}: {e.GetType().Name}: {e.Message} | {string.Join(" | ", trace)}");
    }
}
