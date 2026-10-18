using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace XTrader.Shell.Views;

/// <summary>
/// 流式布局：子元素保持自然宽度从左到右排列，放不下时换行。
///
/// WinUI 只有 GridView 用的 ItemsWrapGrid，不适用于「品种/策略胶囊」这类
/// 非虚拟化的静态集合；这里用一个最小 Panel 实现等价的换行逻辑。
/// </summary>
public sealed class WrapPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(WrapPanel),
        new PropertyMetadata(10.0, OnLayoutPropertyChanged));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((WrapPanel)d).InvalidateMeasure();

    protected override Size MeasureOverride(Size availableSize)
    {
        var maxWidth = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;
        double x = 0, y = 0, rowHeight = 0, widest = 0;

        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (x > 0 && x + Spacing + size.Width > maxWidth)
            {
                widest = Math.Max(widest, x);
                y += rowHeight + Spacing;
                x = 0;
                rowHeight = 0;
            }
            x += (x > 0 ? Spacing : 0) + size.Width;
            rowHeight = Math.Max(rowHeight, size.Height);
        }

        widest = Math.Max(widest, x);
        var height = y + rowHeight;
        return new Size(double.IsInfinity(availableSize.Width) ? widest : Math.Min(widest, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var maxWidth = double.IsInfinity(finalSize.Width) ? double.MaxValue : finalSize.Width;
        double x = 0, y = 0, rowHeight = 0;

        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + Spacing + size.Width > maxWidth)
            {
                y += rowHeight + Spacing;
                x = 0;
                rowHeight = 0;
            }
            x += x > 0 ? Spacing : 0;
            child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width;
            rowHeight = Math.Max(rowHeight, size.Height);
        }
        return finalSize;
    }

    /// <summary>
    /// 取胶囊按钮样式。样式定义在 App.xaml；查不到时返回 null 由调用方退回默认
    /// Button 样式，而不是在界面构建时抛异常。
    /// </summary>
    internal static Style? CapsuleStyle() =>
        Application.Current?.Resources.TryGetValue("CapsuleButtonStyle", out var style) == true
            ? style as Style
            : null;
}
