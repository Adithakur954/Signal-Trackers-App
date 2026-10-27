-- Apply separately to BOTH MySqlConnection (India) and MySqlConnection2 (Taiwan)
-- before enabling the integration. Each region stores only its own records.
CREATE TABLE IF NOT EXISTS rf_log_uploads (
    id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    client_id VARCHAR(80) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    company_id INT NOT NULL,
    region VARCHAR(2) NOT NULL DEFAULT 'IN',
    metadata JSON NOT NULL,
    staging_key VARCHAR(512) NOT NULL,
    object_key VARCHAR(512) NULL,
    status VARCHAR(24) NOT NULL,
    created_at DATETIME(6) NOT NULL,
    uploaded_at DATETIME(6) NULL,
    etag VARCHAR(200) NULL,
    report_token CHAR(32) NULL,
    report_lease_until DATETIME(6) NULL,
    report_reference VARCHAR(2048) NULL,
    report_error VARCHAR(2000) NULL,
    notify_status VARCHAR(24) NOT NULL DEFAULT 'pending',
    notify_attempts INT NOT NULL DEFAULT 0,
    notify_after DATETIME(6) NULL,
    notify_token CHAR(32) NULL,
    notify_until DATETIME(6) NULL,
    INDEX ix_rf_pending (status, report_lease_until, created_at),
    INDEX ix_rf_notify (notify_status, notify_after, notify_until),
    UNIQUE INDEX ux_rf_notify_token (notify_token)
) ENGINE=InnoDB;
