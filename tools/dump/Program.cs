using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

// Offline inspector for the game's Assembly-CSharp.dll.
//   dump <ManagedDir> list [regex]   type names (+ base type), optionally filtered
//   dump <ManagedDir> show <regex>   fields and methods of every matching type
static class Program
{
    static void Main(string[] a)
    {
        string managed = a[0];
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string p = Path.Combine(managed, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };
        Assembly asm = Assembly.LoadFrom(Path.Combine(managed, "Assembly-CSharp.dll"));
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }

        Regex rx = new Regex(a.Length > 2 ? a[2] : ".", RegexOptions.IgnoreCase);
        foreach (Type t in types.Where(t => rx.IsMatch(t.Name)).OrderBy(t => t.FullName))
        {
            if (a[1] == "list") Console.WriteLine(t.FullName + (t.BaseType != null ? " : " + t.BaseType.Name : ""));
            else Show(t);
        }
    }

    static string Safe(Func<string> f) { try { return f(); } catch { return "?"; } }

    static void Show(Type t)
    {
        Console.WriteLine("=== " + t.FullName + " : " + t.BaseType?.Name);
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (FieldInfo f in t.GetFields(F))
            Console.WriteLine("  F " + (f.IsStatic ? "static " : "") + Safe(() => f.FieldType.Name) + " " + f.Name);
        foreach (MethodInfo m in t.GetMethods(F))
            Console.WriteLine("  M " + (m.IsStatic ? "static " : "") + Safe(() => m.ReturnType.Name) + " " + m.Name + "(" +
                Safe(() => string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))) + ")");
    }
}
