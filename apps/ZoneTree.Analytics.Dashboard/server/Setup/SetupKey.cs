using System.Security.Cryptography;
using System.Text;

// A one-time key printed in the server log while no administrator exists. Only someone who can
// read the log can claim the server. It changes on every start and stops working after setup.
sealed class SetupKey
{
  const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
  string value = Create();

  static string Create()
  {
    var builder = new StringBuilder(14);
    for (var i = 0; i < 12; i++)
    {
      if (i > 0 && i % 4 == 0) builder.Append('-');
      builder.Append(Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]);
    }
    return builder.ToString();
  }

  public string Value => Volatile.Read(ref value);

  public bool Matches(string candidate)
  {
    var current = Volatile.Read(ref value);
    if (current == null || string.IsNullOrWhiteSpace(candidate)) return false;
    var normalized = candidate.Trim().ToUpperInvariant().Replace(" ", "");
    if (normalized.Length == 12) normalized = $"{normalized[..4]}-{normalized[4..8]}-{normalized[8..]}";
    return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(normalized), Encoding.ASCII.GetBytes(current));
  }

  public void Retire() => Volatile.Write(ref value, null);
}
