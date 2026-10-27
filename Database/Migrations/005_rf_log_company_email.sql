-- Existing installations only: apply once to EACH regional database after 004.
-- The --rf-log-db-migrate command checks for the column before running this file.
-- No recipients or historical deliveries are created by this migration.
ALTER TABLE rf_log_report_recipients
    ADD COLUMN company_default TINYINT(1) NOT NULL DEFAULT 0 AFTER enabled;
