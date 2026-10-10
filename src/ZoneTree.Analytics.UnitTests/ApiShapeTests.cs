using System.Collections;
using System.Reflection;
using ZoneTree.Analytics.Web;

namespace ZoneTree.Analytics.UnitTests;

/// <summary>
/// Rules the public API follows, checked across every public type so that new members keep them.
/// </summary>
public sealed class ApiShapeTests
{
  static readonly Type[] PublicTypes = typeof(AnalyticsEngine).Assembly.GetExportedTypes();

  static IEnumerable<MethodInfo> PublicMethods(Type type) =>
      type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
          .Where(x => !x.IsSpecialName);

  static Type[] Parameters(MethodInfo method) => method.GetParameters().Select(x => x.ParameterType).ToArray();

  [TestCase(typeof(AnalyticsEngine))]
  [TestCase(typeof(WebAnalytics))]
  public void EveryOperationHasSynchronousAndAsynchronousForms(Type type)
  {
    var methods = PublicMethods(type).ToList();
    var problems = new List<string>();
    foreach (var asyncMethod in methods.Where(x => x.Name.EndsWith("Async", StringComparison.Ordinal)))
    {
      var name = asyncMethod.Name[..^"Async".Length];
      var parameters = Parameters(asyncMethod);
      // Tracking is the one operation whose synchronous form takes no token: it never blocks
      // unless the queue is full and configured to wait.
      var withoutToken = parameters.Length > 0 && parameters[^1] == typeof(CancellationToken) ? parameters[..^1] : parameters;
      var twin = methods.FirstOrDefault(x => x.Name == name &&
          (Parameters(x).SequenceEqual(parameters) || (name == nameof(AnalyticsEngine.Track) && Parameters(x).SequenceEqual(withoutToken))));
      if (twin == null)
        problems.Add($"{asyncMethod.Name} has no synchronous {name} with the same parameters.");
    }
    foreach (var method in methods.Where(x => x.Name is not (nameof(AnalyticsEngine.Track) or nameof(AnalyticsEngine.Dispose))))
    {
      var parameters = Parameters(method);
      if (parameters.Contains(typeof(CancellationToken)) && !method.Name.EndsWith("Async", StringComparison.Ordinal) &&
          !methods.Any(x => x.Name == method.Name + "Async" && Parameters(x).SequenceEqual(parameters)))
        problems.Add($"{method.Name} has no {method.Name}Async with the same parameters.");
      if ((method.Name.EndsWith("Async", StringComparison.Ordinal) || parameters.Contains(typeof(CancellationToken))) &&
          method.Name != nameof(AnalyticsEngine.DisposeAsync) && parameters.LastOrDefault() != typeof(CancellationToken))
        problems.Add($"{method.Name} does not take a CancellationToken last.");
    }
    Assert.That(problems, Is.Empty);
  }

  [Test]
  public void ExceptionsShareABaseAndHaveTheStandardConstructors()
  {
    var exceptions = PublicTypes.Where(x => typeof(Exception).IsAssignableFrom(x)).ToList();
    Assert.That(exceptions, Is.Not.Empty);
    Assert.Multiple(() =>
    {
      foreach (var exception in exceptions)
      {
        Assert.That(typeof(AnalyticsException).IsAssignableFrom(exception), Is.True, exception.Name);
        Assert.That(exception.GetConstructor(Type.EmptyTypes), Is.Not.Null, exception.Name + "()");
        Assert.That(exception.GetConstructor([typeof(string)]), Is.Not.Null, exception.Name + "(string)");
        Assert.That(exception.GetConstructor([typeof(string), typeof(Exception)]), Is.Not.Null, exception.Name + "(string, Exception)");
      }
      // A timeout is a query failure, so one catch handles both.
      Assert.That(new AnalyticsQueryTimeoutException(TimeSpan.FromSeconds(1)), Is.InstanceOf<AnalyticsQueryException>());
      Assert.That(new AnalyticsQueryTimeoutException(TimeSpan.FromSeconds(2)).Timeout, Is.EqualTo(TimeSpan.FromSeconds(2)));
    });
  }

  [Test]
  public void ExceptionConstructorsKeepMessageAndCause()
  {
    var cause = new IOException("disk full");
    Assert.Multiple(() =>
    {
      foreach (var type in PublicTypes.Where(x => typeof(Exception).IsAssignableFrom(x)))
      {
        var wrapped = (Exception)Activator.CreateInstance(type, "query failed", cause);
        Assert.That(wrapped.Message, Is.EqualTo("query failed"), type.Name);
        Assert.That(wrapped.InnerException, Is.SameAs(cause), type.Name);
        Assert.That(((Exception)Activator.CreateInstance(type)).Message, Is.Not.Empty, type.Name);
        Assert.That(((Exception)Activator.CreateInstance(type, "only a message")).InnerException, Is.Null, type.Name);
      }
      Assert.That(new AnalyticsQueryTimeoutException("no timeout given").Timeout, Is.Null);
      Assert.That(new AnalyticsQueryTimeoutException(TimeSpan.FromSeconds(30)).Message, Does.Contain("30 seconds"));
    });
  }

  /// <summary>
  /// Option groups and collections can be set in place but not replaced, so they are never
  /// null. Allow-lists are the exception: null means "no restriction".
  /// </summary>
  [Test]
  public void OptionGroupsAndCollectionsAreNeverReplaced()
  {
    var replaceable = new HashSet<string>(StringComparer.Ordinal)
    {
      nameof(PrivacyOptions) + "." + nameof(PrivacyOptions.AllowedDimensions),
      nameof(PrivacyOptions) + "." + nameof(PrivacyOptions.VisitorIdSecret),
    };
    var problems = new List<string>();
    foreach (var type in PublicTypes.Where(x => x.Name.EndsWith("Options", StringComparison.Ordinal) && x.IsClass))
    {
      var instance = Activator.CreateInstance(type);
      foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
      {
        var propertyType = property.PropertyType;
        var isGroup = propertyType.Name.EndsWith("Options", StringComparison.Ordinal) && propertyType.IsClass;
        var isCollection = propertyType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(propertyType);
        if (!isGroup && !isCollection)
          continue;
        var name = type.Name + "." + property.Name;
        if (replaceable.Contains(name))
          continue;
        if (property.SetMethod?.IsPublic == true)
          problems.Add(name + " has a public setter.");
        if (property.GetValue(instance) == null)
          problems.Add(name + " is null by default.");
      }
    }
    Assert.That(problems, Is.Empty);
  }

  [Test]
  public void OperatorsShareTheirNames()
  {
    var filterOperators = Enum.GetNames<FilterOperator>();
    Assert.That(filterOperators, Is.SupersetOf(Enum.GetNames<ComparisonOperator>()));
  }

  [Test]
  public void TheEngineIsATracker() =>
      Assert.That(typeof(IAnalyticsTracker).IsAssignableFrom(typeof(AnalyticsEngine)), Is.True);
}
