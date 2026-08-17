-- Verified duplicate-index cleanup candidates for large network imports.
-- Generated from information_schema.STATISTICS and SHOW CREATE TABLE.
-- Review during a maintenance window before running on production.

-- tbl_network_log_neighbour has four identical non-unique BTREE indexes:
--   idx_neigh_band_lat_lon        (band, lat, lon)
--   idx_neighbour_band_lat_lon    (band, lat, lon)  -- keep this one
--   idx_neighbour_band_lat_lon1   (band, lat, lon)
--   idx_neighbour_band_lat_lon2   (band, lat, lon)
ALTER TABLE tbl_network_log_neighbour
    DROP INDEX idx_neigh_band_lat_lon,
    DROP INDEX idx_neighbour_band_lat_lon1,
    DROP INDEX idx_neighbour_band_lat_lon2;

-- tbl_network_log has two identical non-unique BTREE indexes:
--   idx_log_session_lat_lon       (session_id, lat, lon)
--   idx_session_lat_lon           (session_id, lat, lon)  -- keep this one
ALTER TABLE tbl_network_log
    DROP INDEX idx_log_session_lat_lon;
