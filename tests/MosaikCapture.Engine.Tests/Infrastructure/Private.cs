using System.Reflection;

namespace MosaikCapture.Engine.Tests.Infrastructure;

/// <summary>
/// Reflection access to private UI members for the pixel tests, which drive the windows the way a
/// user would (tool selection, strokes, hover) without showing them.
/// </summary>
internal static class Private
{
    const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
    const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

    public static T Field<T>(object target, string name) => (T)FieldInfo(target.GetType(), name).GetValue(target)!;

    public static void SetField(object target, string name, object? value) => FieldInfo(target.GetType(), name).SetValue(target, value);

    public static object? Call(object target, string name, params object?[] args) => Method(target.GetType(), name, args.Length, Instance | Static).Invoke(target, args);

    public static object Enum(Type owner, string nestedType, string value) =>
        System.Enum.Parse(owner.GetNestedType(nestedType, BindingFlags.NonPublic)!, value);

    static FieldInfo FieldInfo(Type type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var field = t.GetField(name, Instance);
            if (field != null) return field;
        }
        throw new MissingFieldException(type.Name, name);
    }

    static MethodInfo Method(Type type, string name, int parameters, BindingFlags flags) =>
        type.GetMethods(flags).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == parameters && m.DeclaringType == type)
        ?? throw new MissingMethodException(type.Name, name);
}
