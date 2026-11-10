-- Control plane 04: secure views + global CONSTANTS
-- Run once, after 02 and 03.
--
-- These two views are the ONLY things the app reads from the SECURITY schema.
-- Each one filters to the calling session, so a client role can see its own row
-- and nothing else — the same protection a row access policy would give, without
-- needing RLS (which is out of scope for this round).
--
-- SECURE is significant: it stops Snowflake's optimiser from leaking
-- non-visible rows via query plans or error messages, and hides the view body
-- from users who only have SELECT.

-- CURRENT_USER() is the Snowflake login name, which the External OAuth
-- integration maps from the Entra UPN — so it equals APP_USERS.PRINCIPAL.
CREATE OR REPLACE SECURE VIEW PROGRAMINSIGHTS_APP.SECURITY.V_MY_APP_USER
    COMMENT = 'The calling user''s own APP_USERS row. Used as the app-level access gate at login.'
AS
SELECT PRINCIPAL, IS_ACTIVE, DISPLAY_NAME
FROM PROGRAMINSIGHTS_APP.SECURITY.APP_USERS
WHERE LOWER(TRIM(PRINCIPAL)) = LOWER(TRIM(CURRENT_USER()))
QUALIFY ROW_NUMBER() OVER (ORDER BY UPDATED_AT DESC) = 1;

-- Routes the calling session's active role to its client database.
CREATE OR REPLACE SECURE VIEW PROGRAMINSIGHTS_APP.SECURITY.V_MY_CLIENT
    COMMENT = 'The client database/tenant for the calling session''s active role.'
AS
SELECT DATABASE_NAME, CLIENT_NAME, TENANT_ID
FROM PROGRAMINSIGHTS_APP.SECURITY.ROLE_DATABASE_MAP
WHERE UPPER(TRIM(ROLE_NAME)) = UPPER(TRIM(CURRENT_ROLE()))
  AND IS_ACTIVE
QUALIFY ROW_NUMBER() OVER (ORDER BY UPDATED_AT DESC) = 1;

GRANT SELECT ON VIEW PROGRAMINSIGHTS_APP.SECURITY.V_MY_APP_USER TO ROLE PUBLIC;
GRANT SELECT ON VIEW PROGRAMINSIGHTS_APP.SECURITY.V_MY_CLIENT   TO ROLE PUBLIC;


-- ── Global reference data ───────────────────────────────────────────────────
-- CONSTANTS is genuinely global (shared lookup values), so it lives here in the
-- control plane rather than being duplicated into every client database.
-- ProgramService reads it fully qualified as PROGRAMINSIGHTS_APP.SECURITY.CONSTANTS.
CREATE TABLE IF NOT EXISTS PROGRAMINSIGHTS_APP.SECURITY.CONSTANTS (
    NAME   VARCHAR(255) NOT NULL,
    VALUE1 FLOAT,
    TYPE   VARCHAR(100) NOT NULL
);

GRANT SELECT ON TABLE PROGRAMINSIGHTS_APP.SECURITY.CONSTANTS TO ROLE PUBLIC;

-- One-time copy of the existing rows out of PILBARA_PRI. Safe to re-run: the
-- NOT EXISTS guard makes it a no-op once the rows are present.
INSERT INTO PROGRAMINSIGHTS_APP.SECURITY.CONSTANTS (NAME, VALUE1, TYPE)
SELECT c.NAME, c.VALUE1, c.TYPE
FROM PILBARA_PRI.PUBLIC.CONSTANTS c
WHERE NOT EXISTS (
    SELECT 1 FROM PROGRAMINSIGHTS_APP.SECURITY.CONSTANTS x
    WHERE x.NAME = c.NAME AND x.TYPE = c.TYPE
);

-- Verify before moving on — expect your principal, your role, PILBARA_PRI, and a row count.
-- SELECT * FROM PROGRAMINSIGHTS_APP.SECURITY.V_MY_APP_USER;
-- SELECT * FROM PROGRAMINSIGHTS_APP.SECURITY.V_MY_CLIENT;
-- SELECT COUNT(*) FROM PROGRAMINSIGHTS_APP.SECURITY.CONSTANTS;
