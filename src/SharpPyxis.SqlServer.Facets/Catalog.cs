using Microsoft.Data.SqlClient;

namespace SharpPyxis.SqlServer.Facets;

/// <summary>
/// What the agent may see of one database: the views exposed to the model, their columns, the note
/// of domain and the questions. Read from <c>facets_dict</c> under the login of the server, which is
/// what makes it the intersection of what the login can read and what the dictionary exposes.
/// </summary>
public sealed record Catalog(IReadOnlyList<FacetView> Views, string DomainNote, IReadOnlyList<Question> Questions)
{
    /// <summary>The view of that name, or null. Names compare as SQL Server does by default: case does not matter.</summary>
    public FacetView? FindView(string name) =>
        Views.FirstOrDefault(view => string.Equals(view.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One view of the schema <c>facets</c>, exposed to the model and present in the schema.</summary>
public sealed record FacetView(string Name, string Label, string Answers, IReadOnlyList<FacetColumn> Columns)
{
    /// <summary>The column of that name, or null.</summary>
    public FacetColumn? FindColumn(string name) =>
        Columns.FirstOrDefault(column => string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// One column of a view, present in the view. <see cref="Nature"/> is null while nobody has decided it:
/// the column is shown, so that the agent can say what it lacks, and the composer refuses it. An
/// <see cref="IsExportOnly"/> column is shown by its name, its values never: the composer refuses it,
/// and its <see cref="Values"/> are not read.
/// </summary>
public sealed record FacetColumn(
    string Name, string Label, string Description, Nature? Nature, string SqlType, string Unit, decimal Divisor,
    bool IsExportOnly, IReadOnlyList<CodeValue> Values);

/// <summary>One value a code column takes, and what it means.</summary>
public sealed record CodeValue(string Value, string Label, string Description);

/// <summary>A question the agent asked, with its answer when someone gave one.</summary>
public sealed record Question(long Id, string? ViewName, string? ColumnName, string Text, string Answer, DateTimeOffset AskedAt, DateTimeOffset? AnsweredAt);

/// <summary>Reads the catalog from the dictionary.</summary>
public static class CatalogReader
{
    // One statement, five result sets, in the order the reader consumes them. Everything the model must
    // not see stays out here: a hidden or quarantined view, a quarantined column, and the values of an
    // export only column, which are data. A column without a nature is read, with a null code.
    private const string Sql = """
        select  v.view_id, v.view_name, v.label, v.answers
        from    facets_dict.views as v
        where   v.is_exposed_to_model = 1 and v.is_quarantined = 0
        order by v.view_name;

        select  c.column_id, c.view_id, c.column_name, c.label, c.description, c.nature_code, c.sql_type, c.unit, c.divisor,
                c.is_export_only
        from    facets_dict.columns as c
        join    facets_dict.views as v on v.view_id = c.view_id
        where   v.is_exposed_to_model = 1 and v.is_quarantined = 0
        and     c.is_quarantined = 0
        order by c.view_id, c.ordinal_position;

        select  cv.column_id, cv.code_value, cv.label, cv.description
        from    facets_dict.column_values as cv
        join    facets_dict.columns as c on c.column_id = cv.column_id
        where   c.is_export_only = 0
        order by cv.column_id, cv.code_value;

        select  isnull((select top (1) note from facets_dict.domain_notes where domain_note_id = 1), N'');

        select  q.question_id, v.view_name, c.column_name, q.question, q.answer, q.asked_at, q.answered_at
        from    facets_dict.questions as q
        left join facets_dict.views as v on v.view_id = q.view_id
        left join facets_dict.columns as c on c.column_id = q.column_id
        order by q.question_id;
        """;

    /// <summary>Reads the catalog on an open connection.</summary>
    public static async Task<Catalog> ReadAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        using var command = new SqlCommand(Sql, connection);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var views = new List<(long Id, string Name, string Label, string Answers)>();
        while (await reader.ReadAsync(cancellationToken))
            views.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));

        await reader.NextResultAsync(cancellationToken);
        var columns = new List<(long Id, long ViewId, string Name, string Label, string Description, string? NatureCode, string SqlType, string Unit, decimal Divisor, bool IsExportOnly)>();
        while (await reader.ReadAsync(cancellationToken))
            columns.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                         reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetDecimal(8),
                         reader.GetBoolean(9)));

        await reader.NextResultAsync(cancellationToken);
        var values = new List<(long ColumnId, CodeValue Value)>();
        while (await reader.ReadAsync(cancellationToken))
            values.Add((reader.GetInt64(0), new CodeValue(reader.GetString(1), reader.GetString(2), reader.GetString(3))));

        await reader.NextResultAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var note = reader.GetString(0);

        await reader.NextResultAsync(cancellationToken);
        var questions = new List<Question>();
        while (await reader.ReadAsync(cancellationToken))
            questions.Add(new Question(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3), reader.GetString(4),
                reader.GetDateTimeOffset(5),
                reader.IsDBNull(6) ? null : reader.GetDateTimeOffset(6)));

        var valuesByColumn = values.ToLookup(value => value.ColumnId, value => value.Value);
        var columnsByView = columns.ToLookup(column => column.ViewId, column => new FacetColumn(
            column.Name, column.Label, column.Description, NatureRules.Parse(column.NatureCode), column.SqlType,
            column.Unit, column.Divisor, column.IsExportOnly, [.. valuesByColumn[column.Id]]));

        return new Catalog(
            [.. views.Select(view => new FacetView(view.Name, view.Label, view.Answers, [.. columnsByView[view.Id]]))],
            note,
            questions);
    }
}
