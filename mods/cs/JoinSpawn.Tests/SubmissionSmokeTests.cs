using System.Runtime.CompilerServices;

// Written by the submission bot because the mod came without tests: it checks the mod can be
// loaded and describes itself. Replace it with real tests of the mod's behaviour.
static class SubmissionSmokeTests
{
    [Test]
    static void TheModDescribesItself()
    {
        // Without running the constructor, which may expect the game.
        var mod = (global::JoinSpawn.JoinSpawnMod)RuntimeHelpers.GetUninitializedObject(typeof(global::JoinSpawn.JoinSpawnMod));
        Assert.Equal("join_spawn", mod.Id);
        Assert.True(mod.Name.Length > 0, "the mod has a name");
    }
}
