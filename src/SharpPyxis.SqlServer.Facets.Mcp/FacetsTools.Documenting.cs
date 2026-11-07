using System.ComponentModel;
using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using ModelContextProtocol.Server;

namespace SharpPyxis.SqlServer.Facets.Mcp;

// The documenting tools: one per procedure of facets_dict, the only way the dictionary is written.
// The author of every write is the MCP client, read from the handshake: the agent does not choose it.
internal sealed partial class FacetsTools
{
    /// <summary>Sets what a view is and the questions it answers.</summary>
    [McpServerTool(Name = "annotate_view", Idempotent = true, Destructive = false)]
    [Description("Writes in the dictionary of the SQL Server database what a view is: its label, the questions it "
               + "answers, whether the agent may see it. Call it on the word of the user, with what they said; a "
               + "parameter left out changes nothing.")]
    public Task<string> AnnotateView(
        McpServer server,
        [Description("Name of the view, as list_views gives it.")] string view,
        [Description("What the view is, in a few words.")] string? label = null,
        [Description("The questions the view answers, in the words of a request.")] string? answers = null,
        [Description("False hides the view from the agent while it is being annotated.")] bool? exposedToModel = null,
        CancellationToken cancellationToken = default) =>
        RunProcedureAsync("facets_dict.annotate_view", new()
        {
            ["@view_name"] = view, ["@author"] = Author(server), ["@label"] = label, ["@answers"] = answers,
            ["@is_exposed_to_model"] = exposedToModel,
        }, $"View '{view}' annotated. describe_view shows it as it is now.", cancellationToken);

    /// <summary>Sets what a column is: its nature first, then what the nature allows.</summary>
    [McpServerTool(Name = "annotate_column", Idempotent = true, Destructive = false)]
    [Description("Writes in the dictionary of the SQL Server database what a column of a view is: its nature, "
               + "which decides what the server allows on it, its label and description, its unit and divisor "
               + "for a measure, whether the agent may see it, whether it is sensitive. Call it on the word of "
               + "the user; a parameter left out changes nothing. A column without a nature cannot be queried.")]
    public Task<string> AnnotateColumn(
        McpServer server,
        [Description("Name of the view, as list_views gives it.")] string view,
        [Description("Name of the column, as describe_view gives it.")] string column,
        [Description("amount, quantity, rate, date, code, label, identifier, boolean or text.")] string? nature = null,
        [Description("What the column is, in a few words.")] string? label = null,
        [Description("What the agent must know to read it right: what it counts, what it excludes, how it is computed.")] string? description = null,
        [Description("The unit of a measure, as shown to the agent: EUR, kg, %. Measures only.")] string? unit = null,
        [Description("What the stored value is divided by before it is shown: 100 for cents stored as integers. Amounts and quantities only.")] decimal? divisor = null,
        [Description("False keeps the column out of what the agent sees and queries.")] bool? exposedToModel = null,
        [Description("True for a value that may go to a file on the workstation, never to the model. A sensitive column cannot be exposed.")] bool? sensitive = null,
        CancellationToken cancellationToken = default)
    {
        if (nature is { Length: > 0 } && NatureRules.Parse(nature) is null)
            return Task.FromResult(_active?.Stamp(UnknownNature(nature)) ?? NoConnection);

        return RunProcedureAsync("facets_dict.annotate_column", new()
        {
            ["@view_name"] = view, ["@column_name"] = column, ["@author"] = Author(server),
            ["@label"] = label, ["@description"] = description, ["@nature_code"] = nature?.ToLowerInvariant(),
            ["@unit"] = unit, ["@divisor"] = divisor, ["@is_exposed_to_model"] = exposedToModel, ["@is_sensitive"] = sensitive,
        }, $"Column '{column}' of '{view}' annotated. describe_view shows it as it is now.", cancellationToken);
    }

