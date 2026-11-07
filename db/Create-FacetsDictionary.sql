-- Create-FacetsDictionary.sql
--
-- Creates the two schemas SharpPyxis.SqlServer.Facets works with, in the database an agent will query:
--
--   facets       the views the agent may query. Created empty: the views are written by whoever sets
--                the database up, with or without SharpPyxis.SqlServer.SchemaMcp. A view in this schema
--                can be queried; a view anywhere else does not exist for the server.
--   facets_dict  the dictionary: what each view and each column means, the nature of a column, the
--                values a code takes, the note of domain, the questions the agent asked and their
--                answers. The server reads it to compose its SQL, and writes it through the procedures
--                of Create-FacetsDictionaryProcedures.sql, never directly.
--
-- Both schemas belong to dbo, on purpose. The login the server uses holds SELECT on the schema facets
-- and reads the tables behind its views through ownership chaining, without any right on those tables;
-- it holds EXECUTE on the procedures of facets_dict and writes its tables the same way. Create-FacetsLogin.sql
-- grants those rights, and nothing else.
--
-- Run it once, as an administrator of the database, in the database to expose. It stops if facets_dict
-- exists already, and creates nothing then. Every table and every column whose meaning is not in its
-- name carries an MS_Description, so that a tool reading the structure sees what it is for.

set nocount on;
go

if schema_id(N'facets_dict') is not null
begin
    raiserror(N'The schema facets_dict exists already: nothing was created.', 16, 1);
    set noexec on;
end;
go

-- The schema of the views may exist already, when the views were written first.
if schema_id(N'facets') is null
    exec (N'create schema facets authorization dbo;');
go

create schema facets_dict authorization dbo;
go

-- ── nature ───────────────────────────────────────────────────────────────────────────────────────────
-- What a column is, inside its type: a decimal is an amount, a quantity or a rate, and the three are
-- not aggregated the same way. The nature decides what the composer accepts on a column: which
-- aggregations, which filter operators, whether the column groups rows. That matrix lives in the
-- server; this table holds the closed list of codes, bounded by the foreign key of columns, and says
-- in words what each one is for.
create table facets_dict.nature
(
    nature_code        nvarchar(32)  not null,
    nature_label       nvarchar(100) not null,
    nature_description nvarchar(400) not null,
    sort_order         tinyint       not null,
    constraint pk_nature primary key (nature_code),
    -- Lowercase letters only. The collation of the database is usually case-insensitive, and a
    -- primary key alone would confuse Amount and amount.
    constraint ck_nature_code_format check (nature_code collate Latin1_General_BIN2 like '[a-z]%'
                                        and nature_code collate Latin1_General_BIN2 not like '%[^a-z]%'),
    constraint ck_nature_label_len check (len(nature_label) >= 1)
);
go

insert into facets_dict.nature (nature_code, nature_label, nature_description, sort_order)
values
    (N'amount',     N'Amount',     N'Money. Sums and averages make sense. Unit and divisor apply.', 1),
    (N'quantity',   N'Quantity',   N'A count, or a measured quantity. Sums and averages make sense. Unit and divisor apply.', 2),
    (N'rate',       N'Rate',       N'A ratio, a percentage, or a unit price, which keeps its precision where an amount is given to the cent. Averaged, never summed. Unit applies.', 3),
    (N'date',       N'Date',       N'A day or an instant. Groups rows by year, quarter, month, week or day.', 4),
    (N'code',       N'Code',       N'A value from a closed list, such as a status or a type. Groups and filters rows as it is; its values may be listed in column_values.', 5),
    (N'label',      N'Label',      N'A short name that identifies something, such as the name of a customer. Groups and filters rows as it is, and can be searched.', 6),
    (N'identifier', N'Identifier', N'The key of a row, technical or business. Counted and filtered by equality, never summed.', 7),
    (N'boolean',    N'Boolean',    N'Yes or no. Groups and filters rows by its two values.', 8),
    (N'text',       N'Text',       N'Free prose, such as a comment or a description. Searched, never grouped.', 9);
go

