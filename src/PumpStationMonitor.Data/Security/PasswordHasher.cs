using System.Security.Cryptography;
namespace PumpStationMonitor.Data.Security;

public static class PasswordHasher
{
    private const int Iterations = 210000;
    public static string Hash(string password)
    {
        if (password.Length < 8) throw new ArgumentException("密码至少 8 位");
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }
    public static bool Verify(string password, string encoded)
    {
        var parts = encoded.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out int iterations) || iterations is < 100000 or > 1000000) return false;
        // 数据库损坏的哈希不当作有效凭据，由调用者记录异常。
        byte[] salt = Convert.FromBase64String(parts[1]), expected = Convert.FromBase64String(parts[2]);
        if (salt.Length != 16 || expected.Length != 32) return false;
        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
