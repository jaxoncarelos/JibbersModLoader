namespace HankModPack;

internal static class CleanupActions
{
    public static void Run(Action<Exception> report, params Action[] actions)
    {
        foreach (var action in actions)
        {
            try { action(); }
            catch (Exception error) { try { report(error); } catch { } }
        }
    }
}
