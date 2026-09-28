using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace EagleBoards.App;

/// <summary>
/// The SignUpGenius API key, entered in the start-up window and kept per
/// Windows user in the registry (HKEY_CURRENT_USER\Software\Eagle Boards),
/// encrypted to that Windows account (DPAPI), as the Mac version keeps it in
/// the keychain. Never in the data folder: the Java version shares that, and
/// its launcher reads its own key from a .env file there.
/// </summary>
public sealed class SignUpGeniusKeyStore
{
    private const string ValueName = "SignUpGeniusKey";

    /// <summary>Ties the encrypted value to this use, so it can't be passed off as some other protected blob.</summary>
    private static readonly byte[] Entropy = "Eagle Boards SignUpGenius API key"u8.ToArray();

    /// <summary>Registry sub-key under HKEY_CURRENT_USER, or null to hold the key in memory only.</summary>
    private readonly string? _subKey;

    private string? _memory;

    private SignUpGeniusKeyStore(string? subKey, string? memory)
    {
        _subKey = subKey;
        _memory = memory;
    }

    /// <summary>The Windows user's saved key.</summary>
    public static SignUpGeniusKeyStore CurrentUser { get; } = new(@"Software\Eagle Boards", null);

    /// <summary>
    /// Held in memory, never read from or written to the registry: the
    /// snapshot harness uses this so a screenshot can't show, or an import
    /// use, the district's real key.
    /// </summary>
    public static SignUpGeniusKeyStore InMemory(string? key = null) => new(null, key);

    /// <summary>At least looks like a key: SignUpGenius keys are long, and the Java launcher's placeholder isn't one.</summary>
    public static bool IsPlausible(string? key) => key is { Length: > 10 } && key != "replace-with-real-key";

    /// <summary>The saved key, or null if there's none or it can't be read (saved by another Windows account, say).</summary>
    public string? Read()
    {
        if (_subKey == null)
        {
            return _memory;
        }

        try
        {
            using var reg = Registry.CurrentUser.OpenSubKey(_subKey);
            if (reg?.GetValue(ValueName) is not byte[] sealedKey)
            {
                return null;
            }

            var key = Encoding.UTF8.GetString(ProtectedData.Unprotect(sealedKey, Entropy, DataProtectionScope.CurrentUser));
            return key.Length > 0 ? key : null;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Keep <paramref name="key"/>, or forget the saved one when it's empty. Returns whether that worked.</summary>
    public bool Save(string key)
    {
        key = key.Trim();
        if (_subKey == null)
        {
            _memory = key.Length > 0 ? key : null;
            return true;
        }

        try
        {
            if (key.Length == 0)
            {
                using var existing = Registry.CurrentUser.OpenSubKey(_subKey, writable: true);
                existing?.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }

            using var reg = Registry.CurrentUser.CreateSubKey(_subKey);
            reg.SetValue(ValueName,
                ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser),
                RegistryValueKind.Binary);
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
