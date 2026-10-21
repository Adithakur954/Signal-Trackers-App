-- Run once per region database in a maintenance window, before deploying.
-- GetNetworkLog deliberately does not perform DDL or Redis key scans anymore.
-- Review existing indexes and table size first. No data rows are deleted.
DELIMITER $$

DROP PROCEDURE IF EXISTS st_networklog_column $$
CREATE PROCEDURE st_networklog_column(IN column_name_in VARCHAR(128), IN definition_in TEXT)
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = DATABASE() AND table_name = 'tbl_network_log'
          AND column_name = column_name_in
    ) THEN
        SET @ddl = CONCAT('ALTER TABLE tbl_network_log ADD COLUMN `', column_name_in, '` ', definition_in);
        PREPARE stmt FROM @ddl;
        EXECUTE stmt;
        DEALLOCATE PREPARE stmt;
    END IF;
END $$

DROP PROCEDURE IF EXISTS st_networklog_index $$
CREATE PROCEDURE st_networklog_index(IN index_name_in VARCHAR(128), IN columns_in TEXT)
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.statistics
        WHERE table_schema = DATABASE() AND table_name = 'tbl_network_log'
          AND index_name = index_name_in
    ) AND NOT EXISTS (
        SELECT 1 FROM information_schema.statistics
        WHERE table_schema = DATABASE() AND table_name = 'tbl_network_log'
        GROUP BY index_name
        HAVING REPLACE(GROUP_CONCAT(column_name ORDER BY seq_in_index), ' ', '') = REPLACE(columns_in, ' ', '')
    ) THEN
        SET @ddl = CONCAT('CREATE INDEX `', index_name_in, '` ON tbl_network_log (', columns_in, ')');
        PREPARE stmt FROM @ddl;
        EXECUTE stmt;
        DEALLOCATE PREPARE stmt;
    END IF;
END $$
DELIMITER ;

CALL st_networklog_column('updated_at', 'DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP');
CALL st_networklog_column('hotspot', 'LONGTEXT NULL');
CALL st_networklog_column('hotspot_symbol', 'VARCHAR(100) NULL');
CALL st_networklog_column('hotspot_line_json', 'LONGTEXT NULL');
CALL st_networklog_index('idx_networklog_session_timestamp_id', 'session_id, timestamp, id');
CALL st_networklog_index('idx_networklog_session_network_timestamp_id', 'session_id, network, timestamp, id');
CALL st_networklog_index('idx_networklog_session_lat_lon', 'session_id, lat, lon');

DROP PROCEDURE IF EXISTS st_networklog_column;
DROP PROCEDURE IF EXISTS st_networklog_index;
