namespace CncLoader.Communication.Simulation;

/// <summary>
/// 进程内 PLC 模拟器的寄存器直读写抽象（Modbus / FINS 均实现）。
/// 供 <see cref="CncMachineSimulator"/> 叠加加工位节拍语义时，不依赖具体协议即可写入寄存器——
/// 写入只对已登记该 PLC 的模拟器生效（另一协议模拟器 no-op），因此可安全地向所有模拟器广播写入。
/// </summary>
public interface ISimulatorRegisterStore
{
    /// <summary>写一个寄存器（未登记该 PLC 时静默 no-op）。offset 为 <c>RegisterAddress.ToRegisterIndex</c> 口径。</summary>
    void WriteRegister(long plcId, int offset, ushort value);
}
