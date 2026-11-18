namespace Raun.Mtp.Test;

/// <summary>
/// For hand-built definitions in tests: the namespace and type a dotted method name reads as. The
/// generator states both from symbols; a test that only has a name spells them out with this.
/// </summary>
internal static class DeclaringType
{
    public static string NamespaceOf(string methodFullName) => Parts(methodFullName).Namespace;

    public static string NameOf(string methodFullName) => Parts(methodFullName).Type;

    private static (string Namespace, string Type) Parts(string methodFullName)
    {
        var method = methodFullName.LastIndexOf('.');
        if (method < 0)
        {
            return ("", "");
        }

        var declaring = methodFullName[..method];
        var type = declaring.LastIndexOf('.');
        return type < 0 ? ("", declaring) : (declaring[..type], declaring[(type + 1)..]);
    }
}
