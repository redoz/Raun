using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace Raun.Testing;

/// <summary>
/// Snapshot assertions: compare a produced string against a committed <c>*.verified.*</c> file, and
/// leave a <c>*.received.*</c> beside it when they differ.
/// </summary>
/// <remarks>
/// <para>
/// The workflow is the familiar one — review the diff, then move the received file over the verified
/// one, or re-run with <c>RAUN_ACCEPT_SNAPSHOTS=1</c> to have that done for you. Received files are
/// git-ignored; verified files are committed and are the reviewable artifact.
/// </para>
/// <para>
/// Comparison ignores a UTF-8 BOM, normalizes CRLF to LF and ignores trailing newlines, so a
/// snapshot cannot fail over how an editor or a git checkout saved the file. Everything else —
/// every space of the generated output — is compared exactly, which is the point of these tests.
/// </para>
/// </remarks>
internal static class Snapshot
{
    /// <summary>Set this environment variable to 1 to overwrite the verified files instead of failing.</summary>
    public const string AcceptVariable = "RAUN_ACCEPT_SNAPSHOTS";

    /// <summary>
    /// Compares <paramref name="content"/> against the snapshot for the calling test.
    /// </summary>
    /// <param name="content">What the code under test produced.</param>
    /// <param name="extension">The snapshot file's extension, without a dot.</param>
    /// <param name="directory">A subdirectory of the test's own source directory, or null for beside it.</param>
    /// <param name="nameSuffix">Appended to the snapshot's name, for a test with more than one
    /// snapshot (the generator's per-file outputs use <c>#&lt;hint name&gt;</c>).</param>
    /// <param name="testName">The calling test method; supplied by the compiler.</param>
    /// <param name="sourceFile">The calling test's file, which names the snapshots and locates
    /// them; supplied by the compiler.</param>
    /// <returns>The path of the verified file this snapshot owns.</returns>
    public static string Verify(
        string content,
        string extension = "txt",
        string? directory = null,
        string? nameSuffix = null,
        [CallerMemberName] string testName = "",
        [CallerFilePath] string sourceFile = "")
    {
        var verified = PathFor(extension, directory, nameSuffix, testName, sourceFile);
        var received = verified.Replace(".verified.", ".received.", StringComparison.Ordinal);

        if (Environment.GetEnvironmentVariable(AcceptVariable) == "1")
        {
            Write(verified, content);
            Delete(received);
            return verified;
        }

        var actual = Normalize(content);
        if (File.Exists(verified) && Normalize(File.ReadAllText(verified)) == actual)
        {
            Delete(received);
            return verified;
        }

        Write(received, content);
        Assert.Fail(Describe(verified, received, actual));
        return verified;
    }

    /// <summary>The verified path a snapshot would take, without producing or comparing anything.
    /// Used to spot verified files nothing produces any more.</summary>
    public static string PathFor(
        string extension,
        string? directory,
        string? nameSuffix,
        string testName,
        string sourceFile)
    {
        if (sourceFile.StartsWith("/_/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{sourceFile}' is a deterministic (mapped) source path, so the snapshot cannot be located; "
                + "test projects must build with DeterministicSourcePaths=false (see Directory.Build.targets).");
        }

        var sourceDirectory = Path.GetDirectoryName(sourceFile)
            ?? throw new InvalidOperationException($"Could not take a directory from '{sourceFile}'.");
        var folder = directory is null ? sourceDirectory : Path.Combine(sourceDirectory, directory);
        var name = $"{Path.GetFileNameWithoutExtension(sourceFile)}.{testName}{nameSuffix}.verified.{extension}";
        return Path.Combine(folder, name);
    }

    private static string Describe(string verified, string received, string actual)
    {
        if (!File.Exists(verified))
        {
            return string.Join(
                "\n",
                $"No snapshot at '{verified}'.",
                $"The run wrote '{received}'; review it and move it over the verified name.");
        }

        var lines = new List<string>
        {
            $"Snapshot '{Path.GetFileName(verified)}' does not match.",
            $"Received: {received}",
            $"Verified: {verified}",
        };

        var expectedLines = Normalize(File.ReadAllText(verified)).Split('\n');
        var actualLines = actual.Split('\n');
        for (var i = 0; i < Math.Max(expectedLines.Length, actualLines.Length); i++)
        {
            var expected = i < expectedLines.Length ? expectedLines[i] : "<end of file>";
            var got = i < actualLines.Length ? actualLines[i] : "<end of file>";
            if (string.Equals(expected, got, StringComparison.Ordinal))
            {
                continue;
            }

            lines.Add($"First difference on line {(i + 1).ToString(CultureInfo.InvariantCulture)}:");
            lines.Add($"  verified: {expected}");
            lines.Add($"  received: {got}");
            break;
        }

        lines.Add(
            $"Review the diff, then move the received file over the verified one (or re-run with {AcceptVariable}=1).");
        return string.Join("\n", lines);
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>Strips a BOM, normalizes line endings, and ignores trailing newlines.</summary>
    private static string Normalize(string text)
        => text.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
}
