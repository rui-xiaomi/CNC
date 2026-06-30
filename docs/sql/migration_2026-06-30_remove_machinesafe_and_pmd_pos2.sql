-- =============================================================
-- 迁移：取消 内长宽/平面度「机台安全」信号 + 平面度「工位2」
-- 日期：2026-06-30（已与现场确认）
-- 目标库：cnc_auto（对【已建好的现有库】执行；新建库请直接用 cnc_schema.sql）
-- 用法：  mysql -u root -p cnc_auto < migration_2026-06-30_remove_machinesafe_and_pmd_pos2.sql
--
-- 策略：软删（STATE='1'），运行时按 STATE='0' 过滤，不物理删除，可回滚。
-- 以 EQUIMENT_NO / POSITION_CODE 等业务键定位，避免依赖自增主键 ID。
-- 幂等：带 STATE='0' 条件，重复执行不会重复影响。
-- =============================================================

START TRANSACTION;

-- 1) 取消 内长宽(EQ01)、平面度(EQ02) 的「机台安全」点位
UPDATE MAS_AUTO_PLC_POINT p
JOIN MAS_AUTO_WORKLINE_EQUIMENT e ON e.ID1 = p.EQUIMENT_ID
SET p.STATE = '1'
WHERE p.SIGNAL_KEY = 'MACHINE_SAFE'
  AND e.EQUIMENT_NO IN ('EQ01', 'EQ02')
  AND p.STATE = '0';

-- 2) 取消 平面度(EQ02) 工位2：先软删其全部点位（外键依赖），再软删工位本身
UPDATE MAS_AUTO_PLC_POINT p
JOIN MAS_AUTO_EQUIMENT_POSITION pos ON pos.ID1 = p.POSITION_ID
SET p.STATE = '1'
WHERE pos.POSITION_CODE = 'EQ02-P2'
  AND p.STATE = '0';

UPDATE MAS_AUTO_EQUIMENT_POSITION
SET STATE = '1', AUTHOR = 'system'
WHERE POSITION_CODE = 'EQ02-P2'
  AND STATE = '0';

COMMIT;

-- 核对（执行后应：MACHINE_SAFE 仅剩 A基准 1 条；平面度工位2 与其点位均 STATE='1'）
-- SELECT e.EQUIMENT_NO, p.SIGNAL_KEY, p.REGISTER_ADDR, p.STATE
--   FROM MAS_AUTO_PLC_POINT p JOIN MAS_AUTO_WORKLINE_EQUIMENT e ON e.ID1 = p.EQUIMENT_ID
--   WHERE p.SIGNAL_KEY = 'MACHINE_SAFE';
-- SELECT POSITION_CODE, STATE FROM MAS_AUTO_EQUIMENT_POSITION WHERE POSITION_CODE = 'EQ02-P2';

-- =============================================================
-- 回滚（如现场又要恢复）：把上述行 STATE 改回 '0'
-- =============================================================
-- UPDATE MAS_AUTO_PLC_POINT p JOIN MAS_AUTO_WORKLINE_EQUIMENT e ON e.ID1 = p.EQUIMENT_ID
--   SET p.STATE='0' WHERE p.SIGNAL_KEY='MACHINE_SAFE' AND e.EQUIMENT_NO IN ('EQ01','EQ02') AND p.STATE='1';
-- UPDATE MAS_AUTO_PLC_POINT p JOIN MAS_AUTO_EQUIMENT_POSITION pos ON pos.ID1 = p.POSITION_ID
--   SET p.STATE='0' WHERE pos.POSITION_CODE='EQ02-P2' AND p.STATE='1';
-- UPDATE MAS_AUTO_EQUIMENT_POSITION SET STATE='0' WHERE POSITION_CODE='EQ02-P2' AND STATE='1';
