using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Zeroshot.Native;

/// <summary>Native's controller-pipe check, applied to the raw descriptor so no managed ACL canonicalization changes it.</summary>
[SupportedOSPlatform("windows")]
internal static class NamedPipeSecurity
{
    private const int FileObject = 1;
    private const uint OwnerAndDacl = 0x1 | 0x4;
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);

    /// <summary>Owned by the process user, with a present non-null DACL whose every ACE is an
    /// access-allowed ACE for that user or SYSTEM.</summary>
    public static bool IsPrivate(SafePipeHandle handle)
    {
        if (GetSecurityInfo(handle, FileObject, OwnerAndDacl, 0, 0, 0, 0, out var memory) != 0) return false;
        RawSecurityDescriptor descriptor;
        try
        {
            var bytes = new byte[GetSecurityDescriptorLength(memory)];
            Marshal.Copy(memory, bytes, 0, bytes.Length);
            descriptor = new RawSecurityDescriptor(bytes, 0);
        }
        finally { LocalFree(memory); }
        // Native compares against its process token, never a thread's impersonation token.
        var user = WindowsIdentity.RunImpersonated(SafeAccessTokenHandle.InvalidHandle, () =>
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User!;
        });
        return descriptor.Owner == user && descriptor.DiscretionaryAcl is { } dacl &&
            dacl.Cast<GenericAce>().All(ace => ace is CommonAce { AceType: AceType.AccessAllowed } allowed &&
                (allowed.SecurityIdentifier == user || allowed.SecurityIdentifier == LocalSystem));
    }

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint GetSecurityInfo(SafeHandle handle, int objectType, uint securityInfo,
        nint owner, nint group, nint dacl, nint sacl, out nint descriptor);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint GetSecurityDescriptorLength(nint descriptor);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint LocalFree(nint memory);
}
