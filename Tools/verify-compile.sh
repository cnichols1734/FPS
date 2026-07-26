#!/usr/bin/env bash
# Compile every project script against the Unity assemblies without going through
# the editor. Unity's generated csprojs list sources explicitly and go stale the
# moment a file is added, so we rewrite the compile items as a recursive glob.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

UNITY_ROOT="${UNITY_ROOT:-/Applications/Unity/Hub/Editor/6000.5.5f1/Unity.app}"
DOTNET="$UNITY_ROOT/Contents/Resources/Scripting/DotNetSdk/dotnet"

if [[ ! -x "$DOTNET" ]]; then
  echo "verify-compile: no dotnet at $DOTNET (set UNITY_ROOT)" >&2
  exit 1
fi
for proj in Assembly-CSharp.csproj Assembly-CSharp-Editor.csproj; do
  if [[ ! -f "$proj" ]]; then
    echo "verify-compile: missing $proj — open the project in Unity once to generate it" >&2
    exit 1
  fi
done

python3 - <<'PY'
import re, pathlib

def reglob(src, dst, include, exclude=None):
    text = pathlib.Path(src).read_text()
    text = re.sub(r'\s*<Compile Include="[^"]*"\s*/>', '', text)
    item = f'  <ItemGroup>\n    <Compile Include="{include}"'
    if exclude:
        item += f' Exclude="{exclude}"'
    item += ' />\n  </ItemGroup>\n</Project>'
    text = text.replace('</Project>', item)
    text = text.replace(
        '<ProjectReference Include="Assembly-CSharp.csproj" />',
        '<ProjectReference Include="ArenaRuntime.verify.csproj" />')
    pathlib.Path(dst).write_text(text)

reglob('Assembly-CSharp.csproj', 'ArenaRuntime.verify.csproj',
       'Assets/**/*.cs', 'Assets/_Project/Editor/**/*.cs')
reglob('Assembly-CSharp-Editor.csproj', 'ArenaEditor.verify.csproj',
       'Assets/_Project/Editor/**/*.cs')
PY

OUT="${TMPDIR:-/tmp}/arenafps-verify"
status=0
"$DOTNET" build ArenaEditor.verify.csproj --nologo \
  -p:OutputPath="$OUT/bin/" -p:IntermediateOutputPath="$OUT/obj/" \
  2>&1 | grep -E "error|warning CS|Build succeeded|Error\(s\)" | sort -u || status=$?

exit $status
