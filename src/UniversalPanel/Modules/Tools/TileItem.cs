namespace UniversalPanel.Modules.Tools;

/// <summary>
/// One tile on screen. With sections on, a tool in two categories is two tiles (two TileItems
/// for the same <see cref="Tool"/>), so the list never holds the same object twice: WPF's
/// selection and containers misbehave with duplicate items.
/// </summary>
internal sealed record TileItem(ToolRow Tool, string? Section);
