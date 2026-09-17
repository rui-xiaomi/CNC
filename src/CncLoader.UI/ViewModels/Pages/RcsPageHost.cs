using System.Collections.ObjectModel;
using CncLoader.Common.Configuration;
using CncLoader.Common.Identity;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;

namespace CncLoader.UI.ViewModels.Pages;

/// <summary>UI 调度与通知。集合写入必须经 <see cref="IUiDispatcher"/>。</summary>
internal interface IRcsUiBridge
{
    IUserNotificationService Notify { get; }
    IUiDispatcher Ui { get; }
    void Append(string line);
    void ReportResult(RcsResult r);
    void ReplaceOnUi<T>(ObservableCollection<T> target, IReadOnlyList<T> items);
    string StatusMessage { get; set; }
    bool IsBusy { get; set; }
}

/// <summary>会话上下文与派工门禁。不含各 Tab 表单字段。</summary>
internal interface IRcsSessionServices
{
    IRcsTaskService Rcs { get; }
    ILocationMapService LocationMap { get; }
    IEquipmentConfigService Equipment { get; }
    IFrameService Frames { get; }
    IChangeFrameOrchestrator ChangeFrame { get; }
    IRcsConnectionConfigService ConnConfig { get; }
    IRcsRuntimeConfig Runtime { get; }
    IRcsCallbackListener CallbackListener { get; }
    IPositionScheduler Scheduler { get; }
    ICurrentUser User { get; }
    IManagedDispatchRouteResolver RouteResolver { get; }
    IRoutingAvailabilityValidator RoutingValidator { get; }
    RcsOptions Options { get; }

    long WorkLineId { get; }
    long AgvId { get; set; }
    long ConnectionConfigId { get; set; }
    string LineCode { get; }
    string? VerifiedConnectionKey { get; set; }
    string LiveConnectionKey { get; }
    Dictionary<long, string> FrameCodes { get; }

    void RefreshDispatchGateHint();
    void InvalidateConnectionVerification(string reason);
    void LoadConnectionFromRuntime();
    bool ConfirmDangerousRcs(string title, string detail);
    bool EnsureManualDispatchAllowed(string action);
    string ConnectionKey(string? baseUrl, string? clientCode);
    Task RefreshTasksAsync();
    Task RefreshMessagesAsync();
    Task RefreshLocationsAsync();
    Task RefreshChangeFrameTransactionsAsync();
}

/// <summary>页面协调器：UI 桥 + 会话门禁。子 VM 只依赖本接缝。</summary>
internal interface IRcsPageCoordinator : IRcsUiBridge, IRcsSessionServices;
