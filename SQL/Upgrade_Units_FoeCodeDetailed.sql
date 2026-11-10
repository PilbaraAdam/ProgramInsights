-- Migration 8: Add FOE_CODE_DETAILED to UNITS
-- Stores the detailed FOE code assigned to each unit, used to auto-assign CGS funding cluster rates.
ALTER TABLE UNITS ADD COLUMN IF NOT EXISTS FOE_CODE_DETAILED VARCHAR(100) DEFAULT '';
UPDATE UNITS SET FOE_CODE_DETAILED = '' WHERE FOE_CODE_DETAILED IS NULL;
