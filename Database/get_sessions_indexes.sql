-- Run once against each application database (IN and TW) before deploying.
-- Index creation belongs in database maintenance, never in GetSessions requests.

SET @sessions_index_sql = IF(
    EXISTS (SELECT 1 FROM information_schema.statistics
            WHERE table_schema = DATABASE() AND table_name = 'tbl_network_log'
            GROUP BY index_name
            HAVING GROUP_CONCAT(column_name ORDER BY seq_in_index) = 'session_id,lat,lon'
                OR GROUP_CONCAT(column_name ORDER BY seq_in_index) LIKE 'session_id,lat,lon,%'),
    'SELECT ''Network log lookup index already exists'' AS message',
    'CREATE INDEX idx_networklog_session_lat_lon ON tbl_network_log (session_id, lat, lon)');
PREPARE sessions_index_stmt FROM @sessions_index_sql;
EXECUTE sessions_index_stmt;
DEALLOCATE PREPARE sessions_index_stmt;

SET @sessions_index_sql = IF(
    EXISTS (SELECT 1 FROM information_schema.statistics
            WHERE table_schema = DATABASE() AND table_name = 'tbl_session'
              AND column_name = 'user_id' AND seq_in_index = 1),
    'SELECT ''Session user lookup index already exists'' AS message',
    'CREATE INDEX idx_session_user_id_id ON tbl_session (user_id, id)');
PREPARE sessions_index_stmt FROM @sessions_index_sql;
EXECUTE sessions_index_stmt;
DEALLOCATE PREPARE sessions_index_stmt;

SET @sessions_index_sql = IF(
    EXISTS (SELECT 1 FROM information_schema.statistics
            WHERE table_schema = DATABASE() AND table_name = 'tbl_user'
              AND column_name = 'company_id' AND seq_in_index = 1),
    'SELECT ''Company user lookup index already exists'' AS message',
    'CREATE INDEX idx_user_company_id_id ON tbl_user (company_id, id)');
PREPARE sessions_index_stmt FROM @sessions_index_sql;
EXECUTE sessions_index_stmt;
DEALLOCATE PREPARE sessions_index_stmt;
