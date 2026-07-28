using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Text;
using System.Security.Cryptography;
using System.IO;
using System.Configuration;

namespace SignalTracker
{
    public class AESEncrytDecry
    {
        private const string DefaultAESKey = "xsbbucbducbhbub78r848";
        private const string DefaultAESIV = "cjhdbchdbcbc788746748567";
        private const string DefaultPassword = "kpmg@admin";
        private const string DefaultHash = "SHA1";
        private const string DefaultSalt = "aselrias38490a32";
        private const string DefaultVector = "8947az34awl34kjq";

        public static readonly string AESKey = GetSecret("SIGNALTRACKER_AES_KEY", DefaultAESKey);
        public static readonly string AESIV = GetSecret("SIGNALTRACKER_AES_IV", DefaultAESIV);
        #region Settings

        private static int _iterations = GetIntSecret("SIGNALTRACKER_AES_ITERATIONS", 2);
        private static int _keySize = GetIntSecret("SIGNALTRACKER_AES_KEY_SIZE", 256);
        private static string password = GetSecret("SIGNALTRACKER_AES_PASSWORD", DefaultPassword);
        private static string _hash = GetSecret("SIGNALTRACKER_AES_HASH", DefaultHash);
        private static string _salt = GetSecret("SIGNALTRACKER_AES_SALT", DefaultSalt);
        private static string _vector = GetSecret("SIGNALTRACKER_AES_VECTOR", DefaultVector);

        #endregion

        private static string GetSecret(string name, string fallback)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static int GetIntSecret(string name, int fallback)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
        }

        public static string Encrypt(string value)
        {
            return Encrypt(value, password);
        }
        public static string Encrypt(string value, string password)
        {
            byte[] vectorBytes = System.Text.Encoding.ASCII.GetBytes(_vector);
            byte[] saltBytes = System.Text.Encoding.ASCII.GetBytes(_salt);
            byte[] valueBytes = System.Text.Encoding.UTF8.GetBytes(value);

            byte[] encrypted;
            using (Aes cipher = Aes.Create())
            {
                PasswordDeriveBytes _passwordBytes =
                    new PasswordDeriveBytes(password, saltBytes, _hash, _iterations);
                byte[] keyBytes = _passwordBytes.GetBytes(_keySize / 8);

                cipher.Mode = CipherMode.CBC;

                using (ICryptoTransform encryptor = cipher.CreateEncryptor(keyBytes, vectorBytes))
                {
                    using (MemoryStream to = new MemoryStream())
                    {
                        using (CryptoStream writer = new CryptoStream(to, encryptor, CryptoStreamMode.Write))
                        {
                            writer.Write(valueBytes, 0, valueBytes.Length);
                            writer.FlushFinalBlock();
                            encrypted = to.ToArray();
                        }
                    }
                }
                cipher.Clear();
            }
            return Convert.ToBase64String(encrypted);
        }

        public static string Decrypt(string value)
        {
            return Decrypt(value, password);
        }
        public static string Decrypt(string value, string password)
        {
            byte[] vectorBytes = System.Text.Encoding.ASCII.GetBytes(_vector);
            byte[] saltBytes = System.Text.Encoding.ASCII.GetBytes(_salt);
            byte[] valueBytes = Convert.FromBase64String(value);

            byte[] decrypted;
            int decryptedByteCount = 0;

            using (Aes cipher = Aes.Create())
            {
                PasswordDeriveBytes _passwordBytes = new PasswordDeriveBytes(password, saltBytes, _hash, _iterations);
                byte[] keyBytes = _passwordBytes.GetBytes(_keySize / 8);

                cipher.Mode = CipherMode.CBC;

                try
                {
                    using (ICryptoTransform decryptor = cipher.CreateDecryptor(keyBytes, vectorBytes))
                    {
                        using (MemoryStream from = new MemoryStream(valueBytes))
                        {
                            using (CryptoStream reader = new CryptoStream(from, decryptor, CryptoStreamMode.Read))
                            {
                                decrypted = new byte[valueBytes.Length];
                                decryptedByteCount = reader.Read(decrypted, 0, decrypted.Length);
                            }
                        }
                    }
                }
                catch
                {
                    return String.Empty;
                }

                cipher.Clear();
            }
            return Encoding.UTF8.GetString(decrypted, 0, decryptedByteCount);
        }
        
        //public static string ComputeSha256Hash(string rawData)
        //{
        //    // Create a SHA256   
        //    using (SHA256 sha256Hash = SHA256.Create())
        //    {
        //        // ComputeHash - returns byte array  
        //        byte[] bytes = sha256Hash.ComputeHash(Encoding.UTF8.GetBytes(rawData));

        //        // Convert byte array to a string   
        //        StringBuilder builder = new StringBuilder();
        //        for (int i = 0; i < bytes.Length; i++)
        //        {
        //            builder.Append(bytes[i].ToString("x2"));
        //        }
        //        return builder.ToString();
        //    }
        //}
    }

}


