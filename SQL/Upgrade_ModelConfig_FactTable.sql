-- Upgrade: add FACT_TABLE column to MODEL_CONFIG
-- Run once against the target Snowflake database

ALTER TABLE MODEL_CONFIG ADD COLUMN IF NOT EXISTS FACT_TABLE VARCHAR;

-- Backfill existing rows with the previous hard-coded default
UPDATE MODEL_CONFIG SET FACT_TABLE = 'FACT_GL_TO_PRODUCTS_TO_COURSES' WHERE FACT_TABLE IS NULL;
