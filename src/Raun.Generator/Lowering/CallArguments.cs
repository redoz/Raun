using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Raun.Generator.Lowering;

/// <summary>How a call's written arguments bind to its method's parameters.</summary>
internal static class CallArguments
{
    /// <summary>
    /// The index of the argument bound to the parameter at <paramref name="position"/>: a named
    /// argument (<c>name: value</c>) matching <paramref name="parameterName"/> if present, else the
    /// positional argument at that index. -1 when neither exists (e.g. an omitted optional parameter).
    /// </summary>
    public static int IndexOf(SeparatedSyntaxList<ArgumentSyntax> arguments, string parameterName, int position)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i].NameColon?.Name.Identifier.Text == parameterName)
            {
                return i;
            }
        }

        return position < arguments.Count && arguments[position].NameColon is null ? position : -1;
    }
}
