-- Additive and idempotent. Apply to BOTH regional DBs via --rf-log-db-migrate.
-- Existing manually provisioned credentials are retained.
CREATE TABLE IF NOT EXISTS rf_mobile_enrollments (
    id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    region CHAR(2) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    user_id INT NOT NULL,
    company_id INT NOT NULL,
    mobile_number VARCHAR(40) NOT NULL,
    company_code VARCHAR(100) NOT NULL,
    device_label VARCHAR(100) NOT NULL,
    proof_hash CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    status VARCHAR(16) NOT NULL DEFAULT 'pending',
    created_at DATETIME(6) NOT NULL,
    request_expires_at DATETIME(6) NOT NULL,
    approved_at DATETIME(6) NULL,
    approval_expires_at DATETIME(6) NULL,
    latest_renewal_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NULL,
    PRIMARY KEY (id,region),
    KEY ix_rf_enrollment_company (region,company_id,status,created_at),
    KEY ix_rf_enrollment_user (region,user_id,status,request_expires_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
