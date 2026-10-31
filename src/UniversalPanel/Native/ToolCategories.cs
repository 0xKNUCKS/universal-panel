namespace UniversalPanel.Native;

/// <summary>Tool sections, in display order. Stored by name in settings.</summary>
internal static class ToolCategories
{
    public const string Reversing = "Reverse engineering";
    public const string Debugging = "Debugging";
    public const string Binary = "Binary analysis";
    public const string DotNet = ".NET";
    public const string System = "System";
    public const string Network = "Network";
    public const string Other = "Other";

    public static IReadOnlyList<string> All { get; } = [Reversing, Debugging, Binary, DotNet, System, Network, Other];

    /// <summary>Position for sorting; unknown names (from an edited settings file) go last.</summary>
    public static int Order(string category)
    {
        for (int i = 0; i < All.Count; i++)
        {
            if (All[i] == category)
            {
                return i;
            }
        }

        return All.Count;
    }
}
