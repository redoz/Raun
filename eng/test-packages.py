#!/usr/bin/env python3
"""Build and run consumers of the packages in a local feed, outside the source tree."""

import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
import zipfile


PACKAGE_IDS = {"Raun", "Raun.Mtp", "Raun.Aspire", "Raun.Coverage"}
REPO = Path(__file__).resolve().parents[1]


def package_version(feed):
    versions = {}
    for package in feed.glob("*.nupkg"):
        with zipfile.ZipFile(package) as archive:
            nuspec = next(name for name in archive.namelist() if name.endswith(".nuspec"))
            metadata = ET.fromstring(archive.read(nuspec)).find("{*}metadata")
            package_id = metadata.findtext("{*}id")
            if package_id in PACKAGE_IDS:
                if package_id in versions:
                    raise RuntimeError(f"More than one package for {package_id} in {feed}")
                versions[package_id] = metadata.findtext("{*}version")
    if versions.keys() != PACKAGE_IDS or len(set(versions.values())) != 1:
        raise RuntimeError(f"Expected all four Raun packages at one version; found {versions}")
    return next(iter(versions.values()))


def write_xml(path, root):
    ET.indent(root)
    ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)


def prepare_samples(root, version):
    for name in ("AppointmentTests", "AspireAppointments"):
        shutil.copytree(REPO / "samples" / name, root / name,
                        ignore=shutil.ignore_patterns("bin", "obj", "artifacts", "packages*.lock.json"))
    for project in root.rglob("*.csproj"):
        original = REPO / "samples" / project.relative_to(root)
        lock = original.parent / "packages.lock.json"
        if sys.platform.startswith("linux"):
            lock = original.parent / "packages.linux-x64.lock.json" if (
                original.parent / "packages.linux-x64.lock.json").exists() else lock
        resolved = json.loads(lock.read_text(encoding="utf-8"))["dependencies"]["net10.0"]
        tree = ET.parse(project)
        document = tree.getroot()
        # The repo wires up the analyzer and SDK entry-point switches manually. A consumer
        # must get these from the packages, so do not carry that scaffolding into the test.
        for group in document:
            for item in list(group):
                if item.tag in {"CompilerVisibleProperty", "EnableMSTestRunner",
                                "TestingPlatformDotnetTestSupport", "GenerateProgramFile",
                                "GenerateTestingPlatformEntryPoint"}:
                    group.remove(item)
                elif item.tag == "PackageReference":
                    item.set("Version", f'[{resolved[item.attrib["Include"]]["resolved"]}]')
                elif item.tag == "ProjectReference":
                    target = (project.parent / item.attrib["Include"].replace("\\", "/")).resolve()
                    if target.stem.startswith("Raun"):
                        group.remove(item)
                        if target.stem != "Raun.Generator":
                            ET.SubElement(group, "PackageReference", Include=target.stem,
                                          Version=f"[{version}]")
                    elif not target.is_relative_to(root):
                        raise RuntimeError(f"Unexpected reference outside isolated samples: {target}")
        write_xml(project, document)


def prepare_generated_app(root, version):
    app = root / "GeneratedEntryPoint"
    app.mkdir()
    (app / "GeneratedEntryPoint.csproj").write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup>
  <ItemGroup><PackageReference Include="Raun.Mtp" Version="[{version}]" /></ItemGroup>
</Project>
''', encoding="utf-8")
    (app / "Scenarios.cs").write_text('''using Raun;
public sealed class PackageScenarios : Scenarios
{
    public PackageGiven Given => Steps<PackageGiven>();
    [Scenario] public async Task Runs() { await Given.Passes(); }
}
public sealed class PackageGiven : Given
{
    public Task Passes() => Task.CompletedTask;
}
''', encoding="utf-8")


def configure(root, feed):
    # Stop all MSBuild/NuGet directory searches here; never import repo build targets.
    (root / "Directory.Build.props").write_text('''<Project><PropertyGroup>
  <Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
</PropertyGroup></Project>
''', encoding="utf-8")
    (root / "Directory.Build.targets").write_text("<Project />", encoding="utf-8")
    (root / "Directory.Packages.props").write_text("<Project />", encoding="utf-8")
    shutil.copyfile(REPO / "global.json", root / "global.json")
    config = ET.Element("configuration")
    sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key="local", value=str(feed))
    ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
    mapping = ET.SubElement(config, "packageSourceMapping")
    local = ET.SubElement(mapping, "packageSource", key="local")
    for package_id in sorted(PACKAGE_IDS):
        ET.SubElement(local, "package", pattern=package_id)
    public = ET.SubElement(mapping, "packageSource", key="nuget.org")
    ET.SubElement(public, "package", pattern="*")
    write_xml(root / "nuget.config", config)


def run(root, env, *args):
    print("+ dotnet " + " ".join(map(str, args)), flush=True)
    subprocess.run(["dotnet", *map(str, args)], cwd=root, env=env, check=True, timeout=600)


def verify_assets(project, version):
    assets = json.loads((project.parent / "obj/project.assets.json").read_text(encoding="utf-8"))
    packages = {name.split("/")[0]: (name.split("/")[1], info["type"])
                for name, info in assets["libraries"].items()}
    if packages.get("Raun") != (version, "package"):
        raise RuntimeError(f"{project}: Raun was not resolved from the expected package")
    for name in PACKAGE_IDS & packages.keys():
        if packages[name] != (version, "package"):
            raise RuntimeError(f"{project}: unexpected {name} resolution: {packages[name]}")


def verify_report(directory):
    html = (directory / "raun-report.html").read_text(encoding="utf-8")
    match = re.search(r'<script id="model" type="application/json">(.*?)</script>', html, re.S)
    model = json.loads(match.group(1))
    if not model["scenarios"] or not any(s["steps"] for s in model["scenarios"]):
        raise RuntimeError("Package consumer ran zero scenarios or steps")
    if any(s["status"] != "passed" for s in model["scenarios"]):
        raise RuntimeError("Package consumer report contains unsuccessful scenarios")
    print(f'Verified report: {len(model["scenarios"])} scenarios', flush=True)


def main():
    feed = Path(sys.argv[1] if len(sys.argv) > 1 else REPO / "artifacts").resolve()
    version = package_version(feed)
    root = Path(tempfile.mkdtemp(prefix="raun-package-consumption-"))
    print(f"Testing Raun {version} in {root}", flush=True)
    configure(root, feed)
    prepare_samples(root, version)
    prepare_generated_app(root, version)
    env = dict(os.environ, NUGET_PACKAGES=str(root / ".packages"),
               NUGET_HTTP_CACHE_PATH=str(root / ".http-cache"),
               DOTNET_CLI_UI_LANGUAGE="en-US")
    for relative in ("GeneratedEntryPoint/GeneratedEntryPoint.csproj",
                     "AppointmentTests/AppointmentTests.csproj",
                     "AspireAppointments/AspireAppointments.Tests/AspireAppointments.Tests.csproj"):
        project = root / relative
        run(root, env, "restore", project, "--configfile", root / "nuget.config")
        run(root, env, "build", project, "-c", "Release", "--no-restore")
        verify_assets(project, version)
        report = root / "reports" / project.stem
        run(root, env, "run", "--project", project, "-c", "Release", "--no-build", "--",
            "--report-html", "--results-directory", report)
        verify_report(report)
    print(f"Package consumption passed for all four packages at {version}.", flush=True)


if __name__ == "__main__":
    main()
