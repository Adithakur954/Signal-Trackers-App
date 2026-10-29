-- Apply to BOTH IN and TW databases via --rf-log-db-migrate before enabling mobile uploads.
-- No credentials are issued by migration. Only a SHA-256 digest is persisted.
CREATE TABLE IF NOT EXISTS rf_mobile_upload_credentials (
    id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    region CHAR(2) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    company_id INT NOT NULL,
    user_id INT NOT NULL,
    mobile_number VARCHAR(40) NOT NULL,
    company_code VARCHAR(100) NOT NULL,
    device_label VARCHAR(100) NOT NULL,
    token_hash CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    created_at DATETIME(6) NOT NULL,
    expires_at DATETIME(6) NOT NULL,
    revoked_at DATETIME(6) NULL,
    PRIMARY KEY (id,region),
    KEY ix_rf_mobile_company (region,company_id,created_at),
    KEY ix_rf_mobile_user (region,user_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
