using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ShelfRow.Core.Interfaces;
using Windows.Security.Credentials;

namespace ShelfRow.App.Services;

/// <summary>
/// Secure credential storage using Windows PasswordVault (Credential Locker)
/// with an automatic Windows DPAPI fallback for unpackaged desktop environments.
/// </summary>
public class CredentialLockerStorageService : ISecureStorageService
{
    private const string ResourceName = "ShelfRow.Windows";
    private readonly string _dpapiFallbackDirectory;
    private readonly bool _usePasswordVault;

    public CredentialLockerStorageService(string? fallbackDirectory = null)
    {
        _dpapiFallbackDirectory = fallbackDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShelfRow",
            "Security"
        );

        // PasswordVault requires MSIX package identity. For unpackaged desktop/tests, DPAPI is used.
        _usePasswordVault = CheckHasPackageIdentity();
    }

    private static bool CheckHasPackageIdentity()
    {
        try
        {
            var length = 0;
            return GetCurrentPackageFullName(ref length, null) != 15700L; // APPMODEL_ERROR_NO_PACKAGE
        }
        catch
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);

    public Task SetSecretAsync(string key, string secret, CancellationToken cancellationToken = default)
    {
        if (_usePasswordVault)
        {
            try
            {
                var vault = new PasswordVault();
                try
                {
                    var existing = vault.Retrieve(ResourceName, key);
                    if (existing != null)
                        vault.Remove(existing);
                }
                catch { }

                var credential = new PasswordCredential(ResourceName, key, secret);
                vault.Add(credential);
                return Task.CompletedTask;
            }
            catch { }
        }

        // DPAPI (Safe, robust, zero-hang desktop security)
        SetSecretDpapi(key, secret);
        return Task.CompletedTask;
    }

    public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_usePasswordVault)
        {
            try
            {
                var vault = new PasswordVault();
                var credential = vault.Retrieve(ResourceName, key);
                if (credential != null)
                {
                    credential.RetrievePassword();
                    return Task.FromResult<string?>(credential.Password);
                }
            }
            catch { }
        }

        return Task.FromResult(GetSecretDpapi(key));
    }

    public Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var vault = new PasswordVault();
            var credential = vault.Retrieve(ResourceName, key);
            if (credential != null)
            {
                vault.Remove(credential);
            }
        }
        catch { }

        // Clean up DPAPI fallback if it exists
        DeleteSecretDpapi(key);
        return Task.CompletedTask;
    }

    private void SetSecretDpapi(string key, string secret)
    {
        Directory.CreateDirectory(_dpapiFallbackDirectory);
        string safeFileName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".dat";
        string filePath = Path.Combine(_dpapiFallbackDirectory, safeFileName);

        byte[] plainBytes = Encoding.UTF8.GetBytes(secret);
        byte[] encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(filePath, encryptedBytes);
    }

    private string? GetSecretDpapi(string key)
    {
        string safeFileName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".dat";
        string filePath = Path.Combine(_dpapiFallbackDirectory, safeFileName);

        if (!File.Exists(filePath))
            return null;

        try
        {
            byte[] encryptedBytes = File.ReadAllBytes(filePath);
            byte[] plainBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return null;
        }
    }

    private void DeleteSecretDpapi(string key)
    {
        string safeFileName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".dat";
        string filePath = Path.Combine(_dpapiFallbackDirectory, safeFileName);
        if (File.Exists(filePath))
        {
            try { File.Delete(filePath); } catch { }
        }
    }
}
