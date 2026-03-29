using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SqlVersionControl.Services;

/// <summary>
/// Password store with encrypted persistence.
/// Windows: DPAPI (per-user, per-machine).
/// macOS/Linux: AES-256 with machine-derived key.
/// </summary>
public static class PasswordStore
{
    private static readonly Dictionary<string, string> _passwords = new();
    private static readonly string _credentialsPath = Path.Combine(
        SettingsService.DefaultDataFolder, "credentials.json");

    private static string GetKey(string server, string database, string username)
        => $"{server}|{database}|{username}";

    public static void Store(string server, string database, string username, string password)
    {
        var key = GetKey(server, database, username);
        _passwords[key] = password;
    }

    public static string? Get(string server, string database, string username)
    {
        var key = GetKey(server, database, username);
        return _passwords.TryGetValue(key, out var password) ? password : null;
    }

    public static bool Has(string server, string database, string username)
    {
        var key = GetKey(server, database, username);
        return _passwords.ContainsKey(key);
    }

    public static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_credentialsPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // Encrypt each password before serializing
            var encrypted = new Dictionary<string, string>();
            foreach (var kvp in _passwords)
            {
                encrypted[kvp.Key] = Encrypt(kvp.Value);
            }

            var json = JsonSerializer.Serialize(encrypted, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_credentialsPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save credentials: {ex.Message}");
        }
    }

    public static void Load()
    {
        try
        {
            if (!File.Exists(_credentialsPath)) return;

            var json = File.ReadAllText(_credentialsPath);
            var encrypted = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (encrypted == null) return;

            foreach (var kvp in encrypted)
            {
                try
                {
                    _passwords[kvp.Key] = Decrypt(kvp.Value);
                }
                catch
                {
                    // Skip entries that fail to decrypt (corrupted or from different machine/user)
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load credentials: {ex.Message}");
        }
    }

    private static string Encrypt(string plainText)
    {
        var bytes = Encoding.UTF8.GetBytes(plainText);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }

        // macOS/Linux: AES-256 with machine-derived key
        return AesEncrypt(bytes);
    }

    private static string Decrypt(string encryptedText)
    {
        var bytes = Convert.FromBase64String(encryptedText);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var decrypted = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }

        // macOS/Linux: AES-256 with machine-derived key
        return AesDecrypt(bytes);
    }

    private static byte[] DeriveKey()
    {
        // Derive a key from machine name + username — ties to this user on this machine
        var seed = $"{Environment.MachineName}|{Environment.UserName}|Lookout";
        var salt = Encoding.UTF8.GetBytes("Lookout.PasswordStore");
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(seed), salt, 100_000, HashAlgorithmName.SHA256, 32);
    }

    private static string AesEncrypt(byte[] plainBytes)
    {
        using var aes = Aes.Create();
        aes.Key = DeriveKey();
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var encrypted = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        // Prepend IV to ciphertext
        var result = new byte[aes.IV.Length + encrypted.Length];
        aes.IV.CopyTo(result, 0);
        encrypted.CopyTo(result, aes.IV.Length);
        return Convert.ToBase64String(result);
    }

    private static string AesDecrypt(byte[] encryptedBytes)
    {
        using var aes = Aes.Create();
        aes.Key = DeriveKey();

        // Extract IV from first 16 bytes
        var iv = new byte[16];
        Array.Copy(encryptedBytes, 0, iv, 0, 16);
        aes.IV = iv;

        var ciphertext = new byte[encryptedBytes.Length - 16];
        Array.Copy(encryptedBytes, 16, ciphertext, 0, ciphertext.Length);

        using var decryptor = aes.CreateDecryptor();
        var decrypted = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
        return Encoding.UTF8.GetString(decrypted);
    }
}
