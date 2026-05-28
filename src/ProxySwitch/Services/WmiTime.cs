using System.Globalization;
using System.Management;

namespace ProxySwitch.Services;

internal static class WmiTime
{
    public static DateTime? ParseDmtfDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        try
        {
            return ManagementDateTimeConverter.ToDateTime(value);
        }
        catch
        {
            if (value.Length >= 14 &&
                DateTime.TryParseExact(
                    value[..14],
                    "yyyyMMddHHmmss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out var fallback))
            {
                return fallback;
            }

            return null;
        }
    }

    public static string Describe(DateTime? value)
    {
        if (!value.HasValue) return "createdAt=null fileTimeUtc=null";
        return $"createdAt={value.Value:O} fileTimeUtc={value.Value.ToFileTimeUtc()}";
    }
}
