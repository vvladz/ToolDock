using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ToolDock.Common;

public static class CurrentUserPipe
{
    public static NamedPipeServerStream CreateServer(string pipeName)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User
            ?? throw new InvalidOperationException("The current Windows user has no SID.");
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        // Some Windows launch paths give the process a group owner SID. Keep that owner able to
        // connect while the explicit user SID remains the primary access boundary.
        if (identity.Owner is { } owner && owner != user)
        {
            security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            security,
            HandleInheritability.None,
            (PipeAccessRights)0);
    }
}
