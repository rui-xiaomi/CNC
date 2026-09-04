-- 幂等迁移：为已存在的 MAS_AUTO_WORK_RECORD 补加工记录查询索引（P1-2）。
-- 适用现场已有库；重复执行安全（已存在则跳过）。
-- 索引用途：
--   idx_rec_start              → 当班统计 GetShiftStatsAsync 的 WORK_START_TIME 范围 COUNT
--   idx_rec_eq_pos_result      → RecordStartAsync / FindOpenByPositionAsync 的未结束记录定位

SET @idx := (
    SELECT COUNT(*)
    FROM information_schema.statistics
    WHERE table_schema = DATABASE()
      AND table_name = 'MAS_AUTO_WORK_RECORD'
      AND index_name = 'idx_rec_start'
);
SET @sql := IF(@idx = 0,
    'ALTER TABLE MAS_AUTO_WORK_RECORD ADD INDEX idx_rec_start (WORK_START_TIME)',
    'SELECT ''idx_rec_start already exists'' AS msg');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @idx := (
    SELECT COUNT(*)
    FROM information_schema.statistics
    WHERE table_schema = DATABASE()
      AND table_name = 'MAS_AUTO_WORK_RECORD'
      AND index_name = 'idx_rec_eq_pos_result'
);
SET @sql := IF(@idx = 0,
    'ALTER TABLE MAS_AUTO_WORK_RECORD ADD INDEX idx_rec_eq_pos_result (EQUIMENT_ID, POSITION_CODE, WORK_RESULT)',
    'SELECT ''idx_rec_eq_pos_result already exists'' AS msg');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
