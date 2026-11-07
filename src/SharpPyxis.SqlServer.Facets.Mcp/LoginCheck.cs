using Microsoft.Data.SqlClient;

namespace SharpPyxis.SqlServer.Facets.Mcp;

/// <summary>
/// Tells when the login is an administrator of the instance or of the database. This server reads data
/// by design; what the rights must still guarantee is that it reads only the views of facets, and an
/// administrator reads everything.
/// </summary>
internal static class LoginCheck
{
    /// <summary>
    /// Returns why the login behind <paramref name="connectionString"/> is an administrator, or null when
    /// it is not, or when the check could not run.
    /// </summary>
    public static string? Check(string connectionString)
    {
        try
        {
            using var connection = new SqlConnection(connectionString);
            connection.Open();
            return Check(connection);
        }
        catch (SqlException)
        {
            // The tool that needs this connection will report why it cannot open. Silence claims nothing.
            return null;
        }
    }

    /// <summary>Same check, on a connection already open.</summary>
    public static string? Check(SqlConnection connection)
    {
        try
        {
            using var command = new SqlCommand("select is_srvrolemember('sysadmin'), is_member('db_owner');", connection);
            using var reader = command.ExecuteReader();
            reader.Read();

            if (!reader.IsDBNull(0) && reader.GetInt32(0) == 1)
                return "sysadmin";

            return !reader.IsDBNull(1) && reader.GetInt32(1) == 1 ? "db_owner" : null;
        }
        catch (SqlException)
        {
            return null;
        }
    }

    /// <summary>What the person choosing the login, and the model, are told.</summary>
    public static string Warning(string reason) =>
        $"This login is an administrator ({reason}): only the code of this server keeps an agent to the views "
        + "of facets. The login of Create-FacetsLogin.sql makes that guarantee independent of the server.";
}
