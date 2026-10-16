using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace MbaLms.Api.Infrastructure;

/// <summary>
/// Test-only HTTP fixture. Off by default. Ignored in Production even if the flag is set.
/// Not tied to the Development environment name.
/// </summary>
public static class StaffFixtureAvailability
{
    public const string ConfigKey = "Test:StaffFixture";

    public static bool IsEnabled(IHostEnvironment env, IConfiguration config) =>
        !env.IsProduction() && config.GetValue(ConfigKey, false);
}

public sealed class ExcludeTypeControllerFeatureProvider(Type type) : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        var match = feature.Controllers.FirstOrDefault(c => c.AsType() == type);
        if (match is not null) feature.Controllers.Remove(match);
    }
}
