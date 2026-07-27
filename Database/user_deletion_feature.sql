-- User data deletion feature.
-- Apply this script to every application database that stores users/data
-- (for example Stracer and TaiwanDB).

DELIMITER $$

DROP PROCEDURE IF EXISTS add_column_if_missing $$
CREATE PROCEDURE add_column_if_missing(
    IN tableName VARCHAR(64),
    IN columnName VARCHAR(64),
    IN columnDefinition TEXT
)
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_schema = DATABASE()
          AND table_name = tableName
          AND column_name = columnName
    ) THEN
        SET @sql = CONCAT('ALTER TABLE `', tableName, '` ADD COLUMN `', columnName, '` ', columnDefinition);
        PREPARE stmt FROM @sql;
        EXECUTE stmt;
        DEALLOCATE PREPARE stmt;
    END IF;
END $$

DROP PROCEDURE IF EXISTS add_index_if_missing $$
CREATE PROCEDURE add_index_if_missing(
    IN tableName VARCHAR(64),
    IN indexName VARCHAR(64),
    IN indexDefinition TEXT
)
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.statistics
        WHERE table_schema = DATABASE()
          AND table_name = tableName
          AND index_name = indexName
    ) THEN
        SET @sql = CONCAT('CREATE INDEX `', indexName, '` ON `', tableName, '` ', indexDefinition);
        PREPARE stmt FROM @sql;
        EXECUTE stmt;
        DEALLOCATE PREPARE stmt;
    END IF;
END $$

DELIMITER ;

CALL add_column_if_missing('tbl_user', 'is_deleted', 'TINYINT(1) NOT NULL DEFAULT 0');
CALL add_column_if_missing('tbl_user', 'deletion_requested_at', 'DATETIME NULL');

CALL add_index_if_missing('tbl_user', 'ix_tbl_user_mobile', '(`mobile`)');
CALL add_index_if_missing('tbl_user', 'ix_tbl_user_deletion_due', '(`is_deleted`, `deletion_requested_at`)');

CREATE TABLE IF NOT EXISTS tbl_user_deletion_otp (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    user_id INT NOT NULL,
    phone_number VARCHAR(32) NOT NULL,
    otp_hash VARCHAR(255) NOT NULL,
    expires_at DATETIME NOT NULL,
    attempt_count INT NOT NULL DEFAULT 0,
    max_attempts INT NOT NULL DEFAULT 5,
    resend_available_at DATETIME NOT NULL,
    consumed_at DATETIME NULL,
    blocked_at DATETIME NULL,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX ix_otp_phone_created (phone_number, created_at),
    INDEX ix_otp_user_active (user_id, consumed_at, expires_at)
);

CREATE TABLE IF NOT EXISTS tbl_user_deletion_token (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    user_id INT NOT NULL,
    phone_number VARCHAR(32) NOT NULL,
    token_hash VARCHAR(255) NOT NULL,
    expires_at DATETIME NOT NULL,
    used_at DATETIME NULL,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX ix_token_hash (token_hash),
    INDEX ix_token_user (user_id, expires_at)
);

CREATE TABLE IF NOT EXISTS tbl_user_deletion_audit (
    id BIGINT AUTO_INCREMENT PRIMARY KEY,
    user_id INT NULL,
    phone_number VARCHAR(32) NULL,
    event_type VARCHAR(64) NOT NULL,
    event_status VARCHAR(32) NOT NULL,
    ip_address VARCHAR(64) NULL,
    user_agent VARCHAR(512) NULL,
    message VARCHAR(512) NULL,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX ix_audit_user (user_id, created_at),
    INDEX ix_audit_phone (phone_number, created_at)
);

DROP PROCEDURE IF EXISTS add_column_if_missing;
DROP PROCEDURE IF EXISTS add_index_if_missing;
