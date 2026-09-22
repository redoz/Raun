using Microsoft.Testing.Extensions;
using Raun.Mtp;

namespace AppointmentTests;

/// <summary>
/// <para>
/// Hand-authored Microsoft.Testing.Platform entry point for the showcase. The generated entry point
/// is suppressed (<c>&lt;RaunGenerateProgram&gt;false&lt;/RaunGenerateProgram&gt;</c>) so this
/// <c>Main</c> can opt into Raun's deterministic simulated-time scheduler via
/// <c>simulateTime: true</c>. The Given/When/Then steps author their own durations with
/// <see cref="Raun.ScenarioContext.SimulateElapsed(System.TimeSpan)"/> (no real waiting), which
/// lands a realistic, overlapping timeline in the generated HTML report. Production projects omit
/// the flag
/// (default <see langword="false"/>) and run on real wall-clock timing.
/// </para>
/// <para>
/// It is also where Microsoft.Testing.Platform extensions are registered: the <c>configure</c>
/// callback hands out the platform's own builder, so <c>AddTrxReportProvider()</c> here is what
/// makes <c>--report-trx</c> a recognized option. The generated entry point calls
/// <c>RunAsync(args)</c> with no callback and therefore registers nothing — a project that wants an
/// extension writes this file, as documented in README's "Reports and extensions".
/// </para>
/// </summary>
internal static class Program
{
    private static Task<int> Main(string[] args)
        => RaunTestApplication.RunAsync(
            args,
            simulateTime: true,
            configure: builder => builder.AddTrxReportProvider());
}
