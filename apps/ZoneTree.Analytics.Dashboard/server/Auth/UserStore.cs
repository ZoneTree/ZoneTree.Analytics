using System.Security.Cryptography;
using System.Text.RegularExpressions;

enum UserRole { Administrator, Analyst, Viewer }

sealed class UserRecord
{
  public string Id { get; set; }
  public string UserName { get; set; }
  public string DisplayName { get; set; }
  public string PasswordHash { get; set; }
  public UserRole Role { get; set; }
  // Non-administrators read every site, or only the listed ones.
  public bool AllSites { get; set; }
  public List<string> Sites { get; set; } = [];
  public bool Disabled { get; set; }
  // Changing it ends every session of the user.
  public string SecurityStamp { get; set; }
  public DateTime Created { get; set; }
  public DateTime? LastSignIn { get; set; }

  public bool IsAdministrator => Role == UserRole.Administrator;

  public bool CanQuery => Role is UserRole.Administrator or UserRole.Analyst;

  // Dashboard usage and the Development datasets are the administrator's.
  public bool CanAccess(string site) => IsAdministrator
      || (!SettingsMapper.ReservedSites.Contains(site) && (AllSites || Sites.Contains(site, StringComparer.Ordinal)));
}

// The public view of a user: no password hash or security stamp.
sealed record UserView(string Id, string UserName, string DisplayName, UserRole Role, bool AllSites,
    IReadOnlyList<string> Sites, bool Disabled, DateTime Created, DateTime? LastSignIn)
{
  public static UserView From(UserRecord user) => new(user.Id, user.UserName, user.DisplayName, user.Role,
      user.IsAdministrator || user.AllSites, user.Sites, user.Disabled, user.Created, user.LastSignIn);
}

sealed record UserChange(string UserName, string DisplayName, UserRole? Role, bool? AllSites, List<string> Sites,
    bool? Disabled, string Password);

sealed class UserStoreException(string message) : Exception(message);

sealed partial class UserStore(SystemStore store)
{
  const string UserPrefix = "user/";
  const string NamePrefix = "username/";

  [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._@-]{2,63}$")]
  private static partial Regex UserNamePattern();

  static string NameKey(string userName) => NamePrefix + userName.ToLowerInvariant();

  public bool HasAdministrator => List().Any(user => user.IsAdministrator);

  public UserRecord Get(string id) => id != null && store.TryGet<UserRecord>(UserPrefix + id, out var user) ? user : null;

  public UserRecord FindByName(string userName)
  {
    if (string.IsNullOrWhiteSpace(userName)) return null;
    return store.TryGet<string>(NameKey(userName.Trim()), out var id) ? Get(id) : null;
  }

  public List<UserRecord> List() =>
      [.. store.List<UserRecord>(UserPrefix).Select(entry => entry.Value).OrderBy(user => user.UserName, StringComparer.OrdinalIgnoreCase)];

  public UserRecord Create(UserChange change, ISet<string> knownSites)
  {
    lock (store.WriteLock)
    {
      var role = change.Role ?? UserRole.Viewer;
      if (role == UserRole.Administrator && HasAdministrator)
        throw new UserStoreException("The dashboard has one administrator. Choose the analyst or viewer role.");
      var userName = ValidateUserName(change.UserName);
      if (store.Contains(NameKey(userName)))
        throw new UserStoreException($"The user name '{userName}' is taken.");
      var problem = PasswordHasher.ValidateStrength(change.Password, userName);
      if (problem != null) throw new UserStoreException(problem);
      var user = new UserRecord
      {
        Id = Guid.NewGuid().ToString("N"),
        UserName = userName,
        DisplayName = ValidateDisplayName(change.DisplayName, userName),
        PasswordHash = PasswordHasher.Hash(change.Password),
        Role = role,
        AllSites = role == UserRole.Administrator || (change.AllSites ?? false),
        Sites = role == UserRole.Administrator ? [] : ValidateSites(change.Sites, knownSites),
        SecurityStamp = NewStamp(),
        Created = DateTime.UtcNow,
      };
      store.Put(UserPrefix + user.Id, user);
      store.Put(NameKey(userName), user.Id);
      return user;
    }
  }

