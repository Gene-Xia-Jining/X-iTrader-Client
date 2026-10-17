using Microsoft.UI.Xaml.Navigation;

namespace XTrader.Shell.Views;

/// <summary>
/// 占位页：Token 管理 / 模拟交易 / 实盘交易 在骨架阶段复用本页。
///
/// 这三页的完整实现（申请记录表、五步引导、账户表单）需在后续批次补齐；
/// 本页仅保证导航可用并说明当前状态，避免点到空白页。
/// </summary>
public sealed partial class PlaceholderPage : ThemedPageBase
{
    private string _tag = "";

    public PlaceholderPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is PageContext context)
        {
            _tag = context.Tag;
        }
        ApplyPalette();
    }

    protected override void ApplyPalette()
    {
        var (title, body) = _tag switch
        {
            "token" => ("Token 管理", "Token 申请与审批状态管理。\n\n当前版本尚未实现该页面，将在后续批次补齐。"),
            "sim" => ("模拟交易", "模拟账户配置：快期注册、交易品种与策略选择。\n\n当前版本尚未实现该页面，将在后续批次补齐。"),
            "live" => ("实盘交易", "实盘账户配置：期货公司、资金账号与交易品种。\n\n当前版本尚未实现该页面，将在后续批次补齐。"),
            _ => ("功能开发中", "该页面将在后续批次补齐。"),
        };
        PageTitle.Text = title;
        Body.Text = body;
        PageTitle.Foreground = Brush(Palette.Ink);
        Body.Foreground = Brush(Palette.Dim);
    }
}
