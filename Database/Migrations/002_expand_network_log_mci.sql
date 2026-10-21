-- Select the intended region database before running this migration.
-- Run during a maintenance window: ALTER may rebuild/lock large tables.
-- Expands VARCHAR(45) to TEXT without truncating existing values.
-- TEXT supports up to 65,535 bytes, not necessarily 65,535 characters.
-- Apply to both tables because import copies mci into neighbour logs too.
ALTER TABLE tbl_network_log MODIFY COLUMN mci TEXT NULL;
ALTER TABLE tbl_network_log_neighbour MODIFY COLUMN mci TEXT NULL;

SELECT TABLE_NAME, COLUMN_NAME, COLUMN_TYPE, CHARACTER_MAXIMUM_LENGTH
FROM information_schema.COLUMNS
WHERE TABLE_SCHEMA = DATABASE()
  AND TABLE_NAME IN ('tbl_network_log', 'tbl_network_log_neighbour')
  AND COLUMN_NAME = 'mci';
