using System.Windows;
using CncLoader.UI.ViewModels.Pages;

namespace CncLoader.Core.Tests.UI;

/// <summary>
/// 主题资源装载验证（STA）：按 App.xaml 的顺序真实合并全部字典，确认
/// ①每个页面 surface 的 DataTemplate 都能被解析到；②共享转换器/样式键齐全。
/// PageTemplates 拆成 per-surface 字典后，合并顺序错了只会在运行时炸，编译期发现不了，
/// 所以这条装载测试是拆分的安全网。
/// </summary>
[TestFixture]
[Apartment(ApartmentState.STA)]
public sealed class ThemeResourceLoadTests
{
    private static readonly Type[] PageViewModels =
    {
        typeof(DashboardViewModel),
        typeof(WorkLineViewModel),
        typeof(CraftworkViewModel),
        typeof(EquipmentViewModel),
        typeof(PlcViewModel),
        typeof(PointMappingViewModel),
        typeof(RcsViewModel),
        typeof(AgvViewModel),
        typeof(ScanViewModel),
        typeof(FrameViewModel),
        typeof(LogViewModel)
    };

    private ResourceDictionary _merged = null!;

    [OneTimeSetUp]
    public void LoadTheme()
    {
        // pack://application 需要 Application 资源容器已登记；只建实例不 Run（不起消息循环、不开窗口）
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        if (Application.Current is null) _ = new Application();

        // 必须挂到 Application.Current.Resources：模板内部的 StaticResource 只解析到这一层，
        // 用游离的 ResourceDictionary 测会连 Styles.xaml 的 Panel 都找不到，得出错误结论。
        _merged = Application.Current!.Resources;
        foreach (var uri in new[]
                 {
                     "pack://application:,,,/HandyControl;component/Themes/SkinDark.xaml",
                     "pack://application:,,,/HandyControl;component/Themes/Theme.xaml",
                     "pack://application:,,,/CncLoader.UI;component/Theme/Tokens.xaml",
                     "pack://application:,,,/CncLoader.UI;component/Theme/Styles.xaml",
                     "pack://application:,,,/CncLoader.UI;component/Theme/DashboardStyles.xaml",
                     "pack://application:,,,/CncLoader.UI;component/Theme/SharedResources.xaml",
                     "pack://application:,,,/CncLoader.UI;component/Theme/PageTemplates.xaml"
                 })
        {
            _merged.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(uri) });
        }
    }

    [Test]
    public void EveryPageSurface_HasResolvableDataTemplate()
    {
        Assert.Multiple(() =>
        {
            foreach (var vm in PageViewModels)
            {
                var template = _merged[new DataTemplateKey(vm)] as DataTemplate;
                Assert.That(template, Is.Not.Null,
                    $"{vm.Name} 缺少隐式 DataTemplate——页面会退化为显示类型名");
            }
        });
    }

    /// <summary>
    /// 真正实例化模板内容。取到 DataTemplate 对象只证明键在，不会展开模板里的
    /// StaticResource——转换器丢了要等到界面渲染那一刻才炸。这里提前把它炸出来。
    /// </summary>
    [Test]
    public void EveryPageTemplate_CanMaterializeItsContent()
    {
        var offenders = new List<string>();

        foreach (var vm in PageViewModels)
        {
            var template = (DataTemplate)_merged[new DataTemplateKey(vm)];
            try
            {
                template.LoadContent();
            }
            catch (Exception ex)
            {
                var root = ex;
                while (root.InnerException is not null) root = root.InnerException;
                offenders.Add($"{vm.Name}: {root.Message}");
            }
        }

        Assert.That(offenders, Is.Empty,
            "模板内容展开失败（多为 StaticResource 不在可见层级）：\n" + string.Join('\n', offenders));
    }

    [TestCase("PlaceholderTemplate")]
    [TestCase("EnabledBadge")]
    public void SharedKeyedTemplates_CanMaterializeItsContent(string key)
    {
        var template = (DataTemplate)_merged[key];
        Assert.That(() => template.LoadContent(), Throws.Nothing);
    }

    [TestCase("PlaceholderTemplate")]
    [TestCase("EnabledBadge")]
    [TestCase("CalibrationTicks")]
    [TestCase("BrushConv")]
    [TestCase("StateBadgeBrushConv")]
    [TestCase("DashStateBadgeSoftBrushConv")]
    [TestCase("PrettyJsonConv")]
    public void SharedResourceKeys_AreResolvable(string key)
    {
        Assert.That(_merged.Contains(key), Is.True, $"共享资源键 {key} 未解析到");
    }

    [Test]
    public void PageTemplatesIsAggregatorOnly_NotAMonolith()
    {
        var aggregator = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/CncLoader.UI;component/Theme/PageTemplates.xaml")
        };

        Assert.Multiple(() =>
        {
            Assert.That(aggregator.Count, Is.Zero,
                "PageTemplates 只应聚合子字典，自身不再直接定义资源");
            Assert.That(aggregator.MergedDictionaries, Has.Count.EqualTo(PageViewModels.Length),
                "每个页面 surface 一个字典；共享字典由 App.xaml 同层合并，不得嵌进来");
        });
    }
}
