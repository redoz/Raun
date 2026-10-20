using Raun;

// Every scenario in this assembly gets suite=appointments; classes and methods add their own.
// Select with --treenode-filter, e.g. "/**[area=users]" or "/**[(area=booking)&(tier!=smoke)]".
[assembly: Tag("suite", "appointments")]

namespace AppointmentTests;

/// <summary>
/// A named set of tags: applying <c>[UserImport]</c> applies every <c>[Tag]</c> on this class. The
/// generator reads them at compile time, so the attribute needs no body and works from a shared
/// library just the same.
/// </summary>
[Tag("area", "users")]
[Tag("owner", "identity")]
[AttributeUsage(AttributeTargets.Method)]
public sealed class UserImportAttribute : Attribute;
