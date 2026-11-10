-- Control plane 06: TEMPLATE — stand up the six state tables in a NEW client database
--
-- NOT NEEDED FOR PILBARA_PRI. That database already holds these tables, so tenant #1
-- is a no-op. This file is the template for onboarding client #2 onward.
--
-- Replace &CLIENT_DB with the new client's database name.
--
-- Why this works with no application code change: every app query names these
-- tables UNQUALIFIED (e.g. "FROM PROGRAM"), so they resolve against whatever
-- database the connection was opened with. The connection factory sets
-- db=<client database>, so the same SQL reads a different client's tables per user.

CREATE DATABASE IF NOT EXISTS &CLIENT_DB
    COMMENT = 'ProgramInsights client data';

-- PUBLIC schema is created automatically with the database; this is belt-and-braces.
CREATE SCHEMA IF NOT EXISTS &CLIENT_DB.PUBLIC;

-- ── Clone the table STRUCTURE from PILBARA_PRI, without the data ─────────────
-- LIKE copies columns, types, defaults, and constraints but no rows — which is
-- exactly what a new client needs. It also means the new client automatically
-- picks up every column added by the SQL/Upgrade_*.sql scripts, so those do not
-- need to be re-run here.
CREATE TABLE IF NOT EXISTS &CLIENT_DB.PUBLIC.MODEL            LIKE PILBARA_PRI.PUBLIC.MODEL;
CREATE TABLE IF NOT EXISTS &CLIENT_DB.PUBLIC.MODEL_CONFIG     LIKE PILBARA_PRI.PUBLIC.MODEL_CONFIG;
CREATE TABLE IF NOT EXISTS &CLIENT_DB.PUBLIC.PROGRAM          LIKE PILBARA_PRI.PUBLIC.PROGRAM;
CREATE TABLE IF NOT EXISTS &CLIENT_DB.PUBLIC.PROGRAM_SETTINGS LIKE PILBARA_PRI.PUBLIC.PROGRAM_SETTINGS;
CREATE TABLE IF NOT EXISTS &CLIENT_DB.PUBLIC.UNITS            LIKE PILBARA_PRI.PUBLIC.UNITS;
CREATE TABLE IF NOT EXISTS &CLIENT_DB.PUBLIC.PROFILES         LIKE PILBARA_PRI.PUBLIC.PROFILES;

-- CONSTANTS is intentionally NOT created here — it is global and lives in
-- PROGRAMINSIGHTS_APP.SECURITY.CONSTANTS (see 04), read fully qualified.

-- ── Confirm the structure matches ───────────────────────────────────────────
-- Column drift between clients is the failure mode this guards against. Expect
-- zero rows: anything returned is a column present in one database but not the other.
SELECT 'PILBARA_PRI' AS SIDE, TABLE_NAME, COLUMN_NAME
FROM PILBARA_PRI.INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = 'PUBLIC'
  AND TABLE_NAME IN ('MODEL','MODEL_CONFIG','PROGRAM','PROGRAM_SETTINGS','UNITS','PROFILES')
MINUS
SELECT 'PILBARA_PRI' AS SIDE, TABLE_NAME, COLUMN_NAME
FROM &CLIENT_DB.INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = 'PUBLIC'
  AND TABLE_NAME IN ('MODEL','MODEL_CONFIG','PROGRAM','PROGRAM_SETTINGS','UNITS','PROFILES');

-- Then run 05 with this &CLIENT_DB to create the role and grants, and add a
-- ROLE_DATABASE_MAP row (03) pointing that role at this database.