    /// <summary>Sets the meaning of one value of a code column.</summary>
    [McpServerTool(Name = "annotate_value", Idempotent = true, Destructive = false)]
    [Description("Writes in the dictionary of the SQL Server database what one value of a code column means, so "
               + "that a status asked in words becomes the value stored. Creates the entry, or updates it. Call "
               + "it on the word of the user.")]
    public Task<string> AnnotateValue(
        McpServer server,
        [Description("Name of the view, as list_views gives it.")] string view,
        [Description("Name of the code column, as describe_view gives it.")] string column,
        [Description("The value as stored in the column.")] string value,
        [Description("What the value means, in a few words.")] string? label = null,
        [Description("What the agent must know about it.")] string? description = null,
        CancellationToken cancellationToken = default) =>
        RunProcedureAsync("facets_dict.annotate_value", new()
        {
            ["@view_name"] = view, ["@column_name"] = column, ["@code_value"] = value, ["@author"] = Author(server),
            ["@label"] = label, ["@description"] = description,
        }, $"Value '{value}' of '{column}' annotated.", cancellationToken);

    /// <summary>Replaces the note of domain.</summary>
    [McpServerTool(Name = "write_domain_note", Idempotent = true, Destructive = false)]
    [Description("Replaces, in the dictionary of the SQL Server database, the note that tells an agent what it "
               + "must know about the data as a whole: the business, the conventions, the calendar. The note is "
               + "replaced whole: read it first, and write it again with the change. Call it on the word of the user.")]
    public Task<string> WriteDomainNote(
        McpServer server,
        [Description("The whole note, at most 4000 characters.")] string note,
        CancellationToken cancellationToken = default) =>
        RunProcedureAsync("facets_dict.write_domain_note", new() { ["@note"] = note, ["@author"] = Author(server) },
            "Note of domain written.", cancellationToken);

