using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Xunit;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Raun.Generator;
using Raun.Generator.Analysis;
using Raun.Model;
using Raun.Scheduling;

namespace Raun.Generator.Test;

/// <summary>
/// Drives the generator over input source, then compiles + loads the result so tests can assert on
/// the actual generated <see cref="ScenarioDefinition"/> and even run it through the real scheduler.
/// This makes generator tests behavioral, not just snapshot-based.
/// </summary>
public static class GeneratorHarness
{
    /// <summary>The framework plus <c>Raun</c> (DSL, <c>[Scenario]</c>) plus <c>Raun.Mtp</c> (the
    /// bootstrap the generated entry point calls) — what an MTP test project references.</summary>
    private static readonly ImmutableArray<MetadataReference> References = BuildReferences(withMtp: true);

    /// <summary>The framework plus <c>Raun</c> only — a project that authors scenarios without the
    /// MTP adapter, for which no entry point must be generated.</summary>
    private static readonly ImmutableArray<MetadataReference> CoreOnlyReferences = BuildReferences(withMtp: false);

    private static ImmutableArray<MetadataReference> BuildReferences(bool withMtp)
    {
        // The test process's own probing list also carries this project's references, Raun.Mtp
        // included; the core-only set has to leave that one out or the "without Raun.Mtp" path is
        // not one.
        var mtpAssemblyPath = typeof(Raun.Mtp.RaunTestApplication).Assembly.Location;
        var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        var refs = tpa.Split(Path.PathSeparator)
            .Where(p => p.Length > 0)
            .Where(p => withMtp || !string.Equals(p, mtpAssemblyPath, StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
        refs.Add(MetadataReference.CreateFromFile(typeof(Given).Assembly.Location));
        if (withMtp)
        {
            refs.Add(MetadataReference.CreateFromFile(typeof(Raun.Mtp.RaunTestApplication).Assembly.Location));
        }

        return refs.ToImmutableArray();
    }

    /// <summary>A compilation over one source file with the standard reference set, for tests that
    /// drive the driver themselves (incrementality) rather than taking a result.</summary>
    public static CSharpCompilation CompilationFor(string source, CSharpParseOptions parseOptions)
        => CSharpCompilation.Create(
            "Incremental",
            [CSharpSyntaxTree.ParseText(source, parseOptions, path: "")],
            References,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

    /// <summary>
    /// Compiles generated source as a console app against Raun.Mtp (so the emitted entry point binds
    /// to the real <c>RaunTestApplication.RunAsync</c> signature) and returns any compile errors.
    /// </summary>
    public static ImmutableArray<Diagnostic> CompileWithMtp(string generatedSource)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var refs = References.Add(
            MetadataReference.CreateFromFile(typeof(Raun.Mtp.RaunTestApplication).Assembly.Location));
        var compilation = CSharpCompilation.Create(
            "EntryPointCompile_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(generatedSource, parseOptions)],
            refs,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication,
                nullableContextOptions: NullableContextOptions.Enable));

        using var ms = new MemoryStream();
        return compilation.Emit(ms).Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();
    }

    public static GeneratorResult Run(string source, string assemblyName = "ScenarioTests")
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            assemblyName + "_" + Guid.NewGuid().ToString("N"),
            [tree],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        var driver = CSharpGeneratorDriver.Create(
            [new ScenarioGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var genDiagnostics);

        var generatedTrees = output.SyntaxTrees.Where(t => t != tree).ToList();
        var generatedSource = string.Join("\n\n", generatedTrees.Select(t => t.ToString()));

        var emitDiagnostics = ImmutableArray<Diagnostic>.Empty;
        Assembly? assembly = null;
        using var ms = new MemoryStream();
        var emit = output.Emit(ms);
        emitDiagnostics = emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
        if (emit.Success)
        {
            assembly = Assembly.Load(ms.ToArray());
        }

        return new GeneratorResult(genDiagnostics, emitDiagnostics, generatedSource, assembly);
    }

    /// <summary>Runs the generator over source and returns the driver, for Verify snapshots. By
    /// default the entry point is suppressed (<paramref name="generateProgram"/> = "false") so the
    /// lowering snapshots stay scoped to the scenario manifest; the entry point has its own snapshot.</summary>
    public static GeneratorDriver RunDriver(string source, string? path = null, string? generateProgram = "false")
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions, path: path ?? "");
        var compilation = CSharpCompilation.Create(
            "Snapshot",
            [tree],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        var optionsProvider = generateProgram is null
            ? null
            : new TestOptionsProvider(new Dictionary<string, string>
            {
                ["build_property.RaunGenerateProgram"] = generateProgram,
            });

        return CSharpGeneratorDriver
            .Create(
                [new ScenarioGenerator().AsSourceGenerator()],
                parseOptions: parseOptions,
                optionsProvider: optionsProvider)
            .RunGenerators(compilation);
    }

