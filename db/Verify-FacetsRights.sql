-- Verify-FacetsRights.sql
--
-- Checks what a login can and cannot do in a database, and gives a verdict, PASS or FAIL, for each
-- check. It is the proof that goes with Create-FacetsLogin.sql: that script shows what is granted, this
-- one shows that nothing else is.
--
-- Run it connected AS THE LOGIN the MCP server uses, in the database the MCP server queries. Every
-- check reads the rights of the connection running it: run by an administrator, it would answer for the
-- administrator. In SSMS, open a new connection, choose "SQL Server Authentication", type the name and
-- the password of the login, then select the database.
--
-- The script leaves the database as it found it. Its writes, two through the procedures of the
-- dictionary and one attempt to create a table, run inside transactions that are rolled back. It reads
-- one row of one view of facets into a variable, and never displays it.
--
-- Run it after a first view exists in the schema facets: without one, checks 1 and 2 cannot tell
-- whether the login sees the schema or whether there is nothing to see, and say so.
--
-- The six checks:
--
--   1. The login sees the views of facets in the catalog, with their columns: what refresh_catalog
--      reads. SQL Server shows a login the objects it holds a right on, and nothing else.
--   2. The login reads a view of facets, and no table: an actual read of a view succeeds, no table
--      outside facets_dict is visible or readable.
--   3. The login reads the dictionary: the nine natures are there.
--   4. The login writes the dictionary through its procedures only: no right to insert, update or
--      delete on its tables, a direct insert refused with error 229, and refresh_catalog, run by the
--      login, finds the views of facets.
--   5. The login writes nothing else: no right to create, alter or control anything in the database,
--      no right to execute a procedure outside facets_dict, and an actual attempt to create a table
--      refused with error 262. Two procedures of SSMS diagrams are admitted by name, both of which
--      only read.
--   6. The login is not an administrator: neither sysadmin on the instance nor db_owner in the
--      database. Either would make every other check meaningless.
--
-- A FAIL on check 2, 4, 5 or 6 means the login holds more rights than it should: through another role,
-- through a right granted to public, or because it is an administrator. The detail column names what
-- was found, the first five at most. Run as a sysadmin, this script fails, as it should.

set nocount on;
-- The list of names in the detail goes through an XML method, which SQL Server refuses without it.
-- SSMS sets it by default, sqlcmd does not.
set quoted_identifier on;

declare @verdicts table (step int, check_name nvarchar(60), verdict char(4), detail nvarchar(max));
declare @found table (step int, item nvarchar(400));
declare @counts table (views_added int, views_present int, views_quarantined int,
                       columns_added int, columns_present int, columns_quarantined int);
declare @views int, @columns int, @count int, @admitted int, @present int, @refused bit,
        @view nvarchar(300), @outcome nvarchar(400), @list nvarchar(max);

-- 1. It sees the views of facets, with their columns.
select @views = count(*) from sys.views where schema_id = schema_id(N'facets');
select @columns = count(*) from sys.columns as c
join sys.views as v on v.object_id = c.object_id
where v.schema_id = schema_id(N'facets');

insert into @verdicts
values (1, N'sees the views of facets', iif(@views > 0, 'PASS', 'FAIL'),
        iif(@views > 0,
            concat(@views, N' views visible, ', @columns, N' columns'),
            N'no view of facets visible: either none exists yet, or the login holds no SELECT on the schema'));

-- 2. It reads a view of facets, and no table.
select top (1) @view = N'[facets].' + quotename(name) from sys.views where schema_id = schema_id(N'facets') order by name;

if @view is null
    set @outcome = N'no view to try a read on';
else
begin
    begin try
        -- The one dynamic statement of this script: the name comes from the catalog, quoted. The row
        -- read, if any, goes into a variable and is never displayed.
        exec (N'declare @x int; select top (1) @x = 1 from ' + @view + N';');
        set @outcome = concat(N'read ', @view);
    end try
    begin catch
        set @outcome = concat(N'reading ', @view, N' failed: error ', error_number(), N': ', error_message());
        set @view = null;
    end catch;
end;

insert into @found (step, item)
select 2, quotename(schema_name(schema_id)) + N'.' + quotename(name)
from sys.tables
where schema_id <> schema_id(N'facets_dict');

select @count = count(*) from @found where step = 2;
set @list = stuff((select top (5) N', ' + item from @found where step = 2 order by item
                   for xml path(N''), type).value(N'.', N'nvarchar(max)'), 1, 2, N'')
            + iif(@count > 5, concat(N' and ', @count - 5, N' more'), N'');

insert into @verdicts
values (2, N'reads views, not tables', iif(@view is not null and @count = 0, 'PASS', 'FAIL'),
        concat(@outcome, N'; ', @count, N' tables visible outside facets_dict', iif(@count > 0, N': ' + @list, N'')));

-- 3. It reads the dictionary.
begin try
    select @count = count(*) from facets_dict.nature;
    insert into @verdicts
    values (3, N'reads the dictionary', iif(@count = 9, 'PASS', 'FAIL'),
            concat(@count, N' natures in facets_dict.nature, 9 expected'));
end try
begin catch
    insert into @verdicts
    values (3, N'reads the dictionary', 'FAIL', concat(N'error ', error_number(), N': ', error_message()));
end catch;

-- 4. It writes the dictionary through its procedures only.
insert into @found (step, item)
select 4, stuff(iif(has_perms_by_name(q.name, N'OBJECT', N'INSERT') = 1, N', INSERT', N'')
              + iif(has_perms_by_name(q.name, N'OBJECT', N'UPDATE') = 1, N', UPDATE', N'')
              + iif(has_perms_by_name(q.name, N'OBJECT', N'DELETE') = 1, N', DELETE', N''), 1, 2, N'')
          + N' on ' + q.name
