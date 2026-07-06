-- =============================================================
-- 第四阶段迁移：RCS 对接 + 上下料任务
-- 依据：docs/客户端开发文档.md §4.5 / §5.5 / §6 / §12
-- 目标：为已存在的 cnc_auto 库补齐 RCS 任务跟踪、点位映射、报文流水、
--       料架槽位账目字段与料架角色扩展。
-- 特性：幂等（重复执行安全）、可回滚（末尾附回滚段，默认注释）。
-- 执行：mysql --default-character-set=utf8mb4 -u<user> -p cnc_auto < migration_phase4_rcs.sql
-- =============================================================

SET NAMES utf8mb4;
USE cnc_auto;

-- -------------------------------------------------------------
-- 幂等辅助：仅当列不存在时才 ADD COLUMN（MySQL 8 不支持 ADD COLUMN IF NOT EXISTS）
-- -------------------------------------------------------------
DROP PROCEDURE IF EXISTS sp_add_col_if_absent;
DELIMITER //
CREATE PROCEDURE sp_add_col_if_absent(
  IN p_table VARCHAR(64), IN p_col VARCHAR(64), IN p_ddl VARCHAR(1000))
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.COLUMNS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = p_table AND COLUMN_NAME = p_col
  ) THEN
    SET @s = CONCAT('ALTER TABLE `', p_table, '` ADD COLUMN ', p_ddl);
    PREPARE stmt FROM @s; EXECUTE stmt; DEALLOCATE PREPARE stmt;
  END IF;
END //
DELIMITER ;

DROP PROCEDURE IF EXISTS sp_add_index_if_absent;
DELIMITER //
CREATE PROCEDURE sp_add_index_if_absent(
  IN p_table VARCHAR(64), IN p_index VARCHAR(64), IN p_cols VARCHAR(255))
BEGIN
  IF NOT EXISTS (
    SELECT 1 FROM information_schema.STATISTICS
    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = p_table AND INDEX_NAME = p_index
  ) THEN
    SET @s = CONCAT('ALTER TABLE `', p_table, '` ADD INDEX `', p_index, '` (', p_cols, ')');
    PREPARE stmt FROM @s; EXECUTE stmt; DEALLOCATE PREPARE stmt;
  END IF;
END //
DELIMITER ;

-- -------------------------------------------------------------
-- 1. 扩展 MAS_AUTO_AGV_TASK：承载 RCS 任务全生命周期
-- -------------------------------------------------------------
CALL sp_add_col_if_absent('MAS_AUTO_AGV_TASK', 'RCS_TASK_ID',
  "RCS_TASK_ID VARCHAR(50) NULL COMMENT 'RCS 全局唯一 taskId（先落库后发送）' AFTER ID1");
CALL sp_add_col_if_absent('MAS_AUTO_AGV_TASK', 'RCS_KIND',
  "RCS_KIND VARCHAR(20) NULL COMMENT 'RCS 接口种类 transit/grab/identify/pallet_return/change_frame' AFTER RCS_TASK_ID");
CALL sp_add_col_if_absent('MAS_AUTO_AGV_TASK', 'RCS_STATUS',
  "RCS_STATUS VARCHAR(20) NULL COMMENT 'RCS 原始 11 态 queued/underway/completed/failed/canceled...'");
CALL sp_add_col_if_absent('MAS_AUTO_AGV_TASK', 'TASK_STATE',
  "TASK_STATE VARCHAR(20) NOT NULL DEFAULT 'CREATED' COMMENT '本系统 5+1 态 CREATED/DISPATCHED/EXECUTING/COMPLETED/FAILED/CANCELED'");
CALL sp_add_col_if_absent('MAS_AUTO_AGV_TASK', 'PRIORITY',
  "PRIORITY INT NOT NULL DEFAULT 5 COMMENT '优先级 1-10 大者优先（换架/回收≥紧急下料>常规上料）'");
CALL sp_add_col_if_absent('MAS_AUTO_AGV_TASK', 'DISPATCH_TIME',
  "DISPATCH_TIME DATETIME NULL COMMENT '下发（transitTask/excuteTask）成功时间'");
