namespace CncLoader.UI.Navigation;

/// <summary>从看板等页跳到 RCS 任务页并定位指定任务号。</summary>
public interface IRcsTaskNavigator
{
    void OpenTask(string taskId);
}
