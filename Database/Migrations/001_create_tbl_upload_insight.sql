CREATE TABLE IF NOT EXISTS tbl_upload_insight (
    id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    upload_id INT NULL,
    session_id INT NULL,
    source VARCHAR(32) NOT NULL,
    source_file_name VARCHAR(255) NULL,
    severity VARCHAR(32) NULL,
    title VARCHAR(512) NOT NULL,
    insight_time VARCHAR(64) NULL,
    latitude DOUBLE NULL,
    longitude DOUBLE NULL,
    description LONGTEXT NULL,
    details_json LONGTEXT NULL,
    raw_text LONGTEXT NOT NULL,
    uploaded_on DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX ix_upload_insight_upload (upload_id),
    INDEX ix_upload_insight_session (session_id),
    INDEX ix_upload_insight_severity (severity)
);
