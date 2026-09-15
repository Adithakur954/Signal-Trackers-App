-- Backend performance indexes for frequently queried Signal Tracker tables.
-- Run this once per database during a maintenance window.
-- MySQL 8.0 does not support CREATE INDEX IF NOT EXISTS, so this helper
-- checks information_schema before creating each index.

DELIMITER $$

DROP PROCEDURE IF EXISTS add_index_if_missing $$
CREATE PROCEDURE add_index_if_missing(
    IN table_name_in VARCHAR(128),
    IN index_name_in VARCHAR(128),
    IN index_columns_in TEXT
)
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.statistics
        WHERE table_schema = DATABASE()
          AND table_name = table_name_in
          AND index_name = index_name_in
    ) AND NOT EXISTS (
        -- A previous deployment may have created the same index under a
        -- different name. Do not create another equivalent index.
        SELECT 1
        FROM information_schema.statistics s
        WHERE s.table_schema = DATABASE()
          AND s.table_name = table_name_in
          AND s.non_unique = 1
        GROUP BY s.index_name
        HAVING REPLACE(GROUP_CONCAT(s.column_name ORDER BY s.seq_in_index), ' ', '')
             = REPLACE(index_columns_in, ' ', '')
    ) THEN
        SET @sql = CONCAT(
            'CREATE INDEX ',
            index_name_in,
            ' ON ',
            table_name_in,
            ' (',
            index_columns_in,
            ')'
        );
        PREPARE stmt FROM @sql;
        EXECUTE stmt;
        DEALLOCATE PREPARE stmt;
    END IF;
END $$

DELIMITER ;

CALL add_index_if_missing('tbl_network_log', 'ix_network_log_session_time_id', 'session_id, timestamp, id');
CALL add_index_if_missing('tbl_network_log', 'ix_network_log_timestamp_session', 'timestamp, session_id');
CALL add_index_if_missing('tbl_network_log', 'ix_network_log_company_session', 'company_id, session_id');
CALL add_index_if_missing('tbl_network_log', 'ix_network_log_session_kpi', 'session_id, rsrp, rsrq, sinr, mos');
CALL add_index_if_missing('tbl_network_log_neighbour', 'ix_network_log_neighbour_session_time_id', 'session_id, timestamp, id');
-- Diagnostic summary loads both tables by session and returns rows in id order.
-- These indexes avoid a full scan/filesort for the summary endpoint.
CALL add_index_if_missing('tbl_event_log', 'ix_event_log_session_id_id', 'session_id, id');
CALL add_index_if_missing('tbl_l3_log', 'ix_l3_log_session_id_id', 'session_id, id');
CALL add_index_if_missing('tbl_project_clutter_tile', 'ix_project_clutter_tile_active', 'project_id, is_active');

CALL add_index_if_missing('tbl_session', 'ix_session_user_start_id', 'user_id, start_time, id');
CALL add_index_if_missing('tbl_session', 'ix_session_upload_id', 'tbl_upload_id');

CALL add_index_if_missing('tbl_project', 'ix_project_company_status_id', 'company_id, status, id');
CALL add_index_if_missing('tbl_project', 'ix_project_created_by_status_id', 'created_by_user_id, status, id');

CALL add_index_if_missing('site_prediction', 'ix_site_prediction_project_upload_id', 'tbl_project_id, tbl_upload_id, id');
CALL add_index_if_missing('site_prediction', 'ix_site_prediction_project_site_cell', 'tbl_project_id, site, cell_id');
CALL add_index_if_missing('site_prediction_optimized', 'ix_site_prediction_opt_project_scenario_id', 'tbl_project_id, scenario, id');
CALL add_index_if_missing('site_prediction_optimized', 'ix_site_prediction_opt_source_scenario', 'site_prediction_id, scenario');

DROP PROCEDURE IF EXISTS add_index_if_missing;
