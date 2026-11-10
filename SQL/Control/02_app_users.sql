-- Control plane 02: APP_USERS — the app-level access gate
-- Run once, after 01.
--
-- This is deliberately SEPARATE from Snowflake RBAC. Snowflake grants decide what
-- data a user can reach; this table decides whether the app lets them in at all.
-- That gives support a way to switch someone off without touching Snowflake roles.
--
-- PRINCIPAL is the Entra identity (UPN / preferred_username / email) and must match
-- the Snowflake user's LOGIN_NAME, since the app connects with user=<principal>.

CREATE TABLE IF NOT EXISTS PROGRAMINSIGHTS_APP.SECURITY.APP_USERS (
    PRINCIPAL   VARCHAR(255) NOT NULL,
    IS_ACTIVE   BOOLEAN      NOT NULL DEFAULT TRUE,
    DISPLAY_NAME VARCHAR(255),
    NOTES       VARCHAR(1000),
    UPDATED_AT  TIMESTAMP_LTZ NOT NULL DEFAULT CURRENT_TIMESTAMP(),
    UPDATED_BY  VARCHAR(255)  DEFAULT CURRENT_USER()
);

-- No PRIMARY KEY on purpose: Snowflake does not enforce them, and the app's read
-- path (see 04) dedupes with QUALIFY ROW_NUMBER() OVER (ORDER BY UPDATED_AT DESC)
-- so a re-inserted row supersedes the old one rather than erroring.

-- Seed yourself so you can sign in. Replace with your real Entra UPN.
-- MERGE keeps this script re-runnable.
MERGE INTO PROGRAMINSIGHTS_APP.SECURITY.APP_USERS t
USING (SELECT 'adam.gallard@pilbaragroup.com' AS PRINCIPAL) s
   ON LOWER(TRIM(t.PRINCIPAL)) = LOWER(TRIM(s.PRINCIPAL))
WHEN NOT MATCHED THEN
    INSERT (PRINCIPAL, IS_ACTIVE, DISPLAY_NAME, NOTES)
    VALUES (s.PRINCIPAL, TRUE, 'Adam Gallard', 'Initial admin user');

-- Deliberately NO grants on this base table. Every client role would otherwise be
-- able to read every other client's user list. The app reads V_MY_APP_USER (04).
