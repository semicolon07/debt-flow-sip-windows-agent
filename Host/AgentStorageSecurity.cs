using System.Security.AccessControl;
using System.Security.Principal;

namespace DebtFlow.SipAgent.Host;

public static class AgentStorageSecurity
{
    public static void EnsureRestricted(string rootDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(rootDirectory);
        SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("current_user_sid_unavailable");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            system,
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(rootDirectory).SetAccessControl(security);
    }
}
