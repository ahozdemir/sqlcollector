using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace SqlDataCollector.Setup;

/// <summary>Windows APIs the installer needs (credential check, "Log on as a service", service creation without passwords on a command line).</summary>
internal static class Native
{
    public static bool IsAdministrator()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>The computer's AD domain, or null when it is not joined to a domain.</summary>
    public static string? JoinedDomain()
    {
        if (NetGetJoinInformation(null, out var name, out var status) != 0) return null;
        try { return status == 3 /* NetSetupDomainName */ ? Marshal.PtrToStringUni(name) : null; }
        finally { NetApiBufferFree(name); }
    }

    public static SecurityIdentifier ResolveAccount(string account) =>
        (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));

    /// <summary>Checks user name and password against the domain (network logon; no profile is loaded).</summary>
    public static void ValidateCredentials(string account, string password)
    {
        var parts = account.Split('\\', 2);
        if (!LogonUser(parts[1], parts[0], password, 3 /* LOGON32_LOGON_NETWORK */, 0, out var token))
            throw new InvalidOperationException($"The password of {account} could not be verified: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        CloseHandle(token);
    }

    /// <summary>Grants SeServiceLogonRight ("Log on as a service"), without which a service with this account does not start.</summary>
    public static void GrantLogonAsService(string account)
    {
        var sid = new byte[ResolveAccount(account).BinaryLength];
        ResolveAccount(account).GetBinaryForm(sid, 0);
        var attrs = new LSA_OBJECT_ATTRIBUTES { Length = Marshal.SizeOf<LSA_OBJECT_ATTRIBUTES>() };
        var system = default(LSA_UNICODE_STRING);
        var status = LsaOpenPolicy(ref system, ref attrs, 0x00000010 | 0x00000800 /* POLICY_CREATE_ACCOUNT | POLICY_LOOKUP_NAMES */, out var policy);
        if (status != 0) throw new Win32Exception(LsaNtStatusToWinError(status));
        var right = "SeServiceLogonRight";
        var rights = new[] { new LSA_UNICODE_STRING { Buffer = Marshal.StringToHGlobalUni(right), Length = (ushort)(right.Length * 2), MaximumLength = (ushort)((right.Length + 1) * 2) } };
        try
        {
            status = LsaAddAccountRights(policy, sid, rights, 1);
            if (status != 0) throw new Win32Exception(LsaNtStatusToWinError(status));
        }
        finally
        {
            Marshal.FreeHGlobal(rights[0].Buffer);
            LsaClose(policy);
        }
    }

    /// <summary>Creates the service, or updates path and account of an existing one (password passed in memory only).</summary>
    public static void CreateOrUpdateService(string name, string displayName, string binaryPath, string account, string? password)
    {
        var scm = OpenSCManager(null, null, 0xF003F /* SC_MANAGER_ALL_ACCESS */);
        if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var svc = OpenService(scm, name, 0xF01FF /* SERVICE_ALL_ACCESS */);
            if (svc == IntPtr.Zero)
            {
                svc = CreateService(scm, name, displayName, 0xF01FF, 0x10 /* WIN32_OWN_PROCESS */, 2 /* AUTO_START */, 1 /* ERROR_NORMAL */,
                    $"\"{binaryPath}\"", null, IntPtr.Zero, null, account, password);
                if (svc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            else if (!ChangeServiceConfig(svc, 0xFFFFFFFF, 2, 0xFFFFFFFF, $"\"{binaryPath}\"", null, IntPtr.Zero, null, account, password, displayName))
            {
                var err = Marshal.GetLastWin32Error();
                CloseServiceHandle(svc);
                throw new Win32Exception(err);
            }
            CloseServiceHandle(svc);
        }
        finally { CloseServiceHandle(scm); }
    }

    public static bool ServiceExists(string name)
    {
        var scm = OpenSCManager(null, null, 0x0001 /* SC_MANAGER_CONNECT */);
        if (scm == IntPtr.Zero) return false;
        try
        {
            var svc = OpenService(scm, name, 0x0004 /* SERVICE_QUERY_STATUS */);
            if (svc == IntPtr.Zero) return false;
            CloseServiceHandle(svc);
            return true;
        }
        finally { CloseServiceHandle(scm); }
    }

    // ------------------------------------------------------------------ interop

    [StructLayout(LayoutKind.Sequential)]
    private struct LSA_UNICODE_STRING { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LSA_OBJECT_ATTRIBUTES { public int Length; public IntPtr RootDirectory; public IntPtr ObjectName; public int Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService; }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint LsaOpenPolicy(ref LSA_UNICODE_STRING systemName, ref LSA_OBJECT_ATTRIBUTES attributes, int access, out IntPtr policy);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint LsaAddAccountRights(IntPtr policy, byte[] sid, LSA_UNICODE_STRING[] rights, int count);
    [DllImport("advapi32.dll")]
    private static extern uint LsaClose(IntPtr policy);
    [DllImport("advapi32.dll")]
    private static extern int LsaNtStatusToWinError(uint status);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(string user, string domain, string password, int logonType, int provider, out IntPtr token);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetGetJoinInformation(string? server, out IntPtr name, out int status);
    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr scm, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateService(IntPtr scm, string name, string displayName, uint access, uint serviceType, uint startType,
        uint errorControl, string binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies, string? account, string? password);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ChangeServiceConfig(IntPtr service, uint serviceType, uint startType, uint errorControl, string? binaryPath,
        string? loadOrderGroup, IntPtr tagId, string? dependencies, string? account, string? password, string? displayName);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
