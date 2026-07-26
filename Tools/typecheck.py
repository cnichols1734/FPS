"""Type-check a Unity-generated .csproj with the Roslyn compiler bundled in the editor.

Unity holds an exclusive lock on the project while it is open, so a second batch-mode
editor cannot be spawned just to see compile errors. This drives csc directly off the
sources and references Unity already wrote into the csproj.
"""
from __future__ import annotations

import os
import subprocess
import sys
import xml.etree.ElementTree as ET

UNITY = "/Applications/Unity/Hub/Editor/6000.5.5f1/Unity.app/Contents"
DOTNET = f"{UNITY}/Resources/Scripting/DotNetSdk/dotnet"
CSC = f"{UNITY}/Resources/Scripting/DotNetSdk/sdk/8.0.318/Roslyn/bincore/csc.dll"
NS = "{http://schemas.microsoft.com/developer/msbuild/2003}"


def strip_ns(tag: str) -> str:
    return tag.split("}")[-1]


def collect(csproj: str):
    tree = ET.parse(csproj)
    sources, refs, projrefs, defines, unsafe, langversion = [], [], [], [], False, None

    for node in tree.iter():
        tag = strip_ns(node.tag)
        if tag == "Compile":
            inc = node.get("Include")
            if inc:
                sources.append(inc.replace("\\", "/"))
        elif tag == "HintPath" and node.text:
            refs.append(node.text.strip().replace("\\", "/"))
        elif tag == "ProjectReference":
            inc = node.get("Include")
            if inc:
                projrefs.append(inc.replace("\\", "/"))
        elif tag == "DefineConstants" and node.text:
            defines.extend(d for d in node.text.replace(";", ",").split(",") if d.strip())
        elif tag == "AllowUnsafeBlocks" and node.text:
            unsafe = node.text.strip().lower() == "true"
        elif tag == "LangVersion" and node.text:
            langversion = node.text.strip()

    return sources, refs, projrefs, sorted(set(defines)), unsafe, langversion


def main(csproj: str, built: dict | None = None):
    built = {} if built is None else built
    if csproj in built:
        return built[csproj]

    sources, refs, projrefs, defines, unsafe, langversion = collect(csproj)

    # Editor assemblies depend on the runtime one via ProjectReference; build it first so
    # its types resolve instead of reporting a wall of phantom namespace errors.
    for dep in projrefs:
        if os.path.isfile(dep):
            main(dep, built)
            refs.append(f"/tmp/{os.path.basename(dep)}.typecheck.dll")
    missing_src = [s for s in sources if not os.path.isfile(s)]
    missing_ref = [r for r in refs if not os.path.isfile(r)]

    print(f"{os.path.basename(csproj)}: {len(sources)} sources, {len(refs)} references")
    for m in missing_src:
        print(f"  missing source: {m}")
    for m in missing_ref[:5]:
        print(f"  missing reference: {m}")

    args = [
        DOTNET, CSC,
        "-target:library",
        "-nologo",
        "-nostdlib+",
        "-noconfig",
        f"-out:/tmp/{os.path.basename(csproj)}.typecheck.dll",
        "-warn:0",
    ]
    if langversion:
        args.append(f"-langversion:{langversion}")
    if unsafe:
        args.append("-unsafe+")
    if defines:
        args.append("-define:" + ";".join(defines))
    args += [f"-r:{r}" for r in refs if os.path.isfile(r)]
    args += [s for s in sources if os.path.isfile(s)]

    rsp = f"/tmp/{os.path.basename(csproj)}.rsp"
    with open(rsp, "w") as fh:
        for a in args[2:]:
            fh.write(f'"{a}"\n')

    proc = subprocess.run([DOTNET, CSC, f"@{rsp}"], capture_output=True, text=True)
    out = (proc.stdout + proc.stderr).strip()

    errors = [ln for ln in out.splitlines() if ": error " in ln]
    if errors:
        print(f"\n{len(errors)} ERROR(S):")
        for ln in errors[:40]:
            print("  " + ln)
    else:
        print("\nNo compile errors.")

    built[csproj] = 1 if errors else 0
    return built[csproj]


if __name__ == "__main__":
    targets = sys.argv[1:] or ["Assembly-CSharp.csproj", "Assembly-CSharp-Editor.csproj"]
    cache: dict = {}
    sys.exit(max(main(t, cache) for t in targets))