  // Applies the non-null members of the change. Role, site, password and status changes end
  // the user's sessions.
  public UserRecord Update(string id, UserChange change, ISet<string> knownSites)
  {
    lock (store.WriteLock)
    {
      var user = Get(id) ?? throw new UserStoreException("The user does not exist.");
      var endSessions = false;
      if (change.UserName != null && !string.Equals(change.UserName.Trim(), user.UserName, StringComparison.Ordinal))
      {
        var userName = ValidateUserName(change.UserName);
        if (!string.Equals(userName, user.UserName, StringComparison.OrdinalIgnoreCase) && store.Contains(NameKey(userName)))
          throw new UserStoreException($"The user name '{userName}' is taken.");
        store.Delete(NameKey(user.UserName));
        store.Put(NameKey(userName), user.Id);
        user.UserName = userName;
      }
      if (change.DisplayName != null)
        user.DisplayName = ValidateDisplayName(change.DisplayName, user.UserName);
      if (change.Role is { } role && role != user.Role)
      {
        if (user.IsAdministrator)
          throw new UserStoreException("The administrator's role cannot change.");
        if (role == UserRole.Administrator)
          throw new UserStoreException("The dashboard has one administrator. Choose the analyst or viewer role.");
        user.Role = role;
        endSessions = true;
      }
      if (!user.IsAdministrator)
      {
        if (change.AllSites is { } allSites && allSites != user.AllSites) { user.AllSites = allSites; endSessions = true; }
        if (change.Sites != null)
        {
          var sites = ValidateSites(change.Sites, knownSites);
          if (!sites.SequenceEqual(user.Sites, StringComparer.Ordinal)) { user.Sites = sites; endSessions = true; }
        }
      }
      if (change.Disabled is { } disabled && disabled != user.Disabled)
      {
        if (user.IsAdministrator && disabled)
          throw new UserStoreException("The administrator cannot be disabled.");
        user.Disabled = disabled;
        endSessions = true;
      }
      if (change.Password != null)
      {
        var problem = PasswordHasher.ValidateStrength(change.Password, user.UserName);
        if (problem != null) throw new UserStoreException(problem);
        user.PasswordHash = PasswordHasher.Hash(change.Password);
        endSessions = true;
      }
      if (endSessions) user.SecurityStamp = NewStamp();
      store.Put(UserPrefix + user.Id, user);
      return user;
    }
  }

  public void Delete(string id)
  {
    lock (store.WriteLock)
    {
      var user = Get(id) ?? throw new UserStoreException("The user does not exist.");
      if (user.IsAdministrator)
        throw new UserStoreException("The administrator cannot be deleted.");
      store.Delete(NameKey(user.UserName));
      store.Delete(UserPrefix + id);
    }
  }

  public void RecordSignIn(UserRecord user)
  {
    lock (store.WriteLock)
    {
      var current = Get(user.Id);
      if (current == null) return;
      current.LastSignIn = DateTime.UtcNow;
      store.Put(UserPrefix + current.Id, current);
    }
  }

  // Sites removed from the settings disappear from every user's list.
  public void RemoveSites(ISet<string> knownSites)
  {
    lock (store.WriteLock)
    {
      foreach (var user in List())
      {
        var sites = user.Sites.Where(knownSites.Contains).ToList();
        if (sites.Count == user.Sites.Count) continue;
        user.Sites = sites;
        store.Put(UserPrefix + user.Id, user);
      }
    }
  }

  // Checks a new account's name, display name and password without storing anything.
  public static void ValidateNew(UserChange change)
  {
    var userName = ValidateUserName(change.UserName);
    ValidateDisplayName(change.DisplayName, userName);
    var problem = PasswordHasher.ValidateStrength(change.Password, userName);
    if (problem != null) throw new UserStoreException(problem);
  }

  static string NewStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

  static string ValidateUserName(string userName)
  {
    userName = userName?.Trim();
    if (string.IsNullOrEmpty(userName) || !UserNamePattern().IsMatch(userName))
      throw new UserStoreException("Use 3 to 64 letters, digits, dots, dashes, underscores or @ for the user name, starting with a letter or digit.");
    return userName;
  }

  static string ValidateDisplayName(string displayName, string userName)
  {
    displayName = string.IsNullOrWhiteSpace(displayName) ? userName : displayName.Trim();
    if (displayName.Length > 100 || displayName.Any(char.IsControl))
      throw new UserStoreException("Use at most 100 printable characters for the display name.");
    return displayName;
  }

  static List<string> ValidateSites(List<string> sites, ISet<string> knownSites)
  {
    var result = (sites ?? []).Distinct(StringComparer.Ordinal).OrderBy(site => site, StringComparer.Ordinal).ToList();
    var unknown = result.FirstOrDefault(site => !knownSites.Contains(site));
    if (unknown != null) throw new UserStoreException($"The site '{unknown}' does not exist.");
    return result;
  }
}