    /// <summary>Records a question for the owner of the data.</summary>
    [McpServerTool(Name = "ask_question", Idempotent = false, Destructive = false)]
    [Description("Records in the dictionary of the SQL Server database a question the dictionary does not answer, "
               + "about the data in general, a view, or a column of a view, for the owner of the data to answer. "
               + "Ask the user first: record only what they could not answer. Returns the id of the question.")]
    public async Task<string> AskQuestion(
        McpServer server,
        [Description("The question, as the owner of the data will read it.")] string question,
        [Description("The view the question is about, if any.")] string? view = null,
        [Description("The column the question is about, if any; its view is then required.")] string? column = null,
        CancellationToken cancellationToken = default)
    {
        if (_active is not { } active)
            return NoConnection;

        try
        {
            using var connection = new SqlConnection(active.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            using var command = Procedure(connection, "facets_dict.ask_question", new()
            {
                ["@question"] = question, ["@author"] = Author(server), ["@view_name"] = view, ["@column_name"] = column,
            });
            var id = await command.ExecuteScalarAsync(cancellationToken);
            return active.Stamp($"Question #{id} recorded. The owner answers it with answer_question; describe_view and read_domain_note show it meanwhile.");
        }
        catch (SqlException exception)
        {
            return active.Stamp(Explain(exception));
        }
    }

    /// <summary>Records the answer to a question.</summary>
    [McpServerTool(Name = "answer_question", Idempotent = true, Destructive = false)]
    [Description("Records in the dictionary of the SQL Server database the answer to a question, by its id, as the "
               + "user gave it. An answer given again replaces the previous one. Call it on the word of the user.")]
    public Task<string> AnswerQuestion(
        McpServer server,
        [Description("The id of the question, as ask_question, describe_view or read_domain_note give it.")] long questionId,
        [Description("The answer, in the words of the user.")] string answer,
        CancellationToken cancellationToken = default) =>
        RunProcedureAsync("facets_dict.answer_question", new()
        {
            ["@question_id"] = questionId, ["@answer"] = answer, ["@author"] = Author(server),
        }, $"Question #{questionId} answered.", cancellationToken);

    /// <summary>Aligns the dictionary on the views of the schema facets.</summary>
    [McpServerTool(Name = "refresh_catalog", Idempotent = true, Destructive = false)]
    [Description("Aligns the dictionary of the SQL Server database on the views of the schema facets: a view or a "
               + "column seen for the first time gets its entry, one that disappeared is quarantined with its "
               + "annotations kept, nothing is deleted. Call it when the user says a view was written, changed or "
               + "dropped. Returns the counts.")]
    public async Task<string> RefreshCatalog(McpServer server, CancellationToken cancellationToken = default)
    {
        if (_active is not { } active)
            return NoConnection;

        try
        {
            using var connection = new SqlConnection(active.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            using var command = Procedure(connection, "facets_dict.refresh_catalog", new() { ["@author"] = Author(server) });
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);

            return active.Stamp(
                $"Views: {reader.GetInt32(1)} present, {reader.GetInt32(0)} added, {reader.GetInt32(2)} quarantined. "
                + $"Columns: {reader.GetInt32(4)} present, {reader.GetInt32(3)} added, {reader.GetInt32(5)} quarantined. "
                + "A new view or column has no label and no nature yet: annotate it before it is queried.");
        }
        catch (SqlException exception)
        {
            return active.Stamp(Explain(exception));
        }
    }

    private async Task<string> RunProcedureAsync(string procedure, Dictionary<string, object?> parameters, string done, CancellationToken cancellationToken)
    {
        if (_active is not { } active)
            return NoConnection;

        try
        {
            using var connection = new SqlConnection(active.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            using var command = Procedure(connection, procedure, parameters);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return active.Stamp(done);
        }
        catch (SqlException exception)
        {
            return active.Stamp(Explain(exception));
        }
    }

    // A null parameter is sent as null: the procedure leaves the column as it is.
    private static SqlCommand Procedure(SqlConnection connection, string name, Dictionary<string, object?> parameters)
    {
        var command = new SqlCommand(name, connection) { CommandType = CommandType.StoredProcedure };
        foreach (var (parameter, value) in parameters)
            command.Parameters.AddWithValue(parameter, value ?? DBNull.Value);
        return command;
    }

    // The client that drives the agent, as it introduced itself at the handshake. Bounded as the column is.
    private static string Author(McpServer server)
    {
        var client = server.ClientInfo;
        var author = client is null ? "mcp client" : $"{client.Name} {client.Version}".Trim();
        return author.Length <= 128 ? author : author[..128];
    }

    private static string UnknownNature(string nature) =>
        $"'{nature}' is not a nature: use {string.Join(", ", Enum.GetValues<Nature>().Select(NatureRules.Code))}.";

    // What a procedure or a constraint refused, in words the agent can act on. 50001 is the procedures'
    // own error, already worded; 547 is a check or a foreign key, named in the message and translated here.
    private static string Explain(SqlException exception) => Explain(exception.Number, exception.Message);

    internal static string Explain(int number, string message)
    {
        if (number == 50001)
            return $"Refused: {message}";

        if (number == 547 && ConstraintName().Match(message) is { Success: true } match)
            return "Refused: " + match.Groups[1].Value switch
            {
                "ck_columns_unit_on_measure" => "a unit applies to an amount, a quantity or a rate only.",
                "ck_columns_divisor_on_measure" => "a divisor applies to an amount or a quantity only.",
                "ck_columns_divisor_positive" => "the divisor must be positive.",
                "ck_columns_sensitive_not_exposed" => "a sensitive column cannot be exposed to the model: hide it, or make it not sensitive.",
                "fk_columns_nature" => UnknownNature("that"),
                "ck_questions_question_len" => "the question is empty.",
                "ck_views_view_name_len" or "ck_columns_column_name_len" => "the name is empty.",
                var name => $"the database refused it ({name}).",
            };

        return $"Failed: {message}";
    }

    [GeneratedRegex("constraint \"([^\"]+)\"")]
    private static partial Regex ConstraintName();
}
