namespace CncLoader.Core.State;

/// <summary>
/// PLC 写钩子（第四阶段⑤模拟用）：调度器写 POS_TEST_START 成功后回调，
/// 供 CNC 机台行为模拟器驱动"检测→OK"语义。生产环境不注册实现，调度器获 null 即跳过。
/// </summary>
public interface IPlcWriteHook
{
    /// <summary>POS_TEST_START 写入并复核通过后回调。value=1 启动检测 / 2 复位。</summary>
    void OnTestStartWritten(long equipmentId, long positionId, int value);
}
