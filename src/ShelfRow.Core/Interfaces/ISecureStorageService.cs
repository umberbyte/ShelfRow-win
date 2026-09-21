using System.Threading;
using System.Threading.Tasks;

namespace ShelfRow.Core.Interfaces;

/// <summary>
/// Service for securely persisting secrets like CloudKit WebAuth tokens or API keys.
/// </summary>
public interface ISecureStorageService
{
    Task SetSecretAsync(string key, string secret, CancellationToken cancellationToken = default);
    Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default);
    Task DeleteSecretAsync(string key, CancellationToken cancellationToken = default);
}