CALL sp_add_col_if_absent('MAS_AUTO_AGV_TASK', 'REDO_COUNT',
  "REDO_COUNT INT NOT NULL DEFAULT 0 COMMENT 'redo 次数（同 taskId 幂等重发）'");
CALL sp_add_col_if_absent('MAS_AUTO_AGV_TASK', 'CANCEL_MANUAL_FLAG',
  "CANCEL_MANUAL_FLAG CHAR(1) NOT NULL DEFAULT '0' COMMENT '取消后人工处理确认 0=未确认 1=已确认'");
CALL sp_add_col_if_absent('MAS_AUTO_AGV_TASK', 'POSITION_ID',
  "POSITION_ID BIGINT NULL COMMENT '关联加工位（任务由哪个位触发/送往）'");
CALL sp_add_col_if_absent('MAS_AUTO_AGV_TASK', 'ELECTRODE_ID',
  "ELECTRODE_ID VARCHAR(50) NULL COMMENT '关联工件/电极码'");
CALL sp_add_col_if_absent('MAS_AUTO_AGV_TASK', 'TXN_ID',
  "TXN_ID VARCHAR(50) NULL COMMENT '换架事务 ID（关联先拉后送任务对）'");
CALL sp_add_col_if_absent('MAS_AUTO_AGV_TASK', 'REQ_PARAM',
  "REQ_PARAM VARCHAR(1000) NULL COMMENT '下发参数快照（position/param）'");
CALL sp_add_index_if_absent('MAS_AUTO_AGV_TASK', 'idx_agv_rcs_task', 'RCS_TASK_ID');
CALL sp_add_index_if_absent('MAS_AUTO_AGV_TASK', 'idx_agv_task_state', 'TASK_STATE');
CALL sp_add_index_if_absent('MAS_AUTO_AGV_TASK', 'idx_agv_txn', 'TXN_ID');

-- -------------------------------------------------------------
-- 2. 扩展 MAS_AUTO_FRAME_SLOT：绑定来源 + 最近盘点校正时间
-- -------------------------------------------------------------
CALL sp_add_col_if_absent('MAS_AUTO_FRAME_SLOT', 'BIND_SOURCE',
  "BIND_SOURCE VARCHAR(10) NULL COMMENT '电极码绑定来源 MANUAL/SCAN_GUN/RCS_QR'");
CALL sp_add_col_if_absent('MAS_AUTO_FRAME_SLOT', 'LAST_VERIFY_TIME',
  "LAST_VERIFY_TIME DATETIME NULL COMMENT '最近盘点校正时间（电极反查数据新鲜度）'");

-- -------------------------------------------------------------
-- 3. 料架角色语义扩展（CHAR(1) 不变，扩充取值域，仅更新注释）
--    0=上料LOAD 1=下料UNLOAD 2=中转TRANSIT 3=NG架NG_FRAME
-- -------------------------------------------------------------
ALTER TABLE MAS_AUTO_FRAME_BIND
  MODIFY COLUMN FRAME_ROLE CHAR(1) NOT NULL
  COMMENT '角色 0=上料LOAD 1=下料UNLOAD 2=中转TRANSIT 3=NG架NG_FRAME';

