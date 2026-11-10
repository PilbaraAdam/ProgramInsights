-- Control plane 01: create the master database and SECURITY schema
-- Run once, as ACCOUNTADMIN (or a role with CREATE DATABASE).
--
-- PROGRAMINSIGHTS_APP is the control plane: it holds the app-level user gate,
-- the role -> client-database routing map, and global reference data. It holds
-- NO client data. Each client's own data lives in its own database, resolved at
-- login time from ROLE_DATABASE_MAP (see 03).

CREATE DATABASE IF NOT EXISTS PROGRAMINSIGHTS_APP
    COMMENT = 'ProgramInsights control plane — user gate, tenant routing, global reference data. No client data.';

CREATE SCHEMA IF NOT EXISTS PROGRAMINSIGHTS_APP.SECURITY
    COMMENT = 'App users, role->database routing, and the secure views the app reads at login.';

-- The app never reads base tables in this schema directly — only the secure
-- views created in 04. Grant USAGE so those views are reachable.
GRANT USAGE ON DATABASE PROGRAMINSIGHTS_APP TO ROLE PUBLIC;
GRANT USAGE ON SCHEMA   PROGRAMINSIGHTS_APP.SECURITY TO ROLE PUBLIC;