-- ── views ────────────────────────────────────────────────────────────────────────────────────────────
-- One row per view of the schema facets that the catalog refresh has seen. A view that disappears from
-- the schema is quarantined, never deleted: its annotations wait for it. Nothing in this schema is
-- deleted by the server.
create table facets_dict.views
(
    view_id             bigint            not null identity (1, 1),
    view_name           nvarchar(128)     not null,
    label               nvarchar(200)     not null constraint df_views_label default N'',
    answers             nvarchar(2000)    not null constraint df_views_answers default N'',
    is_exposed_to_model bit               not null constraint df_views_is_exposed_to_model default 1,
    is_quarantined      bit               not null constraint df_views_is_quarantined default 0,
    quarantined_at      datetimeoffset(3) null,
    last_seen_at        datetimeoffset(3) not null constraint df_views_last_seen_at default sysdatetimeoffset(),
    created_at          datetimeoffset(3) not null constraint df_views_created_at default sysdatetimeoffset(),
    created_by          nvarchar(128)     not null constraint df_views_created_by default N'',
    modified_at         datetimeoffset(3) null,
    modified_by         nvarchar(128)     null,
    constraint pk_views primary key (view_id),
    constraint uq_views_view_name unique (view_name),
    constraint ck_views_view_name_len check (len(view_name) >= 1),
    constraint ck_views_quarantine check ((is_quarantined = 0 and quarantined_at is null)
                                       or (is_quarantined = 1 and quarantined_at is not null))
);
go

-- ── columns ──────────────────────────────────────────────────────────────────────────────────────────
-- One row per column of each view. The nature is null until someone sets it: the composer refuses a
-- column without one, since it cannot tell what the column allows. Unit and divisor only make sense on
-- a measure, and the checks say so; a sensitive column may go to an exported file, never to the model,
-- and the two flags cannot both be set.
create table facets_dict.columns
(
    column_id           bigint            not null identity (1, 1),
    view_id             bigint            not null,
    column_name         nvarchar(128)     not null,
    ordinal_position    int               not null,
    sql_type            nvarchar(128)     not null,
    label               nvarchar(200)     not null constraint df_columns_label default N'',
    description         nvarchar(2000)    not null constraint df_columns_description default N'',
    nature_code         nvarchar(32)      null,
    unit                nvarchar(20)      not null constraint df_columns_unit default N'',
    divisor             decimal(18, 6)    not null constraint df_columns_divisor default 1,
    is_exposed_to_model bit               not null constraint df_columns_is_exposed_to_model default 1,
    is_sensitive        bit               not null constraint df_columns_is_sensitive default 0,
    is_quarantined      bit               not null constraint df_columns_is_quarantined default 0,
    quarantined_at      datetimeoffset(3) null,
    last_seen_at        datetimeoffset(3) not null constraint df_columns_last_seen_at default sysdatetimeoffset(),
    created_at          datetimeoffset(3) not null constraint df_columns_created_at default sysdatetimeoffset(),
    created_by          nvarchar(128)     not null constraint df_columns_created_by default N'',
    modified_at         datetimeoffset(3) null,
    modified_by         nvarchar(128)     null,
    constraint pk_columns primary key (column_id),
    constraint uq_columns_view_id_column_name unique (view_id, column_name),
    constraint fk_columns_views foreign key (view_id) references facets_dict.views (view_id),
    constraint fk_columns_nature foreign key (nature_code) references facets_dict.nature (nature_code),
    constraint ck_columns_column_name_len check (len(column_name) >= 1),
    constraint ck_columns_ordinal_position check (ordinal_position >= 1),
    -- isnull: a check that evaluates to null is not a violation, so a null nature would let a unit through.
    constraint ck_columns_unit_on_measure check (unit = N'' or isnull(nature_code, N'') in (N'amount', N'quantity', N'rate')),
    constraint ck_columns_divisor_on_measure check (divisor = 1 or isnull(nature_code, N'') in (N'amount', N'quantity')),
    constraint ck_columns_divisor_positive check (divisor > 0),
    constraint ck_columns_sensitive_not_exposed check (is_sensitive = 0 or is_exposed_to_model = 0),
    constraint ck_columns_quarantine check ((is_quarantined = 0 and quarantined_at is null)
                                         or (is_quarantined = 1 and quarantined_at is not null))
);
go

create index ix_columns_view_id on facets_dict.columns (view_id);
go

-- ── column_values ────────────────────────────────────────────────────────────────────────────────────
-- The values a code column takes, with their meaning: what lets the agent translate a status asked in
-- words into the value stored. Written by hand, or from an enumeration table of the database.
create table facets_dict.column_values
(
    column_id   bigint            not null,
    code_value  nvarchar(100)     not null,
    label       nvarchar(200)     not null constraint df_column_values_label default N'',
    description nvarchar(400)     not null constraint df_column_values_description default N'',
    created_at  datetimeoffset(3) not null constraint df_column_values_created_at default sysdatetimeoffset(),
    created_by  nvarchar(128)     not null constraint df_column_values_created_by default N'',
    modified_at datetimeoffset(3) null,
    modified_by nvarchar(128)     null,
    constraint pk_column_values primary key (column_id, code_value),
    constraint fk_column_values_columns foreign key (column_id) references facets_dict.columns (column_id)
);
go

