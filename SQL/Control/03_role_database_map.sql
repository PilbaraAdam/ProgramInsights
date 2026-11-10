-- Control plane 03: ROLE_DATABASE_MAP — routes a Snowflake role to a client database
-- Run once, after 01.
--
-- This is the heart of the multi-tenant design. After login the app runs
-- SELECT CURRENT_ROLE() and looks the result up here to decide which database to
-- point every subsequent connection at. The user's Snowflake DEFAULT_ROLE is
-- therefore what determines which client's data they see.
--
-- One row per role. A role maps to exactly one database.

CREATE TABLE IF NOT EXISTS PROGRAMINSIGHTS_APP.SECURITY.ROLE_DATABASE_MAP (
    ROLE_NAME     VARCHAR(255) NOT NULL,
    DATABASE_NAME VARCHAR(255) NOT NULL,
    CLIENT_NAME   VARCHAR(255) NOT NULL,
    TENANT_ID     VARCHAR(36)  NOT NULL,   -- written to MODEL.TENANT by the app
    IS_ACTIVE     BOOLEAN      NOT NULL DEFAULT TRUE,
    UPDATED_AT    TIMESTAMP_LTZ NOT NULL DEFAULT CURRENT_TIMESTAMP(),
    UPDATED_BY    VARCHAR(255)  DEFAULT CURRENT_USER()
);

-- Tenant #1: the existing PILBARA_PRI database. Its state tables are already in
-- place, so no data migration is needed for this client (see 06).
--
-- TENANT_ID reuses the GUID that was previously hardcoded as App:TenantId in
-- appsettings.json, so existing MODEL.TENANT rows keep matching.
--
-- ROLE_NAME must match CURRENT_ROLE() exactly as Snowflake reports it. Confirm with:
--     SELECT CURRENT_ROLE();
-- Note that a role created with a lowercase/hyphenated name is case-sensitive in
-- Snowflake's metadata, though V_MY_CLIENT (04) compares case-insensitively so either
-- casing here will still match.
MERGE INTO PROGRAMINSIGHTS_APP.SECURITY.ROLE_DATABASE_MAP t
USING (
    SELECT 'PILBARA-CONSULTANTS'                  AS ROLE_NAME,
           'PILBARA_PRI'                          AS DATABASE_NAME,
           'Pilbara'                              AS CLIENT_NAME,
           '06fe6ac7-040a-414a-bec7-6d1223582a8c' AS TENANT_ID
) s
   ON UPPER(TRIM(t.ROLE_NAME)) = UPPER(TRIM(s.ROLE_NAME))
WHEN NOT MATCHED THEN
    INSERT (ROLE_NAME, DATABASE_NAME, CLIENT_NAME, TENANT_ID)
    VALUES (s.ROLE_NAME, s.DATABASE_NAME, s.CLIENT_NAME, s.TENANT_ID);

-- As with APP_USERS: no grants on the base table. The app reads V_MY_CLIENT (04).
