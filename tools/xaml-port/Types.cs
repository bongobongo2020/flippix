using System.Collections;
using System.Reflection;
using System.Runtime.Loader;

namespace XamlPort;

/// <summary>
/// Reflection over the built FlipPix.UI.Linux assembly, so the converter knows the view-model type
/// behind every DataContext and can give Avalonia's compiled bindings an x:DataType.
/// </summary>
public sealed class Types
{
    private readonly Assembly _asm;
    private readonly AssemblyLoadContext _alc;

    public Types(string assemblyPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!;
        _alc = new AssemblyLoadContext("port", isCollectible: false);
        _alc.Resolving += (ctx, name) =>
        {
            var candidate = Path.Combine(dir, name.Name + ".dll");
            return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
        };
        _asm = _alc.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
    }

    public Type? Find(string fullName)
    {
        var t = _asm.GetType(fullName);
        if (t != null) return t;
        foreach (var name in _asm.GetReferencedAssemblies())
        {
            if (!name.Name!.StartsWith("FlipPix")) continue;
            try
            {
                t = _alc.LoadFromAssemblyName(name).GetType(fullName);
                if (t != null) return t;
            }
            catch { }
        }
        return null;
    }

    /// <summary>Resolves a binding path ("A.B[0].C") against a type. Null when any step is unknown.</summary>
    public Type? ResolvePath(Type? start, string? path, out string? error)
    {
        error = null;
        if (start == null) return null;
        if (string.IsNullOrEmpty(path) || path == ".") return start;

        var t = start;
        foreach (var rawSegment in SplitPath(path))
        {
            var segment = rawSegment;
            if (segment.StartsWith("!")) segment = segment.TrimStart('!');
            var indexers = new List<string>();
            var bracket = segment.IndexOf('[');
            if (bracket >= 0)
            {
                indexers.AddRange(segment[bracket..].Split('[', StringSplitOptions.RemoveEmptyEntries).Select(x => x.TrimEnd(']')));
                segment = segment[..bracket];
            }

            if (segment.Length > 0)
            {
                if (segment.StartsWith("(")) return null; // attached property path
                var prop = FindProperty(t!, segment);
                if (prop == null)
                {
                    error = $"'{segment}' not found on {t!.FullName}";
                    return null;
                }
                t = prop.PropertyType;
            }

            foreach (var _ in indexers)
            {
                var item = ElementType(t!) ?? IndexerType(t!);
                if (item == null) return null;
                t = item;
            }
        }
        return t;
    }

    private static IEnumerable<string> SplitPath(string path)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] == '(' || path[i] == '[') depth++;
            else if (path[i] == ')' || path[i] == ']') depth--;
            else if (path[i] == '.' && depth == 0)
            {
                yield return path[start..i];
                start = i + 1;
            }
        }
        yield return path[start..];
    }

    public static PropertyInfo? FindProperty(Type t, string name)
    {
        for (var cur = t; cur != null; cur = cur.BaseType)
        {
            var p = cur.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly | BindingFlags.Static);
            if (p != null) return p;
        }
        if (t.IsInterface)
            foreach (var i in t.GetInterfaces())
            {
                var p = i.GetProperty(name);
                if (p != null) return p;
            }
        return null;
    }

    private static Type? IndexerType(Type t) =>
        t.GetProperties().FirstOrDefault(p => p.GetIndexParameters().Length == 1)?.PropertyType;

    /// <summary>T for an IEnumerable&lt;T&gt; (ObservableCollection, List, arrays...).</summary>
    public static Type? ElementType(Type t)
    {
        if (t.IsArray) return t.GetElementType();
        if (t == typeof(string)) return null;
        var ienum = t.IsGenericType && t.GetGenericTypeDefinition().FullName == "System.Collections.Generic.IEnumerable`1"
            ? t
            : t.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition().FullName == "System.Collections.Generic.IEnumerable`1");
        return ienum?.GetGenericArguments()[0];
    }

    /// <summary>Whether a type can be named in XAML as x:DataType (public, non-generic, not nested-private).</summary>
    public static bool IsNameable(Type t) =>
        !t.IsGenericType && !t.IsGenericParameter && (t.IsPublic || t.IsNestedPublic) && t != typeof(object)
        && !t.IsInterface;
}
