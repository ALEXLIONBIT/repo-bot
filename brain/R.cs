using System;
using System.Collections.Generic;
using System.Reflection;

namespace RepoBot
{
    /// <summary>Field access by name, so we do not depend on the game's public/private choices.</summary>
    internal static class R
    {
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                                 BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        static readonly Dictionary<string, FieldInfo> cache = new Dictionary<string, FieldInfo>();

        static FieldInfo Find(Type t, string name)
        {
            string key = t.FullName + "." + name;
            FieldInfo f;
            if (cache.TryGetValue(key, out f)) return f;
            for (Type c = t; c != null && f == null; c = c.BaseType) f = c.GetField(name, All);
            cache[key] = f;
            return f;
        }

        public static T Get<T>(object o, string name, T fallback = default(T))
        {
            if (o == null) return fallback;
            FieldInfo f = Find(o.GetType(), name);
            if (f == null) return fallback;
            object v = f.GetValue(o);
            return v is T ? (T)v : fallback;
        }

        public static T GetStatic<T>(Type type, string name, T fallback = default(T))
        {
            FieldInfo f = Find(type, name);
            if (f == null) return fallback;
            object v = f.GetValue(null);
            return v is T ? (T)v : fallback;
        }

        public static bool Set(object o, string name, object value)
        {
            if (o == null) return false;
            FieldInfo f = Find(o.GetType(), name);
            if (f == null) return false;
            f.SetValue(o, value);
            return true;
        }

        /// <summary>Value of the first enum field whose enum type has this name (e.g. the "State" of an ExtractionPoint).</summary>
        public static string EnumField(object o, string enumTypeName)
        {
            if (o == null) return "?";
            string key = o.GetType().FullName + "#enum#" + enumTypeName;
            FieldInfo f;
            if (!cache.TryGetValue(key, out f))
            {
                for (Type c = o.GetType(); c != null && f == null; c = c.BaseType)
                    foreach (FieldInfo x in c.GetFields(All))
                        if (x.FieldType.IsEnum && x.FieldType.Name == enumTypeName) { f = x; break; }
                cache[key] = f;
            }
            return f == null ? "?" : f.GetValue(o).ToString();
        }
    }
}
