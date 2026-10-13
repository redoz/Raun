using Microsoft.CodeAnalysis;
using Raun.Coverage.Inventory;

namespace Raun.Coverage.MicrosoftXml;

internal sealed class CoverageSourceVerification
{
    private readonly Dictionary<(SyntaxTree Tree, CoverageSource Source), bool> _verified = [];

    public bool Verify(InventoryBranch branch, CoverageSource source)
    {
        var key = (branch.Syntax.SyntaxTree, source);
        if (!_verified.TryGetValue(key, out var verified))
        {
            verified = File.Exists(branch.Descriptor.Location.Path) && CoverageResolver.VerifySource(branch, source);
            _verified.Add(key, verified);
        }
        return verified;
    }
}
