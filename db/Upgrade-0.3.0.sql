-- Upgrade-0.3.0.sql
--
-- Brings a dictionary of version 0.2.0 to version 0.3.0. A view carries max_rows: the most rows a query
-- on it returns to the agent. Null, which every view gets here, keeps the default of the server,
-- FACETS_MAX_ROWS; a value replaces it, above or below. The designer sets it, under their own login, as
-- they open a column:
--
--   update facets_dict.views set max_rows = 500 where view_name = N'v_product_families';
--
-- Run it once, as an administrator of the database. The procedures of 0.2.0 stay as they are. It stops,
-- and changes nothing, on a dictionary that is not at version 0.2.0. Everything happens in one
-- transaction: an error leaves the dictionary as it was.

set nocount on;
go

if schema_id(N'facets_dict') is null
begin
    raiserror(N'The schema facets_dict does not exist: nothing was changed.', 16, 1);
    set noexec on;
end
else if col_length(N'facets_dict.views', N'max_rows') is not null
begin
    raiserror(N'facets_dict.views has max_rows already: the dictionary is at version 0.3.0, nothing was changed.', 16, 1);
    set noexec on;
end
else if col_length(N'facets_dict.columns', N'is_export_only') is null
begin
    raiserror(N'facets_dict.columns lacks is_export_only: the dictionary is older than 0.2.0, run Upgrade-0.2.0.sql first. Nothing was changed.', 16, 1);
    set noexec on;
end;
go

set xact_abort on;
begin transaction;

alter table facets_dict.views add max_rows int null
    constraint ck_views_max_rows_positive check (max_rows >= 1);

exec sys.sp_addextendedproperty @name = N'MS_Description',
    @value = N'The most rows a query on this view returns to the agent, above or below the default of the server (FACETS_MAX_ROWS). Null: that default. The designer sets it, under their own login. It bounds the volume of one call, not what the agent can read.',
    @level0type = N'SCHEMA', @level0name = N'facets_dict', @level1type = N'TABLE', @level1name = N'views', @level2type = N'COLUMN', @level2name = N'max_rows';

commit;
go

print N'Dictionary at version 0.3.0.';
go

set noexec off;
go
