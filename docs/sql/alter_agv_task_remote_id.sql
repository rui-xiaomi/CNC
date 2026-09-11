-- 已有库升级：保存 RCS 回包任务号。列已存在时 Duplicate column 可忽略。
-- 勿在生产库执行 cnc_schema.sql（会 DROP 全表）。
USE cnc_auto;

ALTER TABLE MAS_AUTO_AGV_TASK
  ADD COLUMN RCS_REMOTE_ID VARCHAR(50) NULL COMMENT 'RCS ACK 回包号（CNC_WMS_TASK_2_…），cancelTask 用' AFTER RCS_TASK_ID;

ALTER TABLE MAS_AUTO_AGV_TASK
  ADD KEY idx_agv_rcs_remote (RCS_REMOTE_ID);
