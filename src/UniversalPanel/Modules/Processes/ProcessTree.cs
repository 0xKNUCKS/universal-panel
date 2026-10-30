// Parent linking mirrors System Informer (MIT), SystemInformer/proctree.c PhpValidateParentProcessNode:
// a process is nested under the process with its parent PID only if that process was created no
// later than the child. Otherwise the parent PID was reused by a newer, unrelated process and the
// child becomes a root. Default sibling order is creation order (System Informer's "no sort").
// https://github.com/winsiderss/systeminformer @ c2f1489890f14de76b985c0142cbad154d92623d

using UniversalPanel.Native;

namespace UniversalPanel.Modules.Processes;

internal static class ProcessTree
{
    /// <summary>
    /// Idle (0) and System (4) never exit, so their PIDs are never reused and the create-time
    /// check isn't needed. It would also be wrong for them: Windows reports Secure System and
    /// Registry as created seconds before System, their actual parent.
    /// </summary>
    private static bool IsPermanent(ProcessRow row) => row.Pid is 0 or 4;

    /// <summary>
    /// Assigns Depth, HasChildren, TreeOrder and IsHiddenByCollapse on every row.
    /// <paramref name="compareSiblings"/> orders children of the same parent (and the roots).
    /// </summary>
    public static void Layout(IReadOnlyCollection<ProcessRow> rows, IReadOnlySet<ProcessKey> collapsed, Comparison<ProcessRow> compareSiblings)
    {
        // PID -> row. Several rows can briefly share a PID (an exited process still listed next to
        // its successor); prefer the newest, since a live child's parent is the current owner.
        var byPid = new Dictionary<int, ProcessRow>(rows.Count);
        foreach (var row in rows)
        {
            if (!byPid.TryGetValue(row.Pid, out var existing) || existing.Key.CreateTime < row.Key.CreateTime)
            {
                byPid[row.Pid] = row;
            }
        }

        var children = new Dictionary<ProcessKey, List<ProcessRow>>();
        var roots = new List<ProcessRow>();
        foreach (var row in rows)
        {
            if (byPid.TryGetValue(row.ParentPid, out var parent)
                && parent.Key != row.Key
                && (IsPermanent(parent) || parent.Key.CreateTime <= row.Key.CreateTime))
            {
                if (!children.TryGetValue(parent.Key, out var list))
                {
                    children[parent.Key] = list = [];
                }

                list.Add(row);
            }
            else
            {
                roots.Add(row);
            }
        }

        int order = 0;
        var visited = new HashSet<ProcessKey>(rows.Count);

        void Visit(ProcessRow row, int depth, bool hidden)
        {
            if (!visited.Add(row.Key))
            {
                return; // Defensive: equal create times (boot-time processes) could otherwise loop.
            }

            bool hasChildren = children.TryGetValue(row.Key, out var kids);
            row.SetTree(depth, hasChildren, order++, hidden);

            if (kids is null)
            {
                return;
            }

            kids.Sort(compareSiblings);
            bool hideKids = hidden || collapsed.Contains(row.Key);
            foreach (var kid in kids)
            {
                Visit(kid, depth + 1, hideKids);
            }
        }

        roots.Sort(compareSiblings);
        foreach (var root in roots)
        {
            Visit(root, 0, hidden: false);
        }

        // Anything unreachable from a root (only possible in a parent cycle) is shown as a root.
        foreach (var row in rows)
        {
            if (!visited.Contains(row.Key))
            {
                Visit(row, 0, hidden: false);
            }
        }
    }
}
