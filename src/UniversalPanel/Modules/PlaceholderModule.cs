using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using TextBlock = Wpf.Ui.Controls.TextBlock;

namespace UniversalPanel.Modules;

/// <summary>Stand-in until the real module is implemented.</summary>
internal sealed class PlaceholderModule(string id, string title, SymbolRegular icon, string description) : IPanelModule
{
    public string Id => id;
    public string Title => title;
    public SymbolRegular Icon => icon;

    public FrameworkElement CreateView() => new StackPanel
    {
        Margin = new Thickness(0, 8, 0, 0),
        Children =
        {
            new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, FontTypography = FontTypography.Body },
            new TextBlock
            {
                Text = "Not implemented yet.",
                Margin = new Thickness(0, 12, 0, 0),
                FontTypography = FontTypography.Caption,
                Appearance = TextColor.Secondary,
            },
        },
    };

    public void OnActivated() { }

    public void OnDeactivated() { }

    public static IReadOnlyList<IPanelModule> Defaults { get; } =
    [
        new PlaceholderModule("processes", "Processes", SymbolRegular.Pulse24,
            "Live process list with kill, suspend/resume, priority, modules, handles and threads."),
        new PlaceholderModule("launcher", "Tools", SymbolRegular.Apps24,
            "One-click launcher for IDA, DIE, Ghidra, HxD, Postman and other tools, with their real icons."),
        new PlaceholderModule("python", "Python", SymbolRegular.Code24,
            "Interactive Python console and script runner, out-of-process."),
        new PlaceholderModule("network", "Network", SymbolRegular.Globe24,
            "TCP/UDP connections per process, and per-process bandwidth."),
    ];
}
