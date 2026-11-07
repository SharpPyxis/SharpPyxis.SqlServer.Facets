-- Create-FacetsLogin.sql
--
-- Creates the login that SharpPyxis.SqlServer.Facets, an MCP server, uses to connect to a SQL Server
-- database, and grants it three rights: SELECT on the schema facets, SELECT and EXECUTE on the schema
-- facets_dict. With them, the login reads the views written for the agent, reads the dictionary that
-- says what they mean, and writes that dictionary through its procedures. It reads no table behind
-- the views, writes no table of the dictionary directly, and can change nothing else.
--
-- This is the principle of least privilege: a login receives what its task needs, and nothing more.
-- The rights are what makes the guarantee; the code of the server is the second line.
--
-- How the login reads the tables behind the views without any right on them: the views of facets and
-- the tables they read belong to the same owner, dbo, and SQL Server then checks the right on the view
-- alone. The same chain lets the procedures of facets_dict write its tables. It holds as long as
-- everything belongs to dbo and no procedure uses dynamic SQL, which the product's scripts respect.
--
-- Before running this script:
--
--   1. The instance must accept SQL Server authentication, also called mixed mode. To check it:
--
--          select serverproperty('IsIntegratedSecurityOnly');
--
--      A result of 1 means Windows authentication only. On such an instance, this script still creates
--      the login, but every connection with it is refused (error 18456). Switching to mixed mode is done
--      in the properties of the server, on the Security page, and needs a restart of the service.
--
--   2. Run Create-FacetsDictionary.sql and Create-FacetsDictionaryProcedures.sql first: the schemas
--      must exist for the rights to be granted on them.
--
--   3. Run it as a login allowed to create logins and users: in practice, an administrator of the
--      instance.
--
--   4. Run it in SQLCMD mode. The four :setvar lines below are commands of sqlcmd, not T-SQL. SSMS
--      understands them only in SQLCMD mode (menu Query > SQLCMD Mode). Without it, the script stops on
--      its first line, and nothing is created.
--
--   5. Change the four values below. The names are examples. The password must be yours: it is checked
--      against the password policy of Windows.
--
-- After running it, connect to the database as the new login, and run Verify-FacetsRights.sql. That
-- script checks what the login can and cannot do. This one, run by an administrator, cannot tell.

:setvar Login    facets_mcp
:setvar Password "Change-this-password-1"
:setvar Role     facets_agent
:setvar Database demo

-- 1. The login. It is created at the level of the instance, in master, and it is what opens a
--    connection. check_policy applies the password rules of Windows, such as length and complexity.
--    default_database is the database a connection lands in when it names none.
use master;
go

create login [$(Login)]
    with password = N'$(Password)', check_policy = on, default_database = [$(Database)];
go

-- 2. The user. A login opens a connection to the instance; to enter a database, it needs a user in that
--    database, mapped to it.
use [$(Database)];
go

create user [$(Login)] for login [$(Login)];
go

-- 3. A role holds the rights, and the user becomes a member of it. When another account needs the same
--    access, such as a second login or a Windows account, it joins the role, and no new right has to be
--    granted or reviewed.
create role [$(Role)];
alter role [$(Role)] add member [$(Login)];
go

-- 4. The three rights granted, each on a whole schema, so that a view or a procedure added later is
--    covered without a new grant.
--
--    SELECT on facets: the views the agent may query, and only them. A view in another schema does not
--    exist for the server, and a table of the database is never readable directly: the login holds no
--    right on it, and reads it only through a view of facets.
--
--    SELECT on facets_dict: the dictionary, read to compose the SQL and to tell the agent what a view
--    and a column mean. EXECUTE on facets_dict: its procedures, the only way the dictionary is written.
grant select on schema::[facets] to [$(Role)];
grant select on schema::[facets_dict] to [$(Role)];
grant execute on schema::[facets_dict] to [$(Role)];
go

-- 5. The only writes left open by the default rights of a database. When database diagrams are used in
--    SSMS, four procedures are installed in the dbo schema. They write into the table dbo.sysdiagrams,
--    and the role public, which every user of the database belongs to, may execute them. Each one gets
--    a deny, when it exists. Diagrams installed after this script ran bring the four procedures back,
--    open to public: run these four lines again then. Verify-FacetsRights.sql names them when it finds
--    them.
--
--    No other deny, on purpose. A deny on the whole database would also refuse the catalog views of the
--    sys schema, which the procedure refresh_catalog reads. Rights that your database grants to public
--    beyond the defaults stay granted, and the verification script lists what it finds.
if object_id(N'dbo.sp_creatediagram') is not null deny execute on dbo.sp_creatediagram to [$(Role)];
if object_id(N'dbo.sp_alterdiagram') is not null deny execute on dbo.sp_alterdiagram to [$(Role)];
if object_id(N'dbo.sp_renamediagram') is not null deny execute on dbo.sp_renamediagram to [$(Role)];
if object_id(N'dbo.sp_dropdiagram') is not null deny execute on dbo.sp_dropdiagram to [$(Role)];
go

-- To undo what this script created, as an administrator, with your own names:
--
--     use [your_database];
--     drop user [facets_mcp];
--     drop role [facets_agent];
--     use master;
--     drop login [facets_mcp];