-- ── domain_notes ─────────────────────────────────────────────────────────────────────────────────────
-- One note for the whole database: what the agent must know before it reads any view, such as the
-- business, the calendar, the conventions of the data. One row, read at the start of every session,
-- which is why it is bounded: a long note is paid in tokens at every start.
create table facets_dict.domain_notes
(
    domain_note_id tinyint           not null,
    note           nvarchar(4000)    not null constraint df_domain_notes_note default N'',
    created_at     datetimeoffset(3) not null constraint df_domain_notes_created_at default sysdatetimeoffset(),
    created_by     nvarchar(128)     not null constraint df_domain_notes_created_by default N'',
    modified_at    datetimeoffset(3) null,
    modified_by    nvarchar(128)     null,
    constraint pk_domain_notes primary key (domain_note_id),
    constraint ck_domain_notes_single_row check (domain_note_id = 1)
);
go

-- ── questions ────────────────────────────────────────────────────────────────────────────────────────
-- What the agent could not tell from the dictionary and asked, about a view, a column, or the data in
-- general; and the answer, when someone gave one. An answer is a fact about the data, kept here so the
-- same question is not asked twice. A question about a column names its view too.
create table facets_dict.questions
(
    question_id bigint            not null identity (1, 1),
    view_id     bigint            null,
    column_id   bigint            null,
    question    nvarchar(2000)    not null,
    answer      nvarchar(2000)    not null constraint df_questions_answer default N'',
    asked_at    datetimeoffset(3) not null constraint df_questions_asked_at default sysdatetimeoffset(),
    asked_by    nvarchar(128)     not null constraint df_questions_asked_by default N'',
    answered_at datetimeoffset(3) null,
    answered_by nvarchar(128)     null,
    constraint pk_questions primary key (question_id),
    constraint fk_questions_views foreign key (view_id) references facets_dict.views (view_id),
    constraint fk_questions_columns foreign key (column_id) references facets_dict.columns (column_id),
    constraint ck_questions_question_len check (len(question) >= 1),
    constraint ck_questions_column_in_view check (column_id is null or view_id is not null),
    constraint ck_questions_answered check ((answered_at is null and answer = N'' and answered_by is null)
                                         or (answered_at is not null and len(answer) >= 1 and answered_by is not null))
);
go

create index ix_questions_view_id on facets_dict.questions (view_id);
go

-- ── Documentation ────────────────────────────────────────────────────────────────────────────────────
-- Each table, and each column whose name does not say what it holds.

exec sys.sp_addextendedproperty @name = N'MS_Description',
    @value = N'The views an agent may query through SharpPyxis.SqlServer.Facets. A view elsewhere does not exist for the server.',
    @level0type = N'SCHEMA', @level0name = N'facets';
exec sys.sp_addextendedproperty @name = N'MS_Description',
    @value = N'The dictionary of SharpPyxis.SqlServer.Facets: what the views and their columns mean. Written through its procedures only.',
    @level0type = N'SCHEMA', @level0name = N'facets_dict';

exec sys.sp_addextendedproperty @name = N'MS_Description',
    @value = N'The closed list of natures a column can have. A nature decides which aggregations and filters the server accepts on a column.',
    @level0type = N'SCHEMA', @level0name = N'facets_dict', @level1type = N'TABLE', @level1name = N'nature';

exec sys.sp_addextendedproperty @name = N'MS_Description',
    @value = N'One row per view of the schema facets seen by the catalog refresh. A view gone from the schema is quarantined, never deleted.',
    @level0type = N'SCHEMA', @level0name = N'facets_dict', @level1type = N'TABLE', @level1name = N'views';
exec sys.sp_addextendedproperty @name = N'MS_Description',
    @value = N'The questions this view answers, in the words of a request: what the agent reads to choose a view.',
    @level0type = N'SCHEMA', @level0name = N'facets_dict', @level1type = N'TABLE', @level1name = N'views', @level2type = N'COLUMN', @level2name = N'answers';
exec sys.sp_addextendedproperty @name = N'MS_Description',
    @value = N'0 hides the view from the agent while it is being annotated.',
    @level0type = N'SCHEMA', @level0name = N'facets_dict', @level1type = N'TABLE', @level1name = N'views', @level2type = N'COLUMN', @level2name = N'is_exposed_to_model';
exec sys.sp_addextendedproperty @name = N'MS_Description',
    @value = N'1 when the last catalog refresh did not find the view in the schema. Its annotations wait for it.',
    @level0type = N'SCHEMA', @level0name = N'facets_dict', @level1type = N'TABLE', @level1name = N'views', @level2type = N'COLUMN', @level2name = N'is_quarantined';
exec sys.sp_addextendedproperty @name = N'MS_Description',
    @value = N'When the catalog refresh last found the view in the schema.',
    @level0type = N'SCHEMA', @level0name = N'facets_dict', @level1type = N'TABLE', @level1name = N'views', @level2type = N'COLUMN', @level2name = N'last_seen_at';