from sys.tables as t
cross apply (select N'[facets_dict].' + quotename(t.name) as name) as q
where t.schema_id = schema_id(N'facets_dict')
  and (has_perms_by_name(q.name, N'OBJECT', N'INSERT') = 1
    or has_perms_by_name(q.name, N'OBJECT', N'UPDATE') = 1
    or has_perms_by_name(q.name, N'OBJECT', N'DELETE') = 1);

select @count = count(*) from @found where step = 4;
set @list = stuff((select top (5) N', ' + item from @found where step = 4 order by item
                   for xml path(N''), type).value(N'.', N'nvarchar(max)'), 1, 2, N'')
            + iif(@count > 5, concat(N' and ', @count - 5, N' more'), N'');

begin try
    begin transaction;
    insert into facets_dict.questions (question, asked_by) values (N'probe', N'probe');
    rollback transaction;
    select @refused = 0, @outcome = N'inserted a row directly';
end try
begin catch
    if @@trancount > 0 rollback transaction;
    select @refused = iif(error_number() = 229, 1, 0),
           @outcome = iif(error_number() = 229, N'direct insert refused (229)',
                          concat(N'error ', error_number(), N': ', error_message()));
end catch;

begin try
    begin transaction;
    insert into @counts exec facets_dict.refresh_catalog @author = N'Verify-FacetsRights.sql';
    rollback transaction;
    select @present = views_present from @counts;
    set @outcome = concat(@outcome, N'; refresh_catalog ran and found ', @present, N' views');
end try
begin catch
    if @@trancount > 0 rollback transaction;
    set @present = -1;
    set @outcome = concat(@outcome, N'; refresh_catalog failed: error ', error_number(), N': ', error_message());
end catch;

insert into @verdicts
values (4, N'writes through the procedures only',
        iif(@count = 0 and @refused = 1 and @present = @views, 'PASS', 'FAIL'),
        concat(@count, N' rights to write the dictionary directly', iif(@count > 0, N': ' + @list, N''), N'; ', @outcome));

-- 5. It writes nothing else.
insert into @found (step, item)
select 5, N'EXECUTE on ' + q.name
from sys.objects as o
cross apply (select quotename(schema_name(o.schema_id)) + N'.' + quotename(o.name) as name) as q
where o.type in ('P', 'PC', 'X')
  and o.schema_id <> schema_id(N'facets_dict')
  and not (o.schema_id = schema_id(N'dbo') and o.name in (N'sp_helpdiagrams', N'sp_helpdiagramdefinition'))
  and has_perms_by_name(q.name, N'OBJECT', N'EXECUTE') = 1;

select @admitted = count(*)
from sys.objects as o
where o.schema_id = schema_id(N'dbo')
  and o.name in (N'sp_helpdiagrams', N'sp_helpdiagramdefinition')
  and has_perms_by_name(N'[dbo].' + quotename(o.name), N'OBJECT', N'EXECUTE') = 1;

insert into @found (step, item)
select 5, permission_name + N' on the database'
from fn_my_permissions(null, N'DATABASE')
where permission_name like N'CREATE%'
   or permission_name like N'ALTER%'
   or permission_name like N'IMPERSONATE%'
   or permission_name like N'BACKUP%'
   or permission_name in (N'CONTROL', N'INSERT', N'UPDATE', N'DELETE', N'EXECUTE', N'TAKE OWNERSHIP');

select @count = count(*) from @found where step = 5;
set @list = stuff((select top (5) N', ' + item from @found where step = 5 order by item
                   for xml path(N''), type).value(N'.', N'nvarchar(max)'), 1, 2, N'')
            + iif(@count > 5, concat(N' and ', @count - 5, N' more'), N'');

begin try
    begin transaction;
    create table dbo.facets_write_probe (id int);
    rollback transaction;
    select @refused = 0, @outcome = N'created a table';
end try
begin catch
    if @@trancount > 0 rollback transaction;
    select @refused = iif(error_number() = 262, 1, 0),
           @outcome = iif(error_number() = 262, N'creating a table refused (262)',
                          concat(N'error ', error_number(), N': ', error_message()));
end catch;

insert into @verdicts
values (5, N'writes nothing else', iif(@count = 0 and @refused = 1, 'PASS', 'FAIL'),
        concat(@count, N' rights held beyond the three granted', iif(@count > 0, N': ' + @list, N''), N'; ', @outcome,
               iif(@admitted > 0, concat(N'; ', @admitted, N' diagram procedures that only read admitted'), N'')));

-- 6. It is not an administrator.
insert into @verdicts
values (6, N'is not an administrator',
        iif(is_srvrolemember(N'sysadmin') = 0 and is_member(N'db_owner') = 0, 'PASS', 'FAIL'),
        concat(N'sysadmin: ', iif(is_srvrolemember(N'sysadmin') = 1, N'yes', N'no'),
               N'; db_owner: ', iif(is_member(N'db_owner') = 1, N'yes', N'no')));

select step, check_name, verdict, detail
from @verdicts
order by step;

select iif(exists (select 1 from @verdicts where verdict = 'FAIL'),
           N'FAIL: this login does not hold exactly what the server needs. The detail of each failed check names what differs.',
           N'PASS: this login reads the views of facets and the dictionary, writes the dictionary through its procedures, and nothing else.') as conclusion;
