using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace LeagueLogin.Services
{
    /// <summary>
    /// Persists account credentials in the Windows Credential Manager.
    /// Each entry uses the target name "LeagueLogin_&lt;label&gt;".
    /// Credentials are encrypted by Windows (DPAPI) and are per-machine.
    /// </summary>
    public static class CredentialStore
    {
        private const string Prefix = "LeagueLogin_";

        // ── Public API ─────────────────────────────────────────────────────────

        public static List<string> ListAccounts()
        {
            var names = new List<string>();

            if (!NativeMethods.CredEnumerate(Prefix + "*", 0, out int count, out var ptr))
                return names;

            try
            {
                for (int i = 0; i < count; i++)
                {
                    var credPtr = Marshal.ReadIntPtr(ptr, i * IntPtr.Size);
                    var cred    = Marshal.PtrToStructure<NativeMethods.CREDENTIAL>(credPtr);
                    if (cred.TargetName?.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) == true)
                        names.Add(cred.TargetName[Prefix.Length..]);
                }
            }
            finally
            {
                NativeMethods.CredFree(ptr);
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        public static (string Username, string Password)? GetCredential(string accountLabel)
        {
            if (!NativeMethods.CredRead(Prefix + accountLabel,
                    NativeMethods.CRED_TYPE_GENERIC, 0, out var ptr))
                return null;

            try
            {
                var cred     = Marshal.PtrToStructure<NativeMethods.CREDENTIAL>(ptr);
                var username = cred.UserName ?? string.Empty;
                var password = string.Empty;

                if (cred.CredentialBlobSize > 0 && cred.CredentialBlob != IntPtr.Zero)
                {
                    var bytes = new byte[cred.CredentialBlobSize];
                    Marshal.Copy(cred.CredentialBlob, bytes, 0, (int)cred.CredentialBlobSize);
                    password = Encoding.Unicode.GetString(bytes);
                }

                return (username, password);
            }
            finally
            {
                NativeMethods.CredFree(ptr);
            }
        }

        public static void SaveAccount(string accountLabel, string username, string password)
        {
            var passBytes = Encoding.Unicode.GetBytes(password);
            var passPtr   = Marshal.AllocHGlobal(passBytes.Length);
            Marshal.Copy(passBytes, 0, passPtr, passBytes.Length);

            try
            {
                var cred = new NativeMethods.CREDENTIAL
                {
                    Type               = NativeMethods.CRED_TYPE_GENERIC,
                    TargetName         = Prefix + accountLabel,
                    UserName           = username,
                    CredentialBlob     = passPtr,
                    CredentialBlobSize = (uint)passBytes.Length,
                    Persist            = NativeMethods.CRED_PERSIST_LOCAL_MACHINE,
                };

                if (!NativeMethods.CredWrite(ref cred, 0))
                    throw new InvalidOperationException(
                        "CredWrite failed: " + Marshal.GetLastWin32Error());
            }
            finally
            {
                Marshal.FreeHGlobal(passPtr);
            }
        }

        public static void DeleteAccount(string accountLabel)
            => NativeMethods.CredDelete(Prefix + accountLabel, NativeMethods.CRED_TYPE_GENERIC, 0);

        public static bool AccountExists(string accountLabel)
        {
            bool ok = NativeMethods.CredRead(
                Prefix + accountLabel, NativeMethods.CRED_TYPE_GENERIC, 0, out var ptr);
            if (ok) NativeMethods.CredFree(ptr);
            return ok;
        }

        // ── P/Invoke ───────────────────────────────────────────────────────────

        private static class NativeMethods
        {
            public const uint CRED_TYPE_GENERIC          = 1;
            public const uint CRED_PERSIST_LOCAL_MACHINE = 2;

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            public struct CREDENTIAL
            {
                public uint    Flags;
                public uint    Type;
                public string? TargetName;
                public string? Comment;
                public long    LastWritten;
                public uint    CredentialBlobSize;
                public IntPtr  CredentialBlob;
                public uint    Persist;
                public uint    AttributeCount;
                public IntPtr  Attributes;
                public string? TargetAlias;
                public string? UserName;
            }

            [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern bool CredRead(string target, uint type, int reserved, out IntPtr ptr);

            [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern bool CredWrite(ref CREDENTIAL cred, uint flags);

            [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern bool CredDelete(string target, uint type, int reserved);

            [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern bool CredEnumerate(string? filter, int flags,
                out int count, out IntPtr credsPtr);

            [DllImport("advapi32.dll", SetLastError = true)]
            public static extern bool CredFree(IntPtr buffer);
        }
    }
}
