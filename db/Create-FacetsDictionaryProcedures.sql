-- Create-FacetsDictionaryProcedures.sql
--
-- Creates the procedures through which the dictionary of facets_dict is written. The login the server
-- uses holds EXECUTE on them and no right on the tables: the procedures belong to dbo, as the tables
-- do, and write them through ownership chaining. None of them uses dynamic SQL, which would break that
-- chain. None of them deletes a row.
--
-- Every procedure takes @author, the name of whoever writes: the server passes the name of the MCP
-- client that drives it. A parameter left null leaves the column as it is, so that an annotation
-- changes one thing without restating the others. A procedure that cannot find what it is asked to
-- annotate stops with error 50001 and names it.
--
-- The procedures close and never open: they hide a view and make a column export only, and stop with
-- error 50002 when asked the reverse. Showing a view again, or opening a column to queries, is the
-- designer's gesture, an update of the table under their own login.
--
-- Run it after Create-FacetsDictionary.sql, as an administrator of the database, and again at every
-- version of the product: create or alter replaces each procedure in place.

set nocount on;
go

if schema_id(N'facets_dict') is null
begin
    raiserror(N'The schema facets_dict does not exist: run Create-FacetsDictionary.sql first.', 16, 1);
    set noexec on;
end;
go

-- ── refresh_catalog ──────────────────────────────────────────────────────────────────────────────────
-- Reads the views of the schema facets and their columns from the catalog, and aligns the dictionary
-- on them: a view or a column seen for the first time gets its row; one seen again is marked seen
-- and leaves quarantine if it was there; one not found is quarantined, with its columns. Nothing is
-- deleted, nothing written by hand is touched: label, description, nature and the rest stay as they
-- are. Returns one row of counts.
create or alter procedure facets_dict.refresh_catalog
    @author nvarchar(128)
as
begin
    set nocount on;
    declare @now datetimeoffset(3) = sysdatetimeoffset();

    -- The catalog, as the dictionary stores it. The type is written as it would be declared.
    declare @catalog table
    (
        view_name        nvarchar(128) not null,
        column_name      nvarchar(128) not null,
        ordinal_position int           not null,
        sql_type         nvarchar(128) not null,
        primary key (view_name, column_name)
    );

    insert into @catalog (view_name, column_name, ordinal_position, sql_type)
    select  v.name,
            c.name,
            c.column_id,
            type_name(c.user_type_id)
            + case
                  when type_name(c.system_type_id) in (N'nvarchar', N'nchar')
                      then N'(' + case when c.max_length = -1 then N'max' else cast(c.max_length / 2 as nvarchar(10)) end + N')'
                  when type_name(c.system_type_id) in (N'varchar', N'char', N'binary', N'varbinary')
                      then N'(' + case when c.max_length = -1 then N'max' else cast(c.max_length as nvarchar(10)) end + N')'
                  when type_name(c.system_type_id) in (N'decimal', N'numeric')
                      then N'(' + cast(c.precision as nvarchar(10)) + N', ' + cast(c.scale as nvarchar(10)) + N')'
                  when type_name(c.system_type_id) in (N'datetime2', N'datetimeoffset', N'time')
                      then N'(' + cast(c.scale as nvarchar(10)) + N')'
                  else N''
              end
    from    sys.views as v
    join    sys.columns as c on c.object_id = v.object_id
    where   v.schema_id = schema_id(N'facets');

    declare @views_added int, @views_quarantined int, @columns_added int, @columns_quarantined int;

    -- Views: new ones, seen ones, gone ones.
    insert into facets_dict.views (view_name, last_seen_at, created_by)
    select  distinct k.view_name, @now, @author
    from    @catalog as k
    where   not exists (select 1 from facets_dict.views as w where w.view_name = k.view_name);
    set @views_added = @@rowcount;

    update  w
    set     last_seen_at = @now, is_quarantined = 0, quarantined_at = null
    from    facets_dict.views as w
    where   exists (select 1 from @catalog as k where k.view_name = w.view_name);

    update  w
    set     is_quarantined = 1, quarantined_at = @now
    from    facets_dict.views as w
    where   w.is_quarantined = 0
    and     not exists (select 1 from @catalog as k where k.view_name = w.view_name);
    set @views_quarantined = @@rowcount;

    -- Columns: the same, inside each view. A column of a quarantined view is quarantined with it.
    insert into facets_dict.columns (view_id, column_name, ordinal_position, sql_type, last_seen_at, created_by)
    select  w.view_id, k.column_name, k.ordinal_position, k.sql_type, @now, @author
    from    @catalog as k
    join    facets_dict.views as w on w.view_name = k.view_name
    where   not exists (select 1 from facets_dict.columns as c
                        where c.view_id = w.view_id and c.column_name = k.column_name);
    set @columns_added = @@rowcount;

    update  c
    set     ordinal_position = k.ordinal_position, sql_type = k.sql_type,
            last_seen_at = @now, is_quarantined = 0, quarantined_at = null
    from    facets_dict.columns as c
    join    facets_dict.views as w on w.view_id = c.view_id
    join    @catalog as k on k.view_name = w.view_name and k.column_name = c.column_name;

    update  c
    set     is_quarantined = 1, quarantined_at = @now
    from    facets_dict.columns as c
    join    facets_dict.views as w on w.view_id = c.view_id
    where   c.is_quarantined = 0
    and     not exists (select 1 from @catalog as k
                        where k.view_name = w.view_name and k.column_name = c.column_name);
    set @columns_quarantined = @@rowcount;

    select  @views_added as views_added,
            (select count(*) from facets_dict.views where is_quarantined = 0) as views_present,
            @views_quarantined as views_quarantined,
            @columns_added as columns_added,
            (select count(*) from facets_dict.columns where is_quarantined = 0) as columns_present,
            @columns_quarantined as columns_quarantined;
