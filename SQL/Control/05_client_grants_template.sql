-- Control plane 05: TEMPLATE — provision one client (role + grants)
-- Copy this file per client, replace the two placeholders, and run as SECURITYADMIN
-- (the Cortex grant in step 6 needs ACCOUNTADMIN).
--
--   &CLIENT_DB    the client's database, e.g. PILBARA_PRI
--   &CLIENT_ROLE  the role for that client
--
-- Snowflake's SQL variables work with SET/IDENTIFIER for some statements but not
-- all GRANT forms, so this is written as find-and-replace rather than SET. Do the
-- replacement in your editor before running.
--
-- ⚠️ QUOTING: if the role name is not a plain uppercase identifier — anything with a
-- hyphen or lowercase letters, e.g. pilbara-consultants — the replacement value must
-- INCLUDE double quotes, or every GRANT below is a syntax error:
--
--     &CLIENT_ROLE  ->  "pilbara-consultants"        (correct)
--     &CLIENT_ROLE  ->  pilbara-consultants          (syntax error on the hyphen)

-- ── 1. The client role ──────────────────────────────────────────────────────
-- Skip this if you are granting to an existing role (e.g. a shared consultants role);
-- only run it when creating a brand-new per-client role.
-- CREATE ROLE IF NOT EXISTS &CLIENT_ROLE
--     COMMENT = 'ProgramInsights access for &CLIENT_DB';

-- ── 2. Warehouse ────────────────────────────────────────────────────────────
-- REQUIRED. The app's connection string sets no warehouse today and works only
-- because the old service account had a DEFAULT_WAREHOUSE. Without USAGE here
-- every query fails with "No active warehouse selected in the current session."
GRANT USAGE ON WAREHOUSE COMPUTE_WH TO ROLE &CLIENT_ROLE;

-- ── 3. The client's own database ────────────────────────────────────────────
GRANT USAGE ON DATABASE &CLIENT_DB              TO ROLE &CLIENT_ROLE;
GRANT USAGE ON SCHEMA   &CLIENT_DB.PUBLIC       TO ROLE &CLIENT_ROLE;

-- The six app state tables. Read/write: the app edits all of these.
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE &CLIENT_DB.PUBLIC.MODEL            TO ROLE &CLIENT_ROLE;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE &CLIENT_DB.PUBLIC.MODEL_CONFIG     TO ROLE &CLIENT_ROLE;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE &CLIENT_DB.PUBLIC.PROGRAM          TO ROLE &CLIENT_ROLE;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE &CLIENT_DB.PUBLIC.PROGRAM_SETTINGS TO ROLE &CLIENT_ROLE;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE &CLIENT_DB.PUBLIC.UNITS            TO ROLE &CLIENT_ROLE;
GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE &CLIENT_DB.PUBLIC.PROFILES         TO ROLE &CLIENT_ROLE;

-- ── 4. View create/drop ─────────────────────────────────────────────────────
-- DataMappingService runs CREATE OR REPLACE VIEW V_COURSE_VALUES_<id> /
-- V_UNIT_VALUES_<id>, and ScenarioService.DeleteAsync runs DROP VIEW IF EXISTS on
-- them. DROP requires OWNERSHIP, which the creating role gets automatically — but
-- only if it also has CREATE VIEW. Without this, scenario delete fails.
GRANT CREATE VIEW ON SCHEMA &CLIENT_DB.PUBLIC TO ROLE &CLIENT_ROLE;

-- ── 5. Shared reference datasets (read-only, cross-database) ─────────────────
-- Public sector reference data — safe to grant broadly.
GRANT USAGE  ON DATABASE AU_HIGHER_ED_FUNDING                            TO ROLE &CLIENT_ROLE;
GRANT USAGE  ON SCHEMA   AU_HIGHER_ED_FUNDING.CURATED                    TO ROLE &CLIENT_ROLE;
GRANT SELECT ON TABLE    AU_HIGHER_ED_FUNDING.CURATED.HELP_ALLOCATIONS   TO ROLE &CLIENT_ROLE;

-- ⚠️ BEFORE RUNNING THE NEXT THREE LINES: confirm BM_FOE4_SECTOR_AGG_BASE is
-- genuinely sector-AGGREGATED and contains no client-identifiable rows. Granting
-- every client role SELECT on it is a cross-tenant disclosure if it is not.
-- Verify, do not assume.
GRANT USAGE  ON DATABASE BENCHMARK_APP                                     TO ROLE &CLIENT_ROLE;
GRANT USAGE  ON SCHEMA   BENCHMARK_APP.CLIENT                              TO ROLE &CLIENT_ROLE;
GRANT SELECT ON TABLE    BENCHMARK_APP.CLIENT.BM_FOE4_SECTOR_AGG_BASE      TO ROLE &CLIENT_ROLE;

-- ── 6. Cortex (Intelligence tab) ────────────────────────────────────────────
-- Needs ACCOUNTADMIN.
GRANT DATABASE ROLE SNOWFLAKE.CORTEX_USER TO ROLE &CLIENT_ROLE;
--
-- ⚠️ Also likely required, but it is an ACCOUNT-LEVEL change with data-residency
-- implications — decide deliberately, do not just run it. On an AU account the
-- models offered in the app's dropdown (mistral-large2, llama3.1-405b) generally
-- need cross-region inference:
--
--   ALTER ACCOUNT SET CORTEX_ENABLED_CROSS_REGION = 'ANY_REGION';

-- ── 7. Assign the role to users ─────────────────────────────────────────────
-- Every Entra user needs a matching Snowflake user whose LOGIN_NAME is their UPN
-- and whose DEFAULT_ROLE is this client role — CURRENT_ROLE() is what the app
-- reads to route to the client database. A user with no DEFAULT_ROLE gets
-- "No active Snowflake role."
--
-- CREATE USER IF NOT EXISTS "jane.doe@client.edu.au"
--     LOGIN_NAME        = 'jane.doe@client.edu.au'
--     DISPLAY_NAME      = 'Jane Doe'
--     DEFAULT_ROLE      = &CLIENT_ROLE
--     DEFAULT_WAREHOUSE = COMPUTE_WH
--     TYPE              = SERVICE;   -- omit TYPE for humans using SSO
--
-- GRANT ROLE &CLIENT_ROLE TO USER "jane.doe@client.edu.au";
--
-- Then add them to APP_USERS (02) or the app will refuse the login.

-- ── 8. Verify as the client role ────────────────────────────────────────────
-- USE ROLE &CLIENT_ROLE;
-- SELECT CURRENT_ROLE(), CURRENT_WAREHOUSE();
-- SELECT * FROM PROGRAMINSIGHTS_APP.SECURITY.V_MY_CLIENT;   -- expect exactly 1 row
-- SELECT COUNT(*) FROM &CLIENT_DB.PUBLIC.MODEL;
