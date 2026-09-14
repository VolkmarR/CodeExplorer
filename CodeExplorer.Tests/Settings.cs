using Microsoft.Extensions.Configuration;

namespace CodeExplorer.Tests;

/// <summary>
///     Configuration built from a dictionary, for the classes that test a component's reading of it
///     rather than the running server's behaviour. Three test classes had spelled this out
///     identically; the fourth would have made it the convention.
/// </summary>
public static class Settings
{
    public static IConfiguration Of(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
