using System.Globalization;
using System.Security.Cryptography;

// PBKDF2-SHA256 hashes in a self-describing format: pbkdf2-sha256$iterations$salt$hash.
static class PasswordHasher
{
  const string Algorithm = "pbkdf2-sha256";
  const int Iterations = 600_000;
  const int SaltBytes = 16;
  const int HashBytes = 32;
  public const int MinLength = 10;
  public const int MaxLength = 256;

  // Verifying against it keeps an unknown user name as slow as a wrong password.
  static readonly string Dummy = Hash("unused-dummy-password");

  public static string Hash(string password)
  {
    var salt = RandomNumberGenerator.GetBytes(SaltBytes);
    var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
    return string.Join('$', Algorithm, Iterations.ToString(CultureInfo.InvariantCulture),
        Convert.ToBase64String(salt), Convert.ToBase64String(hash));
  }

  public static bool Verify(string password, string encoded)
  {
    var parts = (encoded ?? Dummy).Split('$');
    if (parts.Length != 4 || parts[0] != Algorithm
        || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) || iterations < 1)
      return false;
    var salt = Convert.FromBase64String(parts[2]);
    var expected = Convert.FromBase64String(parts[3]);
    var actual = Rfc2898DeriveBytes.Pbkdf2(password ?? "", salt, iterations, HashAlgorithmName.SHA256, expected.Length);
    return CryptographicOperations.FixedTimeEquals(actual, expected) && encoded != null;
  }

  public static string ValidateStrength(string password, string userName)
  {
    if (string.IsNullOrEmpty(password) || password.Length < MinLength)
      return $"Use at least {MinLength} characters for the password.";
    if (password.Length > MaxLength)
      return $"Use at most {MaxLength} characters for the password.";
    if (string.Equals(password, userName, StringComparison.OrdinalIgnoreCase))
      return "The password cannot be the user name.";
    return null;
  }
}
