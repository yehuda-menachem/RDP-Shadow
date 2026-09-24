using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RdpShadow.App;

/// <summary>Windows Credential Manager, "Windows credential" entries: exactly what <c>cmdkey /add</c> writes.
/// SMB, the service manager and NTLM to the agent all pick these up for the host.</summary>
static class Credentials
{
    const int CRED_TYPE_DOMAIN_PASSWORD = 2, CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        public int Flags, Type; public string TargetName; public string? Comment; public long LastWritten;
        public int CredentialBlobSize; public nint CredentialBlob; public int Persist, AttributeCount; public nint Attributes;
        public string? TargetAlias, UserName;
    }

    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredWrite(ref CREDENTIAL cred, int flags);
    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredRead(string target, int type, int flags, out nint cred);
    [DllImport("advapi32")] static extern void CredFree(nint cred);

    /// <summary>User name saved for the host, or null. The password itself is never readable for this type.</summary>
    public static string? SavedUser(string host)
    {
        if (!CredRead(host, CRED_TYPE_DOMAIN_PASSWORD, 0, out var p)) return null;
        try { return Marshal.PtrToStructure<CREDENTIAL>(p).UserName; }
        finally { CredFree(p); }
    }

    public static void Save(string host, string user, string password)
    {
        var blob = Marshal.StringToCoTaskMemUni(password);
        try
        {
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_DOMAIN_PASSWORD, TargetName = host, UserName = user, Persist = CRED_PERSIST_LOCAL_MACHINE,
                CredentialBlob = blob, CredentialBlobSize = password.Length * 2,
            };
            if (!CredWrite(ref cred, 0)) throw new Win32Exception();
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
    }
}
