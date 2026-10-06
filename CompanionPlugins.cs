using System.Reflection;
using Dalamud.Plugin;

namespace SRankSentinel;

internal sealed record CompanionPlugin(string Name, string[] InternalNames, string Role, string Setup);

internal static class CompanionPlugins
{
    internal static readonly CompanionPlugin[] All =
    [
        new("vnavmesh", ["vnavmesh"], "Required for automatic movement",
            "Enable vnavmesh. SRankSentinel requests navigation when needed."),
        new("Lifestream", ["Lifestream"], "Travel companion",
            "Enable if you use it. SRankSentinel currently uses native World Visit and teleport."),
        new("HuntAlerts", ["HuntAlerts"], "S-rank alert provider",
            "Enable S-rank announcements for your data center. Keep at least one alert provider enabled."),
        new("Sonar", ["SonarPlugin", "Sonar"], "S-rank alert provider",
            "Enable S-rank chat announcements and map links for your data center."),
    ];

    internal static bool IsSupported(string name) => All.Any(plugin =>
        plugin.InternalNames.Contains(name, StringComparer.Ordinal));

    internal static IExposedPlugin? Find(IEnumerable<IExposedPlugin> installed, CompanionPlugin companion) =>
        installed.FirstOrDefault(plugin => companion.InternalNames.Contains(plugin.InternalName, StringComparer.Ordinal));
}

/// <summary>
/// Isolated, fail-closed API-15 compatibility bridge. Dalamud's public IExposedPlugin has no enable API.
/// Mirrors the installer's single-plugin default-profile operation; never changes other collections.
/// </summary>
internal static class CompanionPluginActivation
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static string? UnavailableReason(IExposedPlugin plugin)
    {
        if (!CompanionPlugins.IsSupported(plugin.InternalName)) return "Unsupported companion plugin.";
        if (plugin.IsLoaded) return "Already enabled.";
        if (plugin.IsOutdated) return "Update this plugin in the installer first.";
        if (plugin.IsBanned || plugin.IsOrphaned || plugin.IsDecommissioned) return "This plugin is unavailable in the installer.";
        try
        {
            _ = Resolve(plugin);
            return null;
        }
        catch (Exception exception) { return Detail(exception); }
    }

    internal static async Task EnableAsync(IExposedPlugin plugin)
    {
        var reason = UnavailableReason(plugin);
        if (reason is not null) throw new InvalidOperationException(reason);
        var (local, profile, id, update, load) = Resolve(plugin);
        // Same ordering and apply=false as Dalamud's native installer. No blocking on the draw thread.
        await (Task)update.Invoke(profile, [id, plugin.InternalName, true, false])!;
        var loadReason = Enum.Parse(load.GetParameters()[0].ParameterType, "Installer");
        await (Task)load.Invoke(local, [loadReason, false, CancellationToken.None])!;
        if (!plugin.IsLoaded) throw new InvalidOperationException("The plugin did not finish enabling. Open the installer for details.");
    }

    private static (object Local, object Profile, Guid Id, MethodInfo Update, MethodInfo Load) Resolve(IExposedPlugin plugin)
    {
        if (plugin.GetType().FullName != "Dalamud.Plugin.ExposedPlugin" ||
            plugin.GetType().Assembly != typeof(IDalamudPluginInterface).Assembly)
            throw new InvalidOperationException("Direct enabling is unavailable on this Dalamud build. Use the installer.");
        var field = plugin.GetType().GetFields(Instance).SingleOrDefault(field =>
            field.FieldType.FullName == "Dalamud.Plugin.Internal.Types.LocalPlugin");
        var local = field?.GetValue(plugin) ?? throw new InvalidOperationException("Open the installer to enable this plugin.");
        var manager = GetService(plugin.GetType().Assembly, "Dalamud.Plugin.Internal.PluginManager");
        if ((bool)Property(manager, "SafeMode")) throw new InvalidOperationException("Dalamud is in safe mode.");
        var profiles = GetService(plugin.GetType().Assembly, "Dalamud.Plugin.Internal.Profiles.ProfileManager");
        if ((bool)Property(profiles, "IsBusy")) throw new InvalidOperationException("Dalamud is applying plugin collections. Try again shortly.");
        if (!(bool)Property(local, "IsInDefaultProfile"))
            throw new InvalidOperationException("Managed by a plugin collection. Enable its collection in the installer.");
        if (Property(local, "State").ToString() is not ("Unloaded" or "LoadError"))
            throw new InvalidOperationException("Plugin is busy or needs a Dalamud restart. Open the installer.");
        if ((bool)Property(Property(local, "Manifest"), "ScheduledForDeletion"))
            throw new InvalidOperationException("Plugin is scheduled for removal. Open the installer.");
        var profile = Property(profiles, "DefaultProfile");
        var id = (Guid)Property(local, "EffectiveWorkingPluginId");
        var update = profile.GetType().GetMethod("AddOrUpdateAsync", Instance, [typeof(Guid), typeof(string), typeof(bool), typeof(bool)]);
        var load = local.GetType().GetMethods(Instance).SingleOrDefault(method => method.Name == "LoadAsync" &&
            method.GetParameters() is { Length: 3 } parameters && parameters[0].ParameterType.IsEnum &&
            parameters[1].ParameterType == typeof(bool) && parameters[2].ParameterType == typeof(CancellationToken));
        if (update is null || load is null || id == Guid.Empty)
            throw new InvalidOperationException("Direct enabling is unavailable on this Dalamud build. Use the installer.");
        return (local, profile, id, update, load);
    }

    private static object GetService(Assembly assembly, string typeName)
    {
        var type = assembly.GetType(typeName, true)!;
        var service = assembly.GetType("Dalamud.Service`1", true)!.MakeGenericType(type);
        var get = service.GetMethod("Get", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            Type.EmptyTypes) ?? throw new MissingMethodException("Dalamud service contract changed.");
        return get.Invoke(null, null) ?? throw new InvalidOperationException("Dalamud is not ready.");
    }

    private static object Property(object target, string name) => target.GetType().GetProperty(name, Instance)?.GetValue(target)
        ?? throw new InvalidOperationException("Direct enabling is unavailable on this Dalamud build. Use the installer.");

    internal static string Detail(Exception exception) => exception is TargetInvocationException { InnerException: { } inner }
        ? Detail(inner) : exception.Message;
}
