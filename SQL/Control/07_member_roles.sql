-- Control plane 07: Program Insights membership (licence) objects
-- Run once, as SECURITYADMIN (or the owner of PROGRAMINSIGHTS_APP), after 01-04.
--
-- Program Insights is now open to any licensed institution, not only Pilbara
-- Insights clients with their own database. The model mirrors Benchmarks:
--
--   * The Snowflake role IS the licence. A user holding <X>_PROGRAMINSIGHTS_MEMBER
--     that is mapped below can sign in; there is no second list of users.
--   * The licence is separate from Benchmarks' <X>_BENCHMARK_MEMBER, so a person
--     can hold either or both. Both maps key an institution by the same MODEL
--     value, so one institution reads the same in both products.
--   * A user holding several mapped roles (a consultant, typically) is asked which
--     institution to work as; the app never guesses.
--
-- Existing clients are unaffected: TenantResolver checks APP_USERS +
-- ROLE_DATABASE_MAP (02/03) first and only falls through to membership when that
-- path does not resolve.

-- ── 1. Role → institution map ───────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS PROGRAMINSIGHTS_APP.SECURITY.ROLE_MODEL_MAP (
    ROLE_NAME   VARCHAR(255) NOT NULL,   -- e.g. ANU_PROGRAMINSIGHTS_MEMBER
    MODEL       VARCHAR(255) NOT NULL,   -- institution key; same value as BENCHMARK_APP.SECURITY.ROLE_MODEL_MAP.MODEL
    CLIENT_NAME VARCHAR(255) NOT NULL,   -- display name
    IS_ACTIVE   BOOLEAN      NOT NULL DEFAULT TRUE,
    UPDATED_AT  TIMESTAMP_LTZ NOT NULL DEFAULT CURRENT_TIMESTAMP(),
    UPDATED_BY  VARCHAR(255)  DEFAULT CURRENT_USER()
);

-- ── 2. Institution → model database map ─────────────────────────────────────
-- Where each institution's cost model has been loaded. The app reads
-- <DATABASE_NAME>.<SCHEMA_NAME>.VW_APP_COURSE_UNIT_SUMMARY, the same view a
-- client's mapping wizard reads today. No row = the institution has no model in
-- Snowflake yet; members can still use Custom, Benchmark and CGS cluster values.
CREATE TABLE IF NOT EXISTS PROGRAMINSIGHTS_APP.SECURITY.MODEL_DATABASE_MAP (
    MODEL         VARCHAR(255) NOT NULL,
    DATABASE_NAME VARCHAR(255) NOT NULL,
    SCHEMA_NAME   VARCHAR(255) NOT NULL DEFAULT 'PUBLIC',
    IS_ACTIVE     BOOLEAN      NOT NULL DEFAULT TRUE,
    UPDATED_AT    TIMESTAMP_LTZ NOT NULL DEFAULT CURRENT_TIMESTAMP(),
    UPDATED_BY    VARCHAR(255)  DEFAULT CURRENT_USER()
);

-- ── 3. Secure views the app reads ───────────────────────────────────────────
-- Every mapped membership role the calling session can assume. CURRENT_AVAILABLE_ROLES()
-- covers inherited roles and needs no privilege, which is why Benchmarks uses it
-- rather than SHOW GRANTS. A session sees only roles it already holds.
CREATE OR REPLACE SECURE VIEW PROGRAMINSIGHTS_APP.SECURITY.V_MY_MEMBERSHIPS
    COMMENT = 'Program Insights membership roles the calling session can assume, with their institution.'
AS
SELECT m.ROLE_NAME, m.MODEL, m.CLIENT_NAME
FROM PROGRAMINSIGHTS_APP.SECURITY.ROLE_MODEL_MAP m
JOIN TABLE(FLATTEN(INPUT => PARSE_JSON(CURRENT_AVAILABLE_ROLES()))) r
  ON UPPER(TRIM(m.ROLE_NAME)) = UPPER(TRIM(r.VALUE::STRING))
WHERE m.IS_ACTIVE
  AND ENDSWITH(UPPER(TRIM(m.ROLE_NAME)), '_PROGRAMINSIGHTS_MEMBER')
QUALIFY ROW_NUMBER() OVER (PARTITION BY UPPER(m.ROLE_NAME) ORDER BY m.UPDATED_AT DESC) = 1;

-- The model database for the session's ACTIVE role (the app connects with role=).
CREATE OR REPLACE SECURE VIEW PROGRAMINSIGHTS_APP.SECURITY.V_MY_MODEL_DB
    COMMENT = 'Model database/schema for the institution of the calling session''s active membership role.'
AS
SELECT d.MODEL, d.DATABASE_NAME, d.SCHEMA_NAME
FROM PROGRAMINSIGHTS_APP.SECURITY.ROLE_MODEL_MAP m
JOIN PROGRAMINSIGHTS_APP.SECURITY.MODEL_DATABASE_MAP d
  ON d.MODEL = m.MODEL AND d.IS_ACTIVE
WHERE m.IS_ACTIVE
  AND UPPER(TRIM(m.ROLE_NAME)) = UPPER(TRIM(CURRENT_ROLE()))
QUALIFY ROW_NUMBER() OVER (ORDER BY d.UPDATED_AT DESC) = 1;

