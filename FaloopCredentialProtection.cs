using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SRankSentinel;

/// <summary>
/// Protects the optional remembered Faloop password with Windows DPAPI. Omitting the
/// LOCAL_MACHINE flag binds ciphertext to the current Windows user on this computer.
/// </summary>
internal static class FaloopCredentialProtection
{
    private const uint CryptprotectUiForbidden = 0x1;
    private static readonly byte[] OptionalEntropy =
        Encoding.UTF8.GetBytes("SRankSentinel.FaloopCredential.v1");

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("Crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("Crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("Kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static bool TryProtect(string plaintext, out string protectedValue, out string error)
    {
        protectedValue = string.Empty;
        error = string.Empty;
        if (string.IsNullOrEmpty(plaintext))
        {
            error = "Enter the Faloop password before enabling remembered login.";
            return false;
        }

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var dataIn = Allocate(plaintextBytes);
        var entropy = Allocate(OptionalEntropy);
        DataBlob dataOut = default;
        try
        {
            if (!CryptProtectData(ref dataIn, "SRankSentinel Faloop login", ref entropy,
                    IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out dataOut))
            {
                error = $"Windows could not protect the Faloop login (error {Marshal.GetLastWin32Error()}).";
                return false;
            }

            var encryptedBytes = Copy(dataOut);
            try
            {
                protectedValue = Convert.ToBase64String(encryptedBytes);
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encryptedBytes);
            }
        }
        catch
        {
            error = "Windows could not securely protect the Faloop login.";
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
            FreeAllocated(ref dataIn);
            FreeAllocated(ref entropy);
            FreeDpapi(ref dataOut);
        }
    }

    public static bool TryUnprotect(string protectedValue, out string plaintext, out string error)
    {
        plaintext = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            error = "No remembered Faloop login is stored on this PC.";
            return false;
        }

        byte[] encryptedBytes;
        try
        {
            encryptedBytes = Convert.FromBase64String(protectedValue);
        }
        catch (FormatException)
        {
            error = "The remembered Faloop login is invalid; re-enter the credentials.";
            return false;
        }

        var dataIn = Allocate(encryptedBytes);
        var entropy = Allocate(OptionalEntropy);
        DataBlob dataOut = default;
        try
        {
            if (!CryptUnprotectData(ref dataIn, IntPtr.Zero, ref entropy,
                    IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out dataOut))
            {
                error = "Windows could not unlock the remembered Faloop login for this user; re-enter it.";
                return false;
            }

            var plaintextBytes = Copy(dataOut);
            try
            {
                plaintext = Encoding.UTF8.GetString(plaintextBytes);
                if (!string.IsNullOrEmpty(plaintext))
                    return true;
                error = "The remembered Faloop login was empty; re-enter the credentials.";
                return false;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintextBytes);
            }
        }
        catch
        {
            error = "Windows could not unlock the remembered Faloop login; re-enter it.";
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptedBytes);
            FreeAllocated(ref dataIn);
            FreeAllocated(ref entropy);
            FreeDpapi(ref dataOut);
        }
    }

    private static DataBlob Allocate(byte[] bytes)
    {
        if (bytes.Length == 0)
            return default;
        var data = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, data, bytes.Length);
        return new DataBlob { Size = bytes.Length, Data = data };
    }

    private static byte[] Copy(DataBlob blob)
    {
        if (blob.Size <= 0 || blob.Data == IntPtr.Zero)
            return [];
        var bytes = new byte[blob.Size];
        Marshal.Copy(blob.Data, bytes, 0, blob.Size);
        return bytes;
    }

    private static void FreeAllocated(ref DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero)
            return;
        ZeroUnmanaged(blob);
        Marshal.FreeHGlobal(blob.Data);
        blob = default;
    }

    private static void FreeDpapi(ref DataBlob blob)
    {
        if (blob.Data == IntPtr.Zero)
            return;
        ZeroUnmanaged(blob);
        LocalFree(blob.Data);
        blob = default;
    }

    private static void ZeroUnmanaged(DataBlob blob)
    {
        for (var index = 0; index < blob.Size; index++)
            Marshal.WriteByte(blob.Data, index, 0);
    }
}
