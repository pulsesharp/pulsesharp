using System.Reflection;

namespace PulseSharp.Naming;

internal static class HandlerNames
{
    public static string NameOf(Type handlerType)
    {
        return handlerType.GetCustomAttribute<HandlerNameAttribute>()?.Name ?? handlerType.Name;
    }
}
