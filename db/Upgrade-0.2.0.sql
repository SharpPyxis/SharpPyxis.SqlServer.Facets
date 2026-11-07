-- Upgrade-0.2.0.sql
--
-- Brings a dictionary created by version 0.1.0 to version 0.2.0. A column of a view no longer carries
-- is_exposed_to_model and is_sensitive, but one flag, is_export_only: the agent sees the name of an
-- export only column, never its values, which may only go to a file exported on the workstation.
--
-- The two old flags made three states, and a check to forbid a fourth. They map to the new one so:
--
--   exposed, not sensitive    open             is_export_only = 0
--   sensitive                 export only      is_export_only = 1
--   hidden, not sensitive     export only      is_export_only = 1
--
-- A hidden column becomes visible by its name, its values still closed. A column the agent must not
-- see at all has no place in the view: remove it from the view, and run refresh_catalog.
--
-- Run it once, as an administrator of the database, then Create-FacetsDictionaryProcedures.sql of
-- version 0.2.0: the procedures of 0.1.0 write the old flags and fail until they are replaced. It stops,
-- and changes nothing, on a dictionary that is not at version 0.1.0. Everything happens in one
-- transaction: an error leaves the dictionary as it was.

set nocount on;
go

if schema_id(N'facets_dict') is null
begin
    raiserror(N'The schema facets_dict does not exist: nothing was changed.', 16, 1);
    set noexec on;
end
else if col_length(N'facets_dict.columns', N'is_export_only') is not null
begin
    raiserror(N'facets_dict.columns has is_export_only already: the dictionary is at version 0.2.0, nothing was changed.', 16, 1);
    set noexec on;
end
else if col_length(N'facets_dict.columns', N'is_sensitive') is null
    or col_length(N'facets_dict.columns', N'is_exposed_to_model') is null
begin
    raiserror(N'facets_dict.columns lacks the flags of version 0.1.0: nothing was changed.', 16, 1);
    set noexec on;
end;
go

set xact_abort on;
begin transaction;

alter table facets_dict.columns add is_export_only bit not null
    constraint df_columns_is_export_only default 1;

-- The new column cannot be named in the batch that adds it but through a statement compiled apart.
exec sys.sp_executesql N'
update  facets_dict.columns
set     is_export_only = iif(is_exposed_to_model = 1 and is_sensitive = 0, 0, 1);';

alter table facets_dict.columns drop constraint ck_columns_sensitive_not_exposed;
alter table facets_dict.columns drop constraint df_columns_is_exposed_to_model, df_columns_is_sensitive;
alter table facets_dict.columns drop column is_exposed_to_model, is_sensitive;

exec sys.sp_addextendedproperty @name = N'MS_Description',
    @value = N'1, the default: the agent sees the name of the column, never its values, which may only go to a file exported on the workstation. 0 opens it to queries: the designer sets it, under their own login.',
    @level0type = N'SCHEMA', @level0name = N'facets_dict', @level1type = N'TABLE', @level1name = N'columns', @level2type = N'COLUMN', @level2name = N'is_export_only';
exec sys.sp_updateextendedproperty @name = N'MS_Description',
    @value = N'0 hides the view from the agent. The agent may hide a view; only the designer shows it again, under their own login.',
    @level0type = N'SCHEMA', @level0name = N'facets_dict', @level1type = N'TABLE', @level1name = N'views', @level2type = N'COLUMN', @level2name = N'is_exposed_to_model';

commit;
go

print N'Dictionary at version 0.2.0. Next: Create-FacetsDictionaryProcedures.sql of version 0.2.0.';
go

set noexec off;
go
