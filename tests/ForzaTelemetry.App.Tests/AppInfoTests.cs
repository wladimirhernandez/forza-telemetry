using System.Reflection;
using System.Text.RegularExpressions;

namespace ForzaTelemetry.App.Tests;

/// <summary>The version the About card and the log quote must be the one stamped on the assembly, never a literal in code.</summary>
public sealed partial class AppInfoTests
{
    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$")]
    private static partial Regex Semver();

    [Fact]
    public void The_version_is_the_assemblys_own_without_build_metadata()
    {
        var stamped = typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        Assert.Matches(Semver(), AppInfo.Version);
        Assert.StartsWith(AppInfo.Version, stamped, StringComparison.Ordinal); // "0.1.0+abc123" reads as "0.1.0"
        Assert.DoesNotContain('+', AppInfo.Version);
    }

    [Fact]
    public void The_product_name_comes_from_the_resources_so_a_rename_is_one_edit()
    {
        Assert.Equal(Resources.Strings.AppTitle, AppInfo.Name);
    }
}
