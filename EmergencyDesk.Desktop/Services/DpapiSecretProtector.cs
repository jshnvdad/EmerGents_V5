using System.Security.Cryptography;
using System.Text;

namespace EmergencyDesk.Services;

/// <summary>Protects secrets with Windows DPAPI: only the same Windows user on the same PC can read them back.</summary>
public class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EmergencyDesk.Reports.v1");

    public string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public string Unprotect(string protectedText)
    {
        if (string.IsNullOrEmpty(protectedText)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";   // different Windows user / PC, or damaged value: the operator has to type the password again
        }
    }
}
