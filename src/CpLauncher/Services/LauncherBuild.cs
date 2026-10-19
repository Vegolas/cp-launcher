using System.Reflection;
using L2Mod.Core.Updates;

namespace CpLauncher.Services;

/// <summary>The version of this launcher build, as the updater compares it.</summary>
public static class LauncherBuild
{
    /// <summary>
    /// The informational version (<c>&lt;Version&gt;</c> in the project, e.g. 1.0.0, plus the SDK's
    /// <c>+&lt;commit&gt;</c>, which <see cref="LauncherVersion"/> ignores); the assembly version when that is not
    /// a valid version.
    /// </summary>
    public static LauncherVersion CurrentVersion
    {
        get
        {
            Assembly assembly = typeof(LauncherBuild).Assembly;
            AssemblyInformationalVersionAttribute? attribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (attribute is not null && LauncherVersion.TryParse(attribute.InformationalVersion, out LauncherVersion? version))
            {
                return version;
            }

            Version? assemblyVersion = assembly.GetName().Version;
            if (assemblyVersion is null)
            {
                return LauncherVersion.Parse("0.0.0");
            }

            return LauncherVersion.Parse($"{assemblyVersion.Major}.{assemblyVersion.Minor}.{Math.Max(assemblyVersion.Build, 0)}");
        }
    }
}
