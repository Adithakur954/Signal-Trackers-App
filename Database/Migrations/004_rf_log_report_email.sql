-- Apply to BOTH regional databases. Only explicit report subscribers receive email.
CREATE TABLE IF NOT EXISTS rf_log_report_recipients (
    id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    region VARCHAR(2) NOT NULL,
    company_id INT NOT NULL,
    email VARCHAR(254) CHARACTER SET ascii COLLATE ascii_general_ci NOT NULL,
    name VARCHAR(100) NOT NULL,
    enabled TINYINT(1) NOT NULL DEFAULT 1,
    created_at DATETIME(6) NOT NULL,
    UNIQUE INDEX ux_rf_recipient (region,company_id,email)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS rf_log_email_deliveries (
    id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    upload_id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    recipient_id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    region VARCHAR(2) NOT NULL,
    company_id INT NOT NULL,
    report_reference VARCHAR(2048) NOT NULL,
    status VARCHAR(24) NOT NULL DEFAULT 'pending',
    attempts INT NOT NULL DEFAULT 0,
    next_attempt DATETIME(6) NOT NULL,
    lease_token CHAR(32) NULL,
    lease_until DATETIME(6) NULL,
    created_at DATETIME(6) NOT NULL,
    sent_at DATETIME(6) NULL,
    last_error VARCHAR(500) NULL,
    UNIQUE INDEX ux_rf_email_once (region,upload_id,recipient_id),
    UNIQUE INDEX ux_rf_email_token (lease_token),
    INDEX ix_rf_email_pending (region,status,next_attempt,lease_until),
    INDEX ix_rf_email_company (region,company_id,created_at)
) ENGINE=InnoDB;
