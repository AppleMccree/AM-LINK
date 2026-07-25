namespace ClassInterpreter.Core.Configuration;

public interface ISecretStore
{
    ValueTask SaveAsync(string target, string secret, CancellationToken cancellationToken = default);

    ValueTask<string?> ReadAsync(string target, CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(string target, CancellationToken cancellationToken = default);
}
