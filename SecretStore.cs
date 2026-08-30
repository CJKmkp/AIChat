using System;
using System.Security.Cryptography;
using System.Text;

namespace AIChat
{
    /// <summary>
    /// 插件自管的 AES 加密工具，不依赖操作系统的 DPAPI 或凭据服务。
    /// 密文格式为：版本标识 + salt + IV + AES-CBC 密文 + HMAC-SHA256 完整性校验。
    /// </summary>
    internal static class SecretStore
    {
        private const string EnvelopePrefix = "AICHAT-ENC-v1:";
        private static readonly byte[] KeyMaterial = Encoding.UTF8.GetBytes("com.icc.ai-chat/plugin-managed-encryption/v1");

        public static string ProtectText(string plain)
        {
            if (plain == null) plain = "";
            var salt = RandomNumberGenerator.GetBytes(16);
            var iv = RandomNumberGenerator.GetBytes(16);
            var data = Encoding.UTF8.GetBytes(plain);
            try
            {
                DeriveKeys(salt, out var encryptionKey, out var authenticationKey);
                try
                {
                    byte[] cipher;
                    using (var aes = Aes.Create())
                    {
                        aes.Key = encryptionKey;
                        aes.IV = iv;
                        aes.Mode = CipherMode.CBC;
                        aes.Padding = PaddingMode.PKCS7;
                        using var encryptor = aes.CreateEncryptor();
                        cipher = encryptor.TransformFinalBlock(data, 0, data.Length);
                    }

                    var payload = Combine(salt, iv, cipher);
                    using var hmac = new HMACSHA256(authenticationKey);
                    return EnvelopePrefix + Convert.ToBase64String(Combine(payload, hmac.ComputeHash(payload)));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(encryptionKey);
                    CryptographicOperations.ZeroMemory(authenticationKey);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(data);
            }
        }

        public static bool TryUnprotectText(string protectedText, out string plain)
        {
            plain = "";
            if (string.IsNullOrEmpty(protectedText) || !protectedText.StartsWith(EnvelopePrefix, StringComparison.Ordinal))
                return false;

            try
            {
                var payload = Convert.FromBase64String(protectedText.Substring(EnvelopePrefix.Length));
                const int saltLength = 16, ivLength = 16, macLength = 32;
                if (payload.Length <= saltLength + ivLength + macLength) return false;

                var cipherLength = payload.Length - saltLength - ivLength - macLength;
                var salt = Slice(payload, 0, saltLength);
                var iv = Slice(payload, saltLength, ivLength);
                var cipher = Slice(payload, saltLength + ivLength, cipherLength);
                var suppliedMac = Slice(payload, saltLength + ivLength + cipherLength, macLength);
                DeriveKeys(salt, out var encryptionKey, out var authenticationKey);
                try
                {
                    var signedPayload = Slice(payload, 0, payload.Length - macLength);
                    using var hmac = new HMACSHA256(authenticationKey);
                    if (!CryptographicOperations.FixedTimeEquals(suppliedMac, hmac.ComputeHash(signedPayload))) return false;

                    using var aes = Aes.Create();
                    aes.Key = encryptionKey;
                    aes.IV = iv;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    using var decryptor = aes.CreateDecryptor();
                    var decrypted = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
                    try { plain = Encoding.UTF8.GetString(decrypted); }
                    finally { CryptographicOperations.ZeroMemory(decrypted); }
                    return true;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(encryptionKey);
                    CryptographicOperations.ZeroMemory(authenticationKey);
                }
            }
            catch (CryptographicException) { return false; }
            catch (FormatException) { return false; }
        }

        /// <summary>兼容 API Key 字段使用的字节数组接口。</summary>
        public static byte[] ProtectString(string plain) => Encoding.UTF8.GetBytes(ProtectText(plain));

        public static string UnprotectToString(byte[] cipher)
        {
            if (cipher == null || cipher.Length == 0) return "";
            if (TryUnprotectText(Encoding.UTF8.GetString(cipher), out var plain)) return plain;
            throw new CryptographicException("Invalid plugin-managed encrypted data.");
        }

        public static string TryUnprotect(byte[] cipher)
        {
            if (cipher == null || cipher.Length == 0) return "";
            try { return UnprotectToString(cipher); }
            catch (CryptographicException) { return ""; }
        }

        private static void DeriveKeys(byte[] salt, out byte[] encryptionKey, out byte[] authenticationKey)
        {
            using var derive = new Rfc2898DeriveBytes(KeyMaterial, salt, 100000, HashAlgorithmName.SHA256);
            var keys = derive.GetBytes(64);
            encryptionKey = Slice(keys, 0, 32);
            authenticationKey = Slice(keys, 32, 32);
            CryptographicOperations.ZeroMemory(keys);
        }

        private static byte[] Combine(params byte[][] arrays)
        {
            var length = 0;
            foreach (var array in arrays) length += array.Length;
            var result = new byte[length];
            var offset = 0;
            foreach (var array in arrays)
            {
                Buffer.BlockCopy(array, 0, result, offset, array.Length);
                offset += array.Length;
            }
            return result;
        }

        private static byte[] Slice(byte[] source, int offset, int length)
        {
            var result = new byte[length];
            Buffer.BlockCopy(source, offset, result, 0, length);
            return result;
        }
    }
}
