SET @column_exists := (
  SELECT COUNT(*)
  FROM INFORMATION_SCHEMA.COLUMNS
  WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'tbl_project'
    AND COLUMN_NAME = 'sitesize'
);

SET @sql := IF(
  @column_exists = 0,
  'ALTER TABLE tbl_project ADD COLUMN sitesize DECIMAL(6,2) NOT NULL DEFAULT 1.00',
  'SELECT ''sitesize already exists'' AS message'
);

PREPARE stmt FROM @sql;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;
