namespace SharpPyxis.SqlServer.Facets.Tests;

public sealed class ExportLocationTests
{
    [Theory]
    [InlineData("Customers 2026", "Customers 2026")]
    [InlineData(@"(localdb)\MSSQLLocalDB", "(localdb)_MSSQLLocalDB")]
    [InlineData("Sales: review?", "Sales_ review_")]
    [InlineData("a/b", "a_b")]
    [InlineData("  .hidden.  ", "hidden")]
    [InlineData("CON", "CON_")]
    [InlineData("con.csv", "con_.csv")]
    [InlineData("Console", "Console")]
    public void A_name_keeps_one_level_and_what_Windows_accepts(string name, string expected)
    {
        Assert.Equal(expected, ExportLocation.FolderName(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData(" . ")]
    public void A_name_with_nothing_left_is_null(string name)
    {
        Assert.Null(ExportLocation.FolderName(name));
    }

    [Fact]
    public void A_long_name_is_cut()
    {
        Assert.Equal(ExportLocation.MaxNameLength, ExportLocation.FolderName(new string('x', 300))!.Length);
    }

    [Fact]
    public void The_default_root_is_named_after_the_label_or_else_the_server()
    {
        var facets = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Facets");

        Assert.Equal(Path.Combine(facets, "Shop"), ExportLocation.DefaultRoot(Entry() with { Label = "Shop" }));
        Assert.Equal(Path.Combine(facets, "SQL01_PROD"), ExportLocation.DefaultRoot(Entry()));
    }

    [Fact]
    public void A_connection_naming_a_server_only_has_a_folder_per_database()
    {
        var declared = Entry() with { ExportRoot = @"D:\Exports" };

        Assert.Equal(@"D:\Exports", ExportLocation.Root(declared with { Database = "shop" }, "shop"));
        Assert.Equal(Path.Combine(@"D:\Exports", "shop"), ExportLocation.Root(declared, "shop"));
    }

    private static ConnectionEntry Entry() => new() { Id = 1, Server = @"SQL01\PROD" };
}
