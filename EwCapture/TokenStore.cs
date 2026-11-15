using System.Security.Cryptography;
using System.Text;
using EwCapture.Core;

namespace EwCapture;

/// <summary>
/// The device token, encrypted with DPAPI so only this Windows user can read it. A token belongs to one
/// server, so each server has its own file (<see cref="ServerProfile.TokenFile"/>): moving an install from
/// dev to the live site asks for a fresh link instead of sending dev's token there.
/// </summary>
sealed class TokenStore(ServerProfile server)
{
    public string? Load()
    {
        try
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(server.TokenFile), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception e) when (e is IOException or CryptographicException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(server.TokenFile)!);
        File.WriteAllBytes(server.TokenFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));
    }

    public void Clear() => File.Delete(server.TokenFile);
}
