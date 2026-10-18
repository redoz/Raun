namespace Raun;

/// <summary>Well-known source tokens for explicit resource links.</summary>
public static class Subject
{
    /// <summary>
    /// Select the step's return value as the source of a raw resource link.
    /// Use this token in LinkAttribute.Source when the owning action applies to the return value.
    /// A parameter cannot have this name. Keep this value identical to
    /// Raun.Generator.Lowering.AttributeReader.ReturnSubject in the compiler assembly.
    /// </summary>
    public const string Return = "<return>";
}
