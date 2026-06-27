using CncLoader.Core.Plc;

namespace CncLoader.Core.Abstractions;

/// <summary>PLC 读写操作（写操作先落流水再下发）。</summary>
public interface IPlcOperationService
{
    Task<IReadOnlyList<PlcReadResult>> ReadPointsAsync(long plcId, bool readOnlySignals = true, CancellationToken ct = default);
    Task<PlcReadResult> ReadRegisterAsync(long plcId, string registerAddress, int length, CancellationToken ct = default);
    /// <summary>写寄存器：先落库流水 → 下发 → 回读校验。</summary>
    Task<PlcWriteResult> WriteWithConfirmAsync(long plcId, string registerAddress, int value, string author, CancellationToken ct = default);
    Task<PlcWriteResult> VerifyWriteAsync(long plcId, string registerAddress, int expectedValue, CancellationToken ct = default);
}