GRANT SELECT ON VIEW PROGRAMINSIGHTS_APP.SECURITY.V_MY_MEMBERSHIPS TO ROLE PUBLIC;
GRANT SELECT ON VIEW PROGRAMINSIGHTS_APP.SECURITY.V_MY_MODEL_DB    TO ROLE PUBLIC;

-- Verify (as a user holding a membership role):
-- SELECT * FROM PROGRAMINSIGHTS_APP.SECURITY.V_MY_MEMBERSHIPS;
-- USE ROLE <X>_PROGRAMINSIGHTS_MEMBER;
-- SELECT * FROM PROGRAMINSIGHTS_APP.SECURITY.V_MY_MODEL_DB;


-- ════════════════════════════════════════════════════════════════════════════
-- TEMPLATE — onboard one institution. Copy this block, replace the placeholders
-- in your editor (not with SET; several GRANT forms reject IDENTIFIER), and run
-- as SECURITYADMIN. Run 08 first so the WORKSPACE schema exists.
--
--   &MEMBER_ROLE   e.g. ANU_PROGRAMINSIGHTS_MEMBER
--   &MODEL         institution key, copied from BENCHMARK_APP.SECURITY.ROLE_MODEL_MAP
--   &CLIENT_NAME   display name
--   &MODEL_DB      database holding the institution's loaded model (skip §c if none yet)
-- ════════════════════════════════════════════════════════════════════════════
--
-- -- a. Role
-- CREATE ROLE IF NOT EXISTS &MEMBER_ROLE
--     COMMENT = 'Program Insights access for &CLIENT_NAME';
-- GRANT USAGE ON WAREHOUSE COMPUTE_WH TO ROLE &MEMBER_ROLE;
--
-- -- b. Map it. Without this row the role exists but resolves to no institution
-- --    and the app refuses the sign-in.
-- INSERT INTO PROGRAMINSIGHTS_APP.SECURITY.ROLE_MODEL_MAP (ROLE_NAME, MODEL, CLIENT_NAME)
-- SELECT '&MEMBER_ROLE', '&MODEL', '&CLIENT_NAME'
-- WHERE NOT EXISTS (SELECT 1 FROM PROGRAMINSIGHTS_APP.SECURITY.ROLE_MODEL_MAP
--                    WHERE UPPER(ROLE_NAME) = UPPER('&MEMBER_ROLE'));
--
-- -- c. The institution's model database (read-only). Only once it has been loaded.
-- INSERT INTO PROGRAMINSIGHTS_APP.SECURITY.MODEL_DATABASE_MAP (MODEL, DATABASE_NAME, SCHEMA_NAME)
-- SELECT '&MODEL', '&MODEL_DB', 'PUBLIC'
-- WHERE NOT EXISTS (SELECT 1 FROM PROGRAMINSIGHTS_APP.SECURITY.MODEL_DATABASE_MAP WHERE MODEL = '&MODEL');
-- GRANT USAGE  ON DATABASE &MODEL_DB                       TO ROLE &MEMBER_ROLE;
-- GRANT USAGE  ON SCHEMA   &MODEL_DB.PUBLIC                TO ROLE &MEMBER_ROLE;
-- GRANT SELECT ON ALL VIEWS IN SCHEMA &MODEL_DB.PUBLIC     TO ROLE &MEMBER_ROLE;
-- GRANT SELECT ON ALL TABLES IN SCHEMA &MODEL_DB.PUBLIC    TO ROLE &MEMBER_ROLE;
--
-- -- d. Saved work (WORKSPACE schema, row access policy scopes it to &MODEL)
-- GRANT ROLE PROGRAMINSIGHTS_WORKSPACE_USER TO ROLE &MEMBER_ROLE;   -- created in 08
--
-- -- e. Shared reference data. BM_FOE4_SECTOR_AGG_BASE is sector-aggregated (no
-- --    institution rows), the same table client roles are granted in 05.
-- GRANT USAGE  ON DATABASE AU_HIGHER_ED_FUNDING                          TO ROLE &MEMBER_ROLE;
-- GRANT USAGE  ON SCHEMA   AU_HIGHER_ED_FUNDING.CURATED                  TO ROLE &MEMBER_ROLE;
-- GRANT SELECT ON TABLE    AU_HIGHER_ED_FUNDING.CURATED.HELP_ALLOCATIONS TO ROLE &MEMBER_ROLE;
-- GRANT USAGE  ON DATABASE BENCHMARK_APP                                 TO ROLE &MEMBER_ROLE;
-- GRANT USAGE  ON SCHEMA   BENCHMARK_APP.CLIENT                          TO ROLE &MEMBER_ROLE;
-- GRANT SELECT ON TABLE    BENCHMARK_APP.CLIENT.BM_FOE4_SECTOR_AGG_BASE  TO ROLE &MEMBER_ROLE;
--
-- -- f. Cortex (Intelligence tab). Needs ACCOUNTADMIN.
-- GRANT DATABASE ROLE SNOWFLAKE.CORTEX_USER TO ROLE &MEMBER_ROLE;
--
-- -- g. Let consultants switch to it, as Benchmarks does for its member roles.
-- GRANT ROLE &MEMBER_ROLE TO ROLE "PILBARA-CONSULTANTS";
--
-- -- h. Assign to people. Holding the role is enough; DEFAULT_ROLE need not change.
-- GRANT ROLE &MEMBER_ROLE TO USER "jane.doe@university.edu.au";
