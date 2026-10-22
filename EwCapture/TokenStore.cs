using System.Security.Cryptography;
using System.Text;

namespace EwCapture;

/// <summary>
/// The device token, encrypted with DPAPI so only this Windows user can read it. A token
/// belongs to one server, so each server has its own file: moving an install from dev to
/// the live site asks for a fresh link instead of sending dev's token there.
/// </summary>
static class TokenStore
{
    static string _file = "token.bin";

    /// <summary>Which server's token to use. dev keeps the original file name, so dev installs stay linked.</summary>
    public static void UseServer(Uri baseUrl) => _file = baseUrl.Host == "dev.exiledwealth.com" ? "token.bin" : $"token-{baseUrl.Host}.bin";

    static string FilePath => Path.Combine(App.DataDir, _file);

    public static string? Load()
    {
        try
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception e) when (e is IOException or CryptographicException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(string token)
    {
        Directory.CreateDirectory(App.DataDir);
        File.WriteAllBytes(FilePath, ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));
    }

    public static void Clear() => File.Delete(FilePath);
}
