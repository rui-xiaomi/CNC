-- =============================================================================
-- 演示用 LOCATION_MAP 种子（配合内置模拟器跑「完整上下料闭环」）
-- 幂等：重复执行安全（先清 AUTHOR='demo' 的旧行再插）。
-- 前置：已执行 docs/sql/cnc_schema.sql 建库（含 3 机台 / 5 加工位 / 3 料架种子）。
-- 用法：
--   & "C:\Program Files\MySQL\MySQL Server 8.4\bin\mysql.exe" -u root -p cnc_auto < docs\sql\demo_seed_location_map.sql
-- 录完需重启 App（调度器启动时装载路由缓存）。
--
-- RCS_CODE 为演示编码，模拟器不校验真伪；接真机时改为现场真实点位编码。
-- 匹配规则（来自代码）：
--   上/下料区 : LOC_TYPE='AREA' AND LOC_NAME=('LOAD_AREA'|'UNLOAD_AREA')
--   加工位cell: RCS_TYPE='cell' AND EQUIMENT_ID AND POSITION_ID
--   料架站点  : RCS_TYPE='shelf' AND FRAME_ID
--   区域(换架/回收): LOC_TYPE='AREA' AND LOC_NAME=('FULL_BUFFER'|'EMPTY_BUFFER'|'PALLET_RETURN')
-- =============================================================================

USE cnc_auto;

-- 清理旧的演示行（幂等）
DELETE FROM MAS_AUTO_LOCATION_MAP WHERE AUTHOR = 'demo';

-- ---- 命名区域（上料区 / 下料区）----
INSERT INTO MAS_AUTO_LOCATION_MAP
  (LOC_TYPE, LOC_NAME, RCS_CODE, RCS_TYPE, STATE, AUTHOR) VALUES
  ('AREA', 'LOAD_AREA',   '601001', 'station', '0', 'demo'),
  ('AREA', 'UNLOAD_AREA', '609001', 'station', '0', 'demo');

-- ---- 换架 / 空托盘回收 区域（§5 进阶演示用）----
INSERT INTO MAS_AUTO_LOCATION_MAP
  (LOC_TYPE, LOC_NAME, RCS_CODE, RCS_TYPE, STATE, AUTHOR) VALUES
  ('AREA', 'FULL_BUFFER',   '650001', 'station', '0', 'demo'),
  ('AREA', 'EMPTY_BUFFER',  '650002', 'station', '0', 'demo'),
  ('AREA', 'PALLET_RETURN', '650003', 'station', '0', 'demo');

-- ---- 加工位 cell（全部 5 个种子加工位）----
-- EQ01 内长宽(eq=1): 工位1=pos1 / 工位2=pos2
-- EQ02 平面度(eq=2): 工位1=pos3
-- EQ03 A基准 (eq=3): 工位1=pos5 / 工位2=pos6
INSERT INTO MAS_AUTO_LOCATION_MAP
  (LOC_TYPE, EQUIMENT_ID, POSITION_ID, RCS_CODE, RCS_TYPE, STATE, AUTHOR) VALUES
  ('POSITION', 1, 1, '601203', 'cell', '0', 'demo'),
  ('POSITION', 1, 2, '601204', 'cell', '0', 'demo'),
  ('POSITION', 2, 3, '602203', 'cell', '0', 'demo'),
  ('POSITION', 3, 5, '603203', 'cell', '0', 'demo'),
  ('POSITION', 3, 6, '603204', 'cell', '0', 'demo');

-- ---- 料架站点 shelf（3 个种子料架；换架演示用）----
INSERT INTO MAS_AUTO_LOCATION_MAP
  (LOC_TYPE, FRAME_ID, LOC_NAME, RCS_CODE, RCS_TYPE, STATE, AUTHOR) VALUES
  ('FRAME', 1, '上料总架', '651001', 'shelf', '0', 'demo'),
  ('FRAME', 2, '中转架',   '651002', 'shelf', '0', 'demo'),
  ('FRAME', 3, '下料总架', '651003', 'shelf', '0', 'demo');

-- 校验
SELECT LOC_TYPE, LOC_NAME, EQUIMENT_ID, POSITION_ID, FRAME_ID, RCS_CODE, RCS_TYPE
FROM MAS_AUTO_LOCATION_MAP WHERE AUTHOR = 'demo'
ORDER BY LOC_TYPE, RCS_CODE;