    /// <summary>Mirrors <see cref="Run"/> but parses the input with a real file path, so spans
    /// carry that path (the generator's span-directive branch only fires for path-bearing input).</summary>
    public static GeneratorResult RunWithPath(string source, string path, string assemblyName = "ScenarioTests")
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions, path: path);
        var compilation = CSharpCompilation.Create(
            assemblyName + "_" + Guid.NewGuid().ToString("N"),
            [tree],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        var driver = CSharpGeneratorDriver.Create(
            [new ScenarioGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var genDiagnostics);

        var generatedTrees = output.SyntaxTrees.Where(t => t != tree).ToList();
        var generatedSource = string.Join("\n\n", generatedTrees.Select(t => t.ToString()));

        Assembly? assembly = null;
        using var ms = new MemoryStream();
        var emit = output.Emit(ms);
        var emitDiagnostics = emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
        if (emit.Success)
        {
            assembly = Assembly.Load(ms.ToArray());
        }

        return new GeneratorResult(genDiagnostics, emitDiagnostics, generatedSource, assembly);
    }

    /// <summary>Runs the generator over path-bearing source and emits a portable PDB; returns emit
    /// errors and the raw PDB bytes for sequence-point inspection.</summary>
    public static (ImmutableArray<Diagnostic> Errors, ImmutableArray<byte> Pdb) EmitWithPdb(string source, string path)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions, path: path,
            encoding: System.Text.Encoding.UTF8);
        var compilation = CSharpCompilation.Create(
            "PdbSnapshot_" + Guid.NewGuid().ToString("N"),
            [tree],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        var driver = CSharpGeneratorDriver.Create(
            [new ScenarioGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);

        using var dll = new MemoryStream();
        using var pdbStream = new MemoryStream();
        var emit = output.Emit(dll, pdbStream: pdbStream,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
        var errors = emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray();
        return (errors, [.. pdbStream.ToArray()]);
    }

    /// <summary>One sequence point read from a portable PDB. Line/column are 1-based;
    /// <see cref="IsHidden"/> points have line 0xFEEFEE and no meaningful coordinates.</summary>
    public sealed record SeqPoint(string Document, bool IsHidden, int StartLine, int StartColumn, int EndLine, int EndColumn);

    public static IReadOnlyList<SeqPoint> ReadSequencePoints(ImmutableArray<byte> pdb)
    {
        using var stream = new MemoryStream([.. pdb]);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        var reader = provider.GetMetadataReader();

        var result = new List<SeqPoint>();
        foreach (var handle in reader.MethodDebugInformation)
        {
            var info = reader.GetMethodDebugInformation(handle);
            if (info.SequencePointsBlob.IsNil)
            {
                continue;
            }

            foreach (var sp in info.GetSequencePoints())
            {
                var doc = sp.Document.IsNil
                    ? ""
                    : reader.GetString(reader.GetDocument(sp.Document).Name);
                result.Add(new SeqPoint(doc, sp.IsHidden, sp.StartLine, sp.StartColumn, sp.EndLine, sp.EndColumn));
            }
        }

        return result;
    }

    /// <summary>
    /// Runs the generator over source and returns each generated file keyed by its hint name. When
    /// <paramref name="generateProgram"/> is non-null it is surfaced to the generator as the
    /// <c>build_property.RaunGenerateProgram</c> analyzer-config value (the MSBuild property a
    /// consuming project would set); null leaves the property unset so the generator sees its default.
    /// </summary>
    public static IReadOnlyDictionary<string, string> RunGeneratedFiles(string source, string? generateProgram = null, bool referenceMtp = true)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            "GenFiles_" + Guid.NewGuid().ToString("N"),
            [tree],
            referenceMtp ? References : CoreOnlyReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        var optionsProvider = generateProgram is null
            ? null
            : new TestOptionsProvider(new Dictionary<string, string>
            {
                ["build_property.RaunGenerateProgram"] = generateProgram,
            });

        var driver = CSharpGeneratorDriver.Create(
            [new ScenarioGenerator().AsSourceGenerator()],
            parseOptions: parseOptions,
            optionsProvider: optionsProvider);

        var result = driver.RunGenerators(compilation).GetRunResult();
        return result.Results
            .SelectMany(r => r.GeneratedSources)
            .ToDictionary(s => s.HintName, s => s.SourceText.ToString());
    }

    /// <summary>Runs the analyzer over source and returns just the Raun diagnostics.
    /// <para>
    /// When <paramref name="requireCompilable"/> is true, this first asserts the compilation is
    /// error-free. That check is opt-in, not automatic, because a Roslyn analyzer's job legitimately
    /// includes reporting on code that does not compile — much of the point of an analyzer is to give a
    /// clear diagnostic on broken code, so a test that feeds deliberately invalid source and asserts a
    /// diagnostic is present is exercising exactly that. Source that fails to compile does, however,
    /// produce no analyzer diagnostics at all, which would make an absence assertion
    /// (<c>Assert.DoesNotContain(diagnostics, ...)</c> / <c>Assert.Empty(diagnostics)</c>) pass for free
    /// regardless of what the analyzer actually does — so only a test that asserts a diagnostic is
    /// absent should opt in, to keep a broken test source from silently making that assertion vacuous.
    /// </para></summary>
    public static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, bool requireCompilable = false)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var compilation = CSharpCompilation.Create(
            "Analyze_" + Guid.NewGuid().ToString("N"),
            [tree],
            References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        if (requireCompilable)
        {
            var compileErrors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToImmutableArray();
            Assert.True(
                compileErrors.IsEmpty,
                "test source did not compile: " + string.Join("; ", compileErrors.Take(5).Select(d => d.ToString())));
        }

        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new ScenarioAnalyzer()));
        var diagnostics = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        return diagnostics.Where(d => d.Id.StartsWith("RAUN")).ToImmutableArray();
    }

    public static IReadOnlyList<ScenarioDefinition> Definitions(this GeneratorResult result)
    {
        Assert.NotNull(result.Assembly);
        var type = result.Assembly!.GetType("Raun.Generated.RaunGenerated");
        Assert.NotNull(type);
        var method = type!.GetMethod("CreateAll", BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);
        return (IReadOnlyList<ScenarioDefinition>)method!.Invoke(null, null)!;
    }

    public static Task<IReadOnlyList<StepResult>> RunAsync(this ScenarioDefinition definition, int maxParallelism = 0)
        => new ScenarioScheduler(maxParallelism).RunAsync(definition);
}

/// <summary>An <see cref="AnalyzerConfigOptionsProvider"/> whose global options are a fixed map —
/// mirrors how MSBuild surfaces <c>&lt;CompilerVisibleProperty&gt;</c> values to a generator.</summary>
file sealed class TestOptionsProvider(IReadOnlyDictionary<string, string> globals) : AnalyzerConfigOptionsProvider
{
    public override AnalyzerConfigOptions GlobalOptions { get; } = new TestOptions(globals);

    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;

    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;

    private sealed class TestOptions(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
            => values.TryGetValue(key, out value);
    }
}

public sealed record GeneratorResult(
    ImmutableArray<Diagnostic> GeneratorDiagnostics,
    ImmutableArray<Diagnostic> EmitDiagnostics,
    string GeneratedSource,
    Assembly? Assembly)
{
    public void AssertCompiles()
    {
        Assert.True(
            GeneratorDiagnostics.IsEmpty,
            "generator diagnostics: " + string.Join("; ", GeneratorDiagnostics.Select(d => d.ToString())));
        Assert.True(
            EmitDiagnostics.IsEmpty,
            "generated code did not compile: " + string.Join("; ", EmitDiagnostics.Select(d => d.ToString())));
        Assert.NotNull(Assembly);
    }
}