end;
go

-- ── annotate_view ────────────────────────────────────────────────────────────────────────────────────
-- What a view is and the questions it answers. A null parameter leaves the column as it is.
-- @is_exposed_to_model takes 0 only: the view is hidden, and only the designer shows it again.
create or alter procedure facets_dict.annotate_view
    @view_name           nvarchar(128),
    @author              nvarchar(128),
    @label               nvarchar(200) = null,
    @answers             nvarchar(2000) = null,
    @is_exposed_to_model bit = null
as
begin
    set nocount on;
    if not exists (select 1 from facets_dict.views where view_name = @view_name)
        throw 50001, N'The view is not in the dictionary: run refresh_catalog, or check its name.', 1;

    if @is_exposed_to_model = 1
        throw 50002, N'Only the designer shows a hidden view again, under their own login: the agent may only hide a view.', 1;

    update  facets_dict.views
    set     label               = isnull(@label, label),
            answers             = isnull(@answers, answers),
            is_exposed_to_model = isnull(@is_exposed_to_model, is_exposed_to_model),
            modified_at         = sysdatetimeoffset(),
            modified_by         = @author
    where   view_name = @view_name;
end;
go

-- ── annotate_column ──────────────────────────────────────────────────────────────────────────────────
-- What a column is: its nature first, then what the nature allows. A null parameter leaves the column
-- as it is. The checks of the table refuse a unit or a divisor outside a measure. @is_export_only
-- takes 1 only: the column is closed, and only the designer opens it.
create or alter procedure facets_dict.annotate_column
    @view_name           nvarchar(128),
    @column_name         nvarchar(128),
    @author              nvarchar(128),
    @label               nvarchar(200) = null,
    @description         nvarchar(2000) = null,
    @nature_code         nvarchar(32) = null,
    @unit                nvarchar(20) = null,
    @divisor             decimal(18, 6) = null,
    @is_export_only      bit = null
as
begin
    set nocount on;
    declare @column_id bigint;
    select  @column_id = c.column_id
    from    facets_dict.columns as c
    join    facets_dict.views as w on w.view_id = c.view_id
    where   w.view_name = @view_name and c.column_name = @column_name;

    if @column_id is null
        throw 50001, N'The column is not in the dictionary: run refresh_catalog, or check the names of the view and the column.', 1;

    if @is_export_only = 0
        throw 50002, N'Only the designer opens a column to queries, under their own login: the agent may only make it export only.', 1;

    update  facets_dict.columns
    set     label               = isnull(@label, label),
            description         = isnull(@description, description),
            nature_code         = isnull(@nature_code, nature_code),
            unit                = isnull(@unit, unit),
            divisor             = isnull(@divisor, divisor),
            is_export_only      = isnull(@is_export_only, is_export_only),
            modified_at         = sysdatetimeoffset(),
            modified_by         = @author
    where   column_id = @column_id;
end;
go

-- ── annotate_value ───────────────────────────────────────────────────────────────────────────────────
-- The meaning of one value of a code column. Creates the row, or updates it.
create or alter procedure facets_dict.annotate_value
    @view_name   nvarchar(128),
    @column_name nvarchar(128),
    @code_value  nvarchar(100),
    @author      nvarchar(128),
    @label       nvarchar(200) = null,
    @description nvarchar(400) = null
as
begin
    set nocount on;
    declare @column_id bigint;
    select  @column_id = c.column_id
    from    facets_dict.columns as c
    join    facets_dict.views as w on w.view_id = c.view_id
    where   w.view_name = @view_name and c.column_name = @column_name;

    if @column_id is null
        throw 50001, N'The column is not in the dictionary: run refresh_catalog, or check the names of the view and the column.', 1;

    if exists (select 1 from facets_dict.column_values where column_id = @column_id and code_value = @code_value)
        update  facets_dict.column_values
        set     label       = isnull(@label, label),
                description = isnull(@description, description),
                modified_at = sysdatetimeoffset(),
                modified_by = @author
        where   column_id = @column_id and code_value = @code_value;
    else
        insert into facets_dict.column_values (column_id, code_value, label, description, created_by)
        values (@column_id, @code_value, isnull(@label, N''), isnull(@description, N''), @author);
