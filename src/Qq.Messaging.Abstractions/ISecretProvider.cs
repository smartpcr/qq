namespace Qq.Messaging.Abstractions;

/// <summary>
/// Retrieves secrets (e.g., bot tokens) from a secure store.
/// Implementations must never log secret values.
/// </summary>
public interface ISecretProvider
{
    Task<string> GetSecretAsync(
        string secretName,
        CancellationToken cancellationToken = default);
}