exec sys.sp_addextendedproperty @name = N'MS_Description',
    @value = N'One row per column of each view. The server refuses a column whose nature is not set.',
    @level0type = N'SCHEMA', @level0name = N'facets_dict', @level1type = N'TABLE', @level1name = N'columns';
exec sys.sp_addextendedproperty @name = N'MS_Description',
    @value = N'The type of the column as the catalog gave it at the last refresh, such as decimal(18, 2). Informative: the nature is what the server reads.',
    @level0type = N'SCHEMA', @level0name = N'facets_dict', @level1type = N'TABLE', @level1name = N'columns', @level2type = N'COLUMN', @level2name = N'sql_type';
exec sys.sp_addextendedproperty @name = N'MS_Description',
    @value = N'What the column is, inside its type. Null until someone decides it; the server then refuses the column.',
    @level0type = N'SCHEMA', @level0name = N'facets_dict', @level1type = N'TABLE', @level1name = N'columns', @level2type = N'COLUMN', @level2name = N'nature_code';
exec sys.sp_addextendedproperty @name = N'MS_Description',
    @value = N'The unit of a measure, as shown to the agent, such as EUR, kg or %. Empty on anything that is not a measure.',
    @level0type = N'SCHEMA', @level0name = N'facets_dict', @level1type = N'TABLE', @level1name = N'columns', @level2type = N'COLUMN', @level2name = N'unit';
exec sys.sp_addextendedproperty @name = N'MS_Description',
    @value = N'What the stored value is divided by before it is shown: 100 for cents stored as integers. 1 on anything that is not an amount or a quantity.',
    @level0type = N'SCHEMA', @level0name = N'facets_dict', @level1type = N'TABLE', @level1name = N'columns', @level2type = N'COLUMN', @level2name = N'divisor';
exec sys.sp_addextendedproperty @name = N'MS_Description',
    @value = N'0 keeps the column out of what the agent sees and queries.',
    @level0type = N'SCHEMA', @level0name = N'facets_dict', @level1type = N'TABLE', @level1name = N'columns', @level2type = N'COLUMN', @level2name = N'is_exposed_to_model';
exec sys.sp_addextendedproperty @name = N'MS_Description',
    @value = N'1 for a value that may go to a file exported on the workstation, never to the model: a name, an address, an e-mail. Never exposed to the model.',
    @level0type = N'SCHEMA', @level0name = N'facets_dict', @level1type = N'TABLE', @level1name = N'columns', @level2type = N'COLUMN', @level2name = N'is_sensitive';
exec sys.sp_addextendedproperty @name = N'MS_Description',
    @value = N'1 when the last catalog refresh did not find the column in its view. Its annotations wait for it.',
    @level0type = N'SCHEMA', @level0name = N'facets_dict', @level1type = N'TABLE', @level1name = N'columns', @level2type = N'COLUMN', @level2name = N'is_quarantined';

exec sys.sp_addextendedproperty @name = N'MS_Description',
    @value = N'The values a code column takes, with their meaning, so that a status asked in words becomes the value stored.',
    @level0type = N'SCHEMA', @level0name = N'facets_dict', @level1type = N'TABLE', @level1name = N'column_values';
exec sys.sp_addextendedproperty @name = N'MS_Description',
    @value = N'The value as stored in the column, as text.',
    @level0type = N'SCHEMA', @level0name = N'facets_dict', @level1type = N'TABLE', @level1name = N'column_values', @level2type = N'COLUMN', @level2name = N'code_value';

exec sys.sp_addextendedproperty @name = N'MS_Description',
    @value = N'One row: what the agent must know about the data before it reads any view. Bounded, since it is read at every start.',
    @level0type = N'SCHEMA', @level0name = N'facets_dict', @level1type = N'TABLE', @level1name = N'domain_notes';

exec sys.sp_addextendedproperty @name = N'MS_Description',
    @value = N'What the agent asked about the data, and the answer when someone gave one, so that the same question is not asked twice.',
    @level0type = N'SCHEMA', @level0name = N'facets_dict', @level1type = N'TABLE', @level1name = N'questions';
exec sys.sp_addextendedproperty @name = N'MS_Description',
    @value = N'Empty until answered. The answer is a fact about the data, in the words of whoever gave it.',
    @level0type = N'SCHEMA', @level0name = N'facets_dict', @level1type = N'TABLE', @level1name = N'questions', @level2type = N'COLUMN', @level2name = N'answer';
go

print N'facets_dict created: 6 tables, 9 natures. Next: Create-FacetsDictionaryProcedures.sql, then Create-FacetsLogin.sql.';
go

set noexec off;
go
