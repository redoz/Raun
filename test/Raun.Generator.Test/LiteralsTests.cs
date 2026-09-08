using Raun.Generator.Syntax;
using Xunit;

namespace Raun.Generator.Test;

/// <summary>One shared <see cref="Literals.Num"/> renders every int the generator emits — including
/// the negative ones the LINQ unroll can substitute, where a bare <c>Literal(value)</c> would be a
/// malformed token and a naive negation would overflow.</summary>
public class LiteralsTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(42, "42")]
    [InlineData(-1, "-1")]
    [InlineData(int.MinValue, "-2147483648")]
    public void An_int_renders_as_C_sharp_spells_it(int value, string expected)
        => Assert.Equal(expected, Literals.Num(value).ToString());
}
