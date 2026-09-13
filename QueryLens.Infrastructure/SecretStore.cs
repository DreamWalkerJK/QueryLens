using System.Security.Cryptography;
using System.Text;

namespace QueryLens.Infrastructure;

public interface ISecretStore
{
    Task<string?> ResolveAsync(string? reference, CancellationToken cancellationToken = default);
    Task<string> SaveAsync(string secret, CancellationToken cancellationToken = default);
    Task DeleteAsync(string? reference, CancellationToken cancellationToken = default);
}

/// <summary>Secrets are encrypted by Windows DPAPI for the current Windows user.</summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private readonly string directory;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("QueryLens.credentials.v1");

    public DpapiSecretStore(string directory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("此发布版凭据存储需要 Windows DPAPI。");
        this.directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(this.directory);
    }

    public async Task<string> SaveAsync(string secret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secret);
        cancellationToken.ThrowIfCancellationRequested();
        var reference = Guid.NewGuid().ToString("N");
        var plaintext = Encoding.UTF8.GetBytes(secret);
        byte[]? encrypted = null;
        try
        {
#pragma warning disable CA1416
            encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
#pragma warning restore CA1416
            await File.WriteAllBytesAsync(GetPath(reference), encrypted, cancellationToken).ConfigureAwait(false);
            return reference;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted); }
    }

    public async Task<string?> ResolveAsync(string? reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var path = GetPath(reference);
        if (!File.Exists(path)) throw new AdapterException(AdapterErrorKind.SecretUnavailable, "未找到本机保存的凭据；请编辑连接并重新输入密码。");
        var encrypted = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        byte[]? plaintext = null;
        try
        {
#pragma warning disable CA1416
            plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
#pragma warning restore CA1416
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException) { throw new AdapterException(AdapterErrorKind.SecretUnavailable, "Windows 无法解密此凭据；请使用保存凭据的 Windows 账号或重新输入密码。"); }
        finally { CryptographicOperations.ZeroMemory(encrypted); if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    public Task DeleteAsync(string? reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(reference)) File.Delete(GetPath(reference));
        return Task.CompletedTask;
    }

    private string GetPath(string reference)
    {
        if (!Guid.TryParseExact(reference, "N", out var id)) throw new AdapterException(AdapterErrorKind.SecretUnavailable, "凭据引用无效；请重新保存连接密码。");
        return Path.Combine(directory, id.ToString("N") + ".secret");
    }
}
