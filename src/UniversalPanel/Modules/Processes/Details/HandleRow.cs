using System.Globalization;
using UniversalPanel.Native;

namespace UniversalPanel.Modules.Processes.Details;

/// <summary>One handle. Immutable: the table is reloaded, not updated in place.</summary>
internal sealed class HandleRow(HandleRecord handle)
{
    private const uint OBJ_PROTECT_CLOSE = 0x1;
    private const uint OBJ_INHERIT = 0x2;

    public ulong Value => handle.Handle;
    public string Type => handle.Type;
    public string Name => handle.Name;
    public bool IsNamed => handle.Name.Length > 0;
    public string ValueText { get; } = "0x" + handle.Handle.ToString("X", CultureInfo.InvariantCulture);

    public string Details
    {
        get
        {
            var flags = new List<string>(2);
            if ((handle.Attributes & OBJ_INHERIT) != 0)
            {
                flags.Add("inherit");
            }

            if ((handle.Attributes & OBJ_PROTECT_CLOSE) != 0)
            {
                flags.Add("protect from close");
            }

            return $"{(IsNamed ? Name : "(unnamed)")}\n{Type} · handle {ValueText} · access 0x{handle.GrantedAccess:X8}" +
                (flags.Count > 0 ? $" · {string.Join(", ", flags)}" : string.Empty);
        }
    }
}
