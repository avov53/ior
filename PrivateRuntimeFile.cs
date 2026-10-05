using System.Security.AccessControl;
using System.Security.Principal;

namespace HappBox;

internal static class PrivateRuntimeFile
{
    public static void Write(string path, string content)
    {
        // Apply user-only access before any transient bridge credentials are written.
        using (File.Open(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None)) { }
        var identity = WindowsIdentity.GetCurrent().User ?? throw new IOException("RUNTIME_FILE_IDENTITY_UNAVAILABLE");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(identity);
        security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
        File.WriteAllText(path, content);
    }
}