end;
go

-- ── write_domain_note ────────────────────────────────────────────────────────────────────────────────
-- The one note of the database, replaced whole: it is short, and read at every start.
create or alter procedure facets_dict.write_domain_note
    @note   nvarchar(4000),
    @author nvarchar(128)
as
begin
    set nocount on;
    if exists (select 1 from facets_dict.domain_notes where domain_note_id = 1)
        update  facets_dict.domain_notes
        set     note = @note, modified_at = sysdatetimeoffset(), modified_by = @author
        where   domain_note_id = 1;
    else
        insert into facets_dict.domain_notes (domain_note_id, note, created_by)
        values (1, @note, @author);
end;
go

-- ── ask_question ─────────────────────────────────────────────────────────────────────────────────────
-- A question about the data in general, a view, or a column of a view. Returns its id, which the
-- answer will name.
create or alter procedure facets_dict.ask_question
    @question    nvarchar(2000),
    @author      nvarchar(128),
    @view_name   nvarchar(128) = null,
    @column_name nvarchar(128) = null
as
begin
    set nocount on;
    declare @view_id bigint, @column_id bigint;

    if @view_name is not null
    begin
        select @view_id = view_id from facets_dict.views where view_name = @view_name;
        if @view_id is null
            throw 50001, N'The view is not in the dictionary: run refresh_catalog, or check its name.', 1;
    end;

    if @column_name is not null
    begin
        if @view_id is null
            throw 50001, N'A question about a column names its view.', 1;
        select @column_id = column_id from facets_dict.columns where view_id = @view_id and column_name = @column_name;
        if @column_id is null
            throw 50001, N'The column is not in the dictionary: run refresh_catalog, or check the names of the view and the column.', 1;
    end;

    insert into facets_dict.questions (view_id, column_id, question, asked_by)
    values (@view_id, @column_id, @question, @author);

    select scope_identity() as question_id;
end;
go

-- ── answer_question ──────────────────────────────────────────────────────────────────────────────────
-- The answer to a question, by its id. An answer given again replaces the previous one.
create or alter procedure facets_dict.answer_question
    @question_id bigint,
    @answer      nvarchar(2000),
    @author      nvarchar(128)
as
begin
    set nocount on;
    if not exists (select 1 from facets_dict.questions where question_id = @question_id)
        throw 50001, N'No question has this id.', 1;

    update  facets_dict.questions
    set     answer = @answer, answered_at = sysdatetimeoffset(), answered_by = @author
    where   question_id = @question_id;
end;
go

-- ── Documentation ────────────────────────────────────────────────────────────────────────────────────
-- Replaced at every run, with the procedures.
declare @procedures table (name sysname, description nvarchar(400));
insert into @procedures (name, description) values
    (N'refresh_catalog',   N'Aligns the dictionary on the views of the schema facets and their columns: adds what is new, quarantines what is gone, deletes nothing.'),
    (N'annotate_view',     N'Sets the label, the questions answered and the exposure of a view. A null parameter changes nothing.'),
    (N'annotate_column',   N'Sets the label, description, nature, unit, divisor, exposure and sensitivity of a column. A null parameter changes nothing.'),
    (N'annotate_value',    N'Sets the meaning of one value of a code column; creates the row if needed.'),
    (N'write_domain_note', N'Replaces the one note of the database.'),
    (N'ask_question',      N'Records a question about the data, a view or a column, and returns its id.'),
    (N'answer_question',   N'Records the answer to a question, by its id.');

declare @name sysname, @description nvarchar(400);
declare procedures cursor local fast_forward for select name, description from @procedures;
open procedures;
fetch next from procedures into @name, @description;
while @@fetch_status = 0
begin
    if exists (select 1 from sys.extended_properties
               where major_id = object_id(N'facets_dict.' + @name) and minor_id = 0 and name = N'MS_Description')
        exec sys.sp_updateextendedproperty @name = N'MS_Description', @value = @description,
            @level0type = N'SCHEMA', @level0name = N'facets_dict', @level1type = N'PROCEDURE', @level1name = @name;
    else
        exec sys.sp_addextendedproperty @name = N'MS_Description', @value = @description,
            @level0type = N'SCHEMA', @level0name = N'facets_dict', @level1type = N'PROCEDURE', @level1name = @name;
    fetch next from procedures into @name, @description;
end;
close procedures;
deallocate procedures;
go

print N'facets_dict: 7 procedures in place. Next: Create-FacetsLogin.sql.';
go

set noexec off;
go
