using System;
using System.Collections.Generic;
using System.Reflection;

namespace WikiBook;

/// <summary>
///     The only place allowed to reflect over game members that are not public in the shipped assembly. A member
///     missing from an unfamiliar game version is logged once and then ignored, so the mod degrades instead of
///     throwing on every frame.
/// </summary>
internal static class GameAccess
{
    private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.Ordinal);

    internal static FieldInfo Field(Type type, string name, bool isStatic = false)
    {
        if (type == null || string.IsNullOrEmpty(name))
        {
            return null;
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        var field = type.GetField(name, flags);
        if (field == null)
        {
            ReportOnce(type, "field", name);
            return null;
        }

        if (isStatic != field.IsStatic)
        {
            ReportOnce(type, isStatic ? "static field" : "instance field", name);
            return null;
        }

        return field;
    }

    internal static MethodInfo Method(Type type, string name, params Type[] parameters)
    {
        if (type == null || string.IsNullOrEmpty(name))
        {
            return null;
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                   BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        var method = parameters == null || parameters.Length == 0
            ? type.GetMethod(name, flags)
            : type.GetMethod(name, flags, null, parameters, null);
        if (method == null)
        {
            ReportOnce(type, "method", name);
        }

        return method;
    }

    internal static object Get(FieldInfo field, object instance, object fallback = null)
    {
        if (field == null)
        {
            return fallback;
        }

        try
        {
            return field.GetValue(instance);
        }
        catch (Exception exception)
        {
            ReportOnce(field.DeclaringType, "field read", field.Name, exception.Message);
            return fallback;
        }
    }

    internal static void Set(FieldInfo field, object instance, object value)
    {
        if (field == null)
        {
            return;
        }

        try
        {
            field.SetValue(instance, value);
        }
        catch (Exception exception)
        {
            ReportOnce(field.DeclaringType, "field write", field.Name, exception.Message);
        }
    }

    internal static object Call(MethodInfo method, object instance, object[] arguments, object fallback = null)
    {
        if (method == null)
        {
            return fallback;
        }

        try
        {
            return method.Invoke(instance, arguments);
        }
        catch (Exception exception)
        {
            var inner = exception is TargetInvocationException && exception.InnerException != null
                ? exception.InnerException
                : exception;
            ReportOnce(method.DeclaringType, "method call", method.Name, inner.Message);
            return fallback;
        }
    }

    private static void ReportOnce(Type type, string kind, string member, string detail = null)
    {
        var key = (type != null ? type.FullName : "?") + "." + member + ":" + kind;
        if (!Reported.Add(key))
        {
            return;
        }

        var message = "Game API not found: " + kind + " " + (type != null ? type.FullName : "?") + "." + member;
        if (!string.IsNullOrEmpty(detail))
        {
            message += " (" + detail + ")";
        }

        var log = Plugin.Log;
        if (log != null)
        {
            log.LogWarning(message);
        }
    }
}
