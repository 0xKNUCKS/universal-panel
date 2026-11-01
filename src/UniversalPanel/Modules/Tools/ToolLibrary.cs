using UniversalPanel.Shell;

namespace UniversalPanel.Modules.Tools;

/// <summary>
/// The user's tools, shared by the Tools tab (which edits them) and "Open with" in Processes
/// (which runs their actions). UI thread only.
/// </summary>
internal static class ToolLibrary
{
    private static List<ToolRow>? _rows;

    public static List<ToolRow> Rows => _rows ??= ToolStore.Load().Select(e => new ToolRow(e)).ToList();

    /// <summary>Raised after the list or a tool's settings changed (and were saved).</summary>
    public static event Action? Changed;

    public static void Save()
    {
        ToolStore.Save(Rows.Select(r => r.Entry.Clone()).ToList());
        Changed?.Invoke();
    }

    /// <summary>Reads identity and icon for tools not inspected yet (signature checks run off the UI thread).</summary>
    public static async Task EnsureInspectedAsync()
    {
        var pending = Rows.Where(r => !r.Inspected).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        var results = await Task.Run(() => pending.Select(r => r.Inspect()).ToList());
        for (int i = 0; i < pending.Count; i++)
        {
            pending[i].Apply(results[i]);
        }
    }
}
