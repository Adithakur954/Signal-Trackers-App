CREATE TABLE IF NOT EXISTS tbl_indoor_planning_floor (
  id INT NOT NULL AUTO_INCREMENT,
  project_name VARCHAR(255) NOT NULL,
  floor_name VARCHAR(100) NOT NULL DEFAULT 'Level 1',
  plan_json LONGTEXT NULL,
  created_by_user_id INT NULL,
  created_by_user_name VARCHAR(255) NULL,
  created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (id),
  INDEX ix_indoor_planning_floor_updated_at (updated_at)
);
