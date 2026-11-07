using System.Reflection;
using FanControl.Domain.Engine;

namespace FanControl.MCP.Engine;

/// <summary>
/// Finds FanControl's running <see cref="IFanControlEngine"/> from inside a plugin.
///
/// The public plugin API does not hand out the engine, but the object graph links to it:
/// our IPlugin3.RefreshRequested event is subscribed by FanControl's PluginBackendProvider, whose
/// own RefreshRequested event is subscribed by the ComputerAccessLayer, whose Initialized event is
/// subscribed by the FanControlEngine. This works in desktop-app and in Windows-service mode.
/// As a fallback the WPF Application object (desktop mode) is searched as well.
///
/// The search is a bounded breadth-first walk that only follows fields of FanControl's own types
/// and delegates, so it stays cheap and does not wander into WPF or Autofac internals.
/// </summary>
internal static class EngineLocator
{
    private const int MaxDepth = 7;
    private const int MaxVisited = 5000;
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static IFanControlEngine? Find(IEnumerable<object?> roots)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<(object Node, int Depth)>();
        foreach (var root in roots)
            if (root is not null) queue.Enqueue((root, 0));

        while (queue.Count > 0 && visited.Count < MaxVisited)
        {
            var (node, depth) = queue.Dequeue();
            if (!visited.Add(node)) continue;
            if (node is IFanControlEngine engine) return engine;
            if (depth >= MaxDepth) continue;

            if (node is Delegate d)
            {
                foreach (var inv in d.GetInvocationList())
                    if (inv.Target is not null) queue.Enqueue((inv.Target, depth + 1));
                continue;
            }

            for (var type = node.GetType(); type is not null && type != typeof(object); type = type.BaseType)
            {
                foreach (var field in type.GetFields(InstanceFields | BindingFlags.DeclaredOnly))
                {
                    if (field.FieldType.IsValueType || field.FieldType == typeof(string)) continue;
                    object? value;
                    try { value = field.GetValue(node); } catch { continue; }
                    if (value is null) continue;
                    if (value is Delegate || IsFanControlType(value.GetType()))
                        queue.Enqueue((value, depth + 1));
                }
            }
        }
        return null;
    }

    /// <summary>The WPF application object when FanControl runs as desktop app; null in service mode.</summary>
    public static object? GetWpfApplication()
    {
        try
        {
            var appType = Type.GetType("System.Windows.Application, PresentationFramework", throwOnError: false);
            return appType?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsFanControlType(Type t)
    {
        var ns = t.Namespace;
        if (ns is null) return t.Name.Contains("DisplayClass", StringComparison.Ordinal); // compiler closures
        if (ns.StartsWith("FanControl.MCP", StringComparison.Ordinal)) return true; // our own plugin object
        return ns.StartsWith("FanControl", StringComparison.Ordinal) && !ns.StartsWith("FanControl.ViewModels", StringComparison.Ordinal)
               && !ns.StartsWith("FanControl.Views", StringComparison.Ordinal)
               || t.FullName?.Contains("+<>c__DisplayClass", StringComparison.Ordinal) == true;
    }
}
