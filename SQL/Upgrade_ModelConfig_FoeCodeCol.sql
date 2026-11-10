-- Migration 4: Add FOE_CODE_COL to MODEL_CONFIG
-- Stores the column name in the unit dimension table that holds the detailed FOE code.
ALTER TABLE MODEL_CONFIG ADD COLUMN IF NOT EXISTS FOE_CODE_COL VARCHAR(100) DEFAULT '';

-- Backfill existing rows (DEFAULT only applies to rows inserted after the ALTER)
UPDATE MODEL_CONFIG SET FOE_CODE_COL = '' WHERE FOE_CODE_COL IS NULL;
