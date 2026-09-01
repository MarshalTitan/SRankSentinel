using System.Globalization;
using System.Reflection;

namespace SRankSentinel;

internal static class ReflectionReader
{
    public static T? Read<T>(object source, string name)
    {
        var type = source.GetType();
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
        var value = field?.GetValue(source) ?? property?.GetValue(source);
        if (value is null)
            return default;

        if (value is T exact)
            return exact;

        try
        {
            var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            return (T?)Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
        }
        catch
        {
            return default;
        }
    }
}
