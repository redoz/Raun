using Xunit;

namespace Raun.Mtp.Test;

/// <summary>The platform-side selector: adapts a <c>TreeNodeFilter</c> to Raun's selection.</summary>
public class TreeNodeSelectorTests
{
    // TreeNodeFilter's constructor is internal to the platform, so the only way to obtain an
    // instance is from the platform itself (proven end to end in the sample runs, not here). The
    // guard clause below is the one behaviour TreeNodeSelector has that does not need an instance.
    [Fact]
    public void A_tree_node_selector_rejects_a_null_filter()
        => Assert.Throws<ArgumentNullException>(() => new TreeNodeSelector(null!));
}