-- -------------------------------------------------------------
-- 4. 新增 MAS_AUTO_LOCATION_MAP：逻辑位置 ↔ RCS 点位编码（station/cell 双层）
-- -------------------------------------------------------------
CREATE TABLE IF NOT EXISTS MAS_AUTO_LOCATION_MAP (
  ID1         BIGINT      NOT NULL AUTO_INCREMENT COMMENT '主键',
  LOC_TYPE    VARCHAR(20) NOT NULL COMMENT '逻辑位置类型 EQUIPMENT/POSITION/FRAME/AREA',
  EQUIMENT_ID BIGINT      NULL COMMENT '关联机台（LOC_TYPE=EQUIPMENT/POSITION）',
  POSITION_ID BIGINT      NULL COMMENT '关联加工位（LOC_TYPE=POSITION）',
  FRAME_ID    BIGINT      NULL COMMENT '关联料架（LOC_TYPE=FRAME）',
  LOC_NAME    VARCHAR(50) NULL COMMENT '逻辑位置名称（缓存区/备料区/托盘回收区等命名点）',
  RCS_CODE    VARCHAR(50) NOT NULL COMMENT 'RCS 点位编码 如 101(station) / 601203(cell)',
  RCS_TYPE    VARCHAR(10) NOT NULL COMMENT '点位类型 shelf/cell/station',
  REMARK      VARCHAR(200) NULL,
  STATE       CHAR(1)     NOT NULL DEFAULT '0' COMMENT '0=启用 1=禁用',
  AUTHOR      VARCHAR(15) NULL,
  UPDATETIME  DATETIME    NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  PRIMARY KEY (ID1),
  KEY idx_loc_rcs (RCS_CODE),
  KEY idx_loc_eq (EQUIMENT_ID),
  KEY idx_loc_frame (FRAME_ID)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='逻辑位置↔RCS点位编码映射（station/cell双层）';

-- -------------------------------------------------------------
-- 5. 新增 MAS_AUTO_RCS_MSG_LOG：RCS 双向报文流水
-- -------------------------------------------------------------
CREATE TABLE IF NOT EXISTS MAS_AUTO_RCS_MSG_LOG (
  ID1            BIGINT      NOT NULL AUTO_INCREMENT COMMENT '主键',
  DIRECTION      VARCHAR(10) NOT NULL COMMENT '方向 OUT=出站请求 IN=入站回调',
  INTERFACE_NAME VARCHAR(30) NULL COMMENT '接口名 transitTask/excuteTask/cancelTask/queryTask/pushTaskStatus/scanTaskStatus/warnCallback',
  URL            VARCHAR(200) NULL COMMENT '请求/回调 URL',
  TASK_ID        VARCHAR(50) NULL COMMENT '关联 taskId',
  REQUEST_BODY   TEXT        NULL COMMENT '报文原文（请求/推送）',
  RESPONSE_BODY  TEXT        NULL COMMENT '报文原文（应答）',
  COST_MS        INT         NULL COMMENT '耗时毫秒',
  RESULT         CHAR(1)     NOT NULL DEFAULT '0' COMMENT '0=成功 1=失败',
  ERROR_MSG      VARCHAR(500) NULL,
  CREATE_TIME    DATETIME    NULL DEFAULT CURRENT_TIMESTAMP COMMENT '发生时间',
  PRIMARY KEY (ID1),
  KEY idx_rcsmsg_task (TASK_ID),
  KEY idx_rcsmsg_time (DIRECTION, CREATE_TIME)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='RCS双向报文流水';

-- -------------------------------------------------------------
-- 6. 告警来源补充说明（ALARM_TYPE 为自由文本，新增取值 RCS_WARN，仅更新注释）
-- -------------------------------------------------------------
ALTER TABLE MAS_AUTO_ALARM_EVENT
  MODIFY COLUMN ALARM_TYPE VARCHAR(30) NOT NULL
  COMMENT '告警类型 PLC_OFFLINE/READ_TIMEOUT/UNSAFE/POS_STUCK/FRAME_FULL/RCS_WARN/RCS_TASK_FAIL/PLC_VERIFY_FAIL...';

-- 清理临时存储过程
DROP PROCEDURE IF EXISTS sp_add_col_if_absent;
DROP PROCEDURE IF EXISTS sp_add_index_if_absent;

-- =============================================================
-- 回滚段（如需撤销本迁移，取消注释执行）
-- =============================================================
-- DROP TABLE IF EXISTS MAS_AUTO_RCS_MSG_LOG;
-- DROP TABLE IF EXISTS MAS_AUTO_LOCATION_MAP;
-- ALTER TABLE MAS_AUTO_FRAME_SLOT DROP COLUMN BIND_SOURCE, DROP COLUMN LAST_VERIFY_TIME;
-- ALTER TABLE MAS_AUTO_AGV_TASK
--   DROP COLUMN RCS_TASK_ID, DROP COLUMN RCS_KIND, DROP COLUMN RCS_STATUS, DROP COLUMN TASK_STATE,
--   DROP COLUMN PRIORITY, DROP COLUMN DISPATCH_TIME, DROP COLUMN REDO_COUNT, DROP COLUMN CANCEL_MANUAL_FLAG,
--   DROP COLUMN POSITION_ID, DROP COLUMN ELECTRODE_ID, DROP COLUMN TXN_ID, DROP COLUMN REQ_PARAM;
