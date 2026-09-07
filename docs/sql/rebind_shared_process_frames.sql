-- 演示/联调库：把 FRAME_BIND 改成一架两用。只动绑定，不清槽位。
-- 禁止在现场生产库执行全量 cnc_schema.sql；本脚本同样只用于测试库或已备份的联调库。
--
-- 101 = 内长宽上料
-- 301 = 内长宽下料 = 平面度上料
-- 302 = 平面度下料 = A基准上料
-- 303 = A基准下料
-- 401 = 三机 NG

UPDATE MAS_AUTO_FRAME SET FRAME_NAME = '工序架301' WHERE ID1 = 2 AND FRAME_CODE = '301';
UPDATE MAS_AUTO_FRAME SET FRAME_NAME = '工序架302' WHERE ID1 = 92 AND FRAME_CODE = '302';
UPDATE MAS_AUTO_LOCATION_MAP SET LOC_NAME = '工序架301' WHERE FRAME_ID = 2 AND RCS_TYPE = 'shelf';
UPDATE MAS_AUTO_LOCATION_MAP SET LOC_NAME = '工序架302' WHERE FRAME_ID = 92 AND RCS_TYPE = 'shelf';

DELETE FROM MAS_AUTO_FRAME_BIND
WHERE EQUIMENT_ID IN (1, 2, 3)
  AND FRAME_ID IN (1, 2, 3, 91, 92);

INSERT INTO MAS_AUTO_FRAME_BIND
  (FRAME_ID, EQUIMENT_ID, FRAME_ROLE, STATE, AUTHOR) VALUES
  (1,  1, '0', '0', 'system'),
  (2,  1, '1', '0', 'system'),
  (91, 1, '3', '0', 'system'),
  (2,  2, '0', '0', 'system'),
  (92, 2, '1', '0', 'system'),
  (91, 2, '3', '0', 'system'),
  (92, 3, '0', '0', 'system'),
  (3,  3, '1', '0', 'system'),
  (91, 3, '3', '0', 'system');
