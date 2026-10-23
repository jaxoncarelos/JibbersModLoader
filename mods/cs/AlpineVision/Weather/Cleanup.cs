using Jibbers.Sdk;
namespace JibbersAlpineVision.Weather;
/// <summary>Completes every cleanup, even if an earlier resource fails. Cleanup is infrequent.</summary>
internal static class Cleanup
{
    public static void Run(params Action[] actions)
    {
        List<Exception>? failures = null;
        foreach (var action in actions)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                (failures ??= new()).Add(exception);
            }
        }

        if (failures is not null)
            throw new AggregateException("One or more resources could not be released.", failures);
    }
}
