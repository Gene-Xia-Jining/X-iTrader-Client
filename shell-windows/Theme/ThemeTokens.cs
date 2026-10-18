using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace XTrader.Shell.Theme;

/// <summary>
/// 设计令牌（对应 mac 壳的 Theme.swift / Qt 的 theme.py：Apple 风格明暗两套）。
///
/// 这里是 C# 侧的单一来源：视图通过 <see cref="Palette"/> 按当前深浅色取色。
/// 值与 Swift 侧逐项对齐，保证两端观感一致。
/// </summary>
public sealed class Palette
{
    public required Color Canvas { get; init; }
    public required Color Chrome { get; init; }
    public required Color Card { get; init; }
    public required Color Hairline { get; init; }
    public required Color Divider { get; init; }
    public required Color Ink { get; init; }
    public required Color Dim { get; init; }
    public required Color Primary { get; init; }
    public required Color Danger { get; init; }
    public required Color Success { get; init; }
    public required Color InputBg { get; init; }
    public required Color SidebarHover { get; init; }
    public required Color SidebarSelected { get; init; }
    public required Color LogBg { get; init; }
    public required Color Soft { get; init; }
    public required Color Warning { get; init; }

    public SolidColorBrush Brush(Color color) => new(color);
}

internal static class ThemeTokens
{
    private static Color Hex(uint value) => Color.FromArgb(
        0xFF,
        (byte)((value >> 16) & 0xFF),
        (byte)((value >> 8) & 0xFF),
        (byte)(value & 0xFF));

    private static readonly Palette Light = new()
    {
        Canvas = Hex(0xF5F5F7),
        Chrome = Hex(0xFFFFFF),
        Card = Hex(0xFFFFFF),
        Hairline = Hex(0xE0E0E0),
        Divider = Hex(0xF0F0F0),
        Ink = Hex(0x1D1D1F),
        Dim = Hex(0x7A7A7A),
        Primary = Hex(0x0066CC),
        Danger = Hex(0xE3342F),
        Success = Hex(0x34C759),
        InputBg = Hex(0xFFFFFF),
        SidebarHover = Hex(0xF0F0F0),
        SidebarSelected = Hex(0xE0E0E0),
        LogBg = Hex(0xFAFAFC),
        Soft = Hex(0xFAFAFC),
        Warning = Hex(0xB8780A),
    };

    private static readonly Palette Dark = new()
    {
        Canvas = Hex(0x272729),
        Chrome = Hex(0x252527),
        Card = Hex(0x272729),
        Hairline = Hex(0x3A3A3C),
        Divider = Hex(0x3A3A3C),
        Ink = Microsoft.UI.Colors.White,
        Dim = Hex(0x86868B),
        Primary = Hex(0x2997FF),
        Danger = Hex(0xFF453A),
        Success = Hex(0x30D158),
        InputBg = Hex(0x2A2A2C),
        SidebarHover = Hex(0x303032),
        SidebarSelected = Hex(0x48484A),
        LogBg = Hex(0x252527),
        Soft = Hex(0x2A2A2C),
        Warning = Hex(0xFFD60A),
    };

    /// <summary>固定语义色（不随主题）</summary>
    internal static Color StatusGreen { get; } = Hex(0x2ECC71);
    internal static Color StatusRed { get; } = Hex(0xE74C3C);
    internal static Color Link { get; } = Hex(0x007AFF);

    internal static Palette For(bool dark) => dark ? Dark : Light;
}
