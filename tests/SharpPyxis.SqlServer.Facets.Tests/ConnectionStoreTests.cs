using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace SharpPyxis.SqlServer.Facets.Tests;

[SupportedOSPlatform("windows")]
public sealed class ConnectionStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "SharpPyxis.SqlServer.Facets.Tests", $"connections-{Guid.NewGuid():N}.dat");

    [Fact]
    public void A_file_written_before_the_export_root_reads_with_the_default()
    {
        // The file of version 0.2.0, as it was written: no ExportRoot.
        const string written = """{"NextId":2,"Connections":[{"Id":1,"Server":"SQL01","Database":"shop","Login":null,"Password":null,"Label":"Shop","TrustServerCertificate":true}]}""";
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllBytes(_path, ProtectedData.Protect(Encoding.UTF8.GetBytes(written), null, DataProtectionScope.CurrentUser));

        var entry = Assert.Single(new ConnectionStore(_path).Load());

        Assert.Null(entry.ExportRoot);
        Assert.Equal(ExportLocation.DefaultRoot(entry), ExportLocation.BaseRoot(entry));
    }

    [Fact]
    public void A_declared_export_root_is_kept()
    {
        var store = new ConnectionStore(_path);
        var stored = store.Add(new ConnectionEntry { Id = 0, Server = "SQL01", ExportRoot = @"D:\Exports" });

        Assert.Equal(@"D:\Exports", store.Load().Single(entry => entry.Id == stored.Id).ExportRoot);
    }

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }
}
