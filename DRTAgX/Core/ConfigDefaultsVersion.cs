using System;
using System.Reflection;

// Domain folders intentionally share the mod's root namespace.
// ReSharper disable CheckNamespace
namespace DRTAgX;

/// <summary>Release stamp shared by the DRTAgX and curated Sheyder defaults.</summary>
internal static class ConfigDefaultsVersion
{
    // Read the SDK-generated package version once. Build metadata does not
    // change defaults; a release-version change deliberately resets old files.
    internal static string Current { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var assembly = typeof(ConfigDefaultsVersion).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString(3) ?? "2.1.1";
        var metadata = version.IndexOf('+');
        return metadata < 0 ? version : version[..metadata];
    }

    internal static bool Matches(string? saved) => string.Equals(saved, Current, StringComparison.Ordinal);

    // Matching releases retain the loaded object and all user choices. A missing
    // stamp must replace the complete object, rather than merge legacy values.
    internal static AgxConfig ResolveAgx(AgxConfig? saved) =>
        saved != null && Matches(saved.ConfigVersion) ? saved : CreateAgxDefaults();

    internal static AgxConfig CreateAgxDefaults() => new()
    {
        ConfigVersion = Current, AutoExposureMeterVersion = 1
    };
}