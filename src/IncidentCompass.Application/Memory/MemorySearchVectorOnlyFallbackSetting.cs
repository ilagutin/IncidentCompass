namespace IncidentCompass.Application.Memory;

/// <summary>
/// The configured spelling of <see cref="MemorySearchVectorOnlyFallback" />, in one place so no other
/// file repeats the literals. The key is optional: a configuration that does not set it keeps its
/// content and therefore its configuration hash, and resolves to <see cref="Default" />.
/// </summary>
internal static class MemorySearchVectorOnlyFallbackSetting
{
    public const string SettingName = "VectorOnlyFallback";

    public const string Off = "off";

    public const string ForeignScript = "foreign_script";

    public const string Always = "always";

    public static readonly IReadOnlyList<string> KnownValues = [Off, ForeignScript, Always];

    public const MemorySearchVectorOnlyFallback Default = MemorySearchVectorOnlyFallback.ForeignScript;

    public static bool TryParse(string? value, out MemorySearchVectorOnlyFallback fallback)
    {
        switch (value)
        {
            case Off:
                fallback = MemorySearchVectorOnlyFallback.Off;
                return true;
            case ForeignScript:
                fallback = MemorySearchVectorOnlyFallback.ForeignScript;
                return true;
            case Always:
                fallback = MemorySearchVectorOnlyFallback.Always;
                return true;
            default:
                fallback = Default;
                return false;
        }
    }

    /// <summary>
    /// Configuration load already refuses an unknown value, so reaching the throw means a snapshot was
    /// written by a loader that did not check it; failing closed is better than silently guessing.
    /// </summary>
    public static MemorySearchVectorOnlyFallback Resolve(string? value)
    {
        if (value is null)
        {
            return Default;
        }

        if (TryParse(value, out var fallback))
        {
            return fallback;
        }

        throw new InvalidOperationException(
            "Tools.memory_search." + SettingName + " is '" + value + "'; the accepted values are " +
            string.Join(", ", KnownValues) + ".");
    }
}
