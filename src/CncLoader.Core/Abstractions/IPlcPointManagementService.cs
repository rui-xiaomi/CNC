using CncLoader.Core.Plc;

namespace CncLoader.Core.Abstractions;

/// <summary>点位映射维护（MAS_AUTO_PLC_POINT）。</summary>
public interface IPlcPointManagementService
{
    Task<IReadOnlyList<PlcPointRow>> GetByPlcAsync(long plcId, CancellationToken ct = default);
    Task SavePointAsync(PlcPointRow row, string author, CancellationToken ct = default);
    Task DeletePointAsync(long pointId, string author, CancellationToken ct = default);
    /// <summary>按测试机信号表模板批量导入某机台 12 读 + 2 写点位（已存在则跳过）。</summary>
    Task<int> ImportSignalTableAsync(long equipmentId, long plcId, string author, CancellationToken ct = default);
    Task<IReadOnlyList<WriteSignalOption>> GetWriteSignalsAsync(long? plcId = null, CancellationToken ct = default);
}
