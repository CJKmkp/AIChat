using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AIChat
{
    /// <summary>
    /// 使用 Windows DPAPI 将 API Key 加密到本地文件，仅当前 Windows 用户可解密。
    /// 实现参考 SeewoAutoLogin/QrSessionStore.cs：CryptProtectData / CryptUnprotectData + 熵加盐。
    /// </summary>
    internal static class SecretStore
    {
        private const int CryptProtectUiForbidden = 0x1;
        private static readonly byte[] EntropyPrefix = Encoding.UTF8.GetBytes("com.icc.ai-chat/api-key/v1/");

        /// <summary>保护（加密）字节数组，返回 DPAPI 密文。</summary>
        private static byte[] Protect(byte[] data, byte[] entropy)
        {
            var input = CreateBlob(data);
            var ent = CreateBlob(entropy);
            DataBlob output = default;
            try
            {
                if (!CryptProtectData(ref input, null, ref ent, IntPtr.Zero, IntPtr.Zero,
                        CryptProtectUiForbidden, out output))
                {
                    throw new CryptographicException(Marshal.GetLastWin32Error());
                }
                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, output.Length);
                return result;
            }
            finally
            {
                FreeBlob(ref input, true);
                FreeBlob(ref ent, true);
                if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            }
        }

        /// <summary>解保护（解密）字节数组。</summary>
        private static byte[] Unprotect(byte[] data, byte[] entropy)
        {
            var input = CreateBlob(data);
            var ent = CreateBlob(entropy);
            DataBlob output = default;
            try
            {
                if (!CryptUnprotectData(ref input, IntPtr.Zero, ref ent, IntPtr.Zero, IntPtr.Zero,
                        CryptProtectUiForbidden, out output))
                {
                    throw new CryptographicException(Marshal.GetLastWin32Error());
                }
                var result = new byte[output.Length];
                Marshal.Copy(output.Data, result, 0, output.Length);
                return result;
            }
            finally
            {
                FreeBlob(ref input, true);
                FreeBlob(ref ent, true);
                if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            }
        }

        /// <summary>使用固定的应用级熵加密字符串。</summary>
        public static byte[] ProtectString(string plain)
        {
            if (plain == null) plain = "";
            var bytes = Encoding.UTF8.GetBytes(plain);
            try
            {
                return Protect(bytes, EntropyPrefix);
            }
            finally
            {
                Array.Clear(bytes, 0, bytes.Length);
            }
        }

        /// <summary>使用固定的应用级熵解密字节数组为字符串。</summary>
        public static string UnprotectToString(byte[] cipher)
        {
            if (cipher == null || cipher.Length == 0) return "";
            byte[] plain = null;
            try
            {
                plain = Unprotect(cipher, EntropyPrefix);
                return Encoding.UTF8.GetString(plain);
            }
            finally
            {
                if (plain != null) Array.Clear(plain, 0, plain.Length);
            }
        }

        public static string TryUnprotect(byte[] cipher)
        {
            try { return UnprotectToString(cipher); }
            catch (CryptographicException) { return ""; }
            catch (Exception) { return ""; }
        }

        // ---------- DPAPI interop ----------
        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Length;
            public IntPtr Data;
        }

        private static DataBlob CreateBlob(byte[] data)
        {
            if (data == null || data.Length == 0) return default;
            var ptr = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, ptr, data.Length);
            return new DataBlob { Length = data.Length, Data = ptr };
        }

        private static void FreeBlob(ref DataBlob blob, bool clear)
        {
            if (blob.Data == IntPtr.Zero) return;
            if (clear && blob.Length > 0)
            {
                var zeros = new byte[blob.Length];
                Marshal.Copy(zeros, 0, blob.Data, blob.Length);
            }
            Marshal.FreeHGlobal(blob.Data);
            blob = default;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(
            ref DataBlob dataIn, string description, ref DataBlob optionalEntropy,
            IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob dataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(
            ref DataBlob dataIn, IntPtr description, ref DataBlob optionalEntropy,
            IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}