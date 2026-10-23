namespace Jibbers.Sdk;

internal static class ModConflicts
{
    public static IEnumerable<JibbersMod> Order(IEnumerable<JibbersMod> candidates) =>
        candidates.OrderByDescending(mod=>mod.Conflicts.Count>0);
    public static JibbersMod? Find(JibbersMod candidate, IEnumerable<JibbersMod> active) =>
        active.FirstOrDefault(other => candidate.Conflicts.Contains(other.Id, StringComparer.Ordinal)
            || other.Conflicts.Contains(candidate.Id, StringComparer.Ordinal));
}
