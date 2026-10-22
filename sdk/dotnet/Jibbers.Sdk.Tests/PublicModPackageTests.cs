using Jibbers.Sdk;

static class PublicModPackageTests
{
    [Test] static void CatalogDiscoveryIncludesOnlyManagedPublicPackages()
    {
        var directory = Path.Combine(Path.GetTempPath(), "jibbers-packages-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            File.Copy(typeof(JibbersMod).Assembly.Location, Path.Combine(directory, "jibbers_fpv.dll"));
            File.Copy(typeof(JibbersMod).Assembly.Location, Path.Combine(directory, "local.dll"));
            File.WriteAllText(Path.Combine(directory, "jibbers_atmosphere.dll"), "native or invalid data");
            var files = PublicModPackages.Files(directory).ToList();
            Assert.Equal(1, files.Count);
            Assert.Equal("jibbers_fpv.dll", Path.GetFileName(files[0]));
        }
        finally { Directory.Delete(directory, true); }
    }
}
