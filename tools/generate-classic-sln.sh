#!/usr/bin/env bash
# Generates a classic Stampd.sln from every csproj in the tree.
#
# Background: .NET 10 SDK ships a `dotnet new sln` template that defaults to
# the new .slnx XML format. Qodana .NET's SCA scanner doesn't yet understand
# .slnx — it needs the classic .sln format to enumerate projects for the
# vulnerability + license audit pass. This script generates that .sln on the
# fly so the .slnx stays as the canonical source.
#
# Run from repo root. Safe to re-run.
set -euo pipefail

cd "$(dirname "$0")/.."

OUT="Stampd.sln"
rm -f "$OUT"

# Classic .sln header (Visual Studio 2022 format)
cat > "$OUT" <<'EOF'
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.0.31903.59
MinimumVisualStudioVersion = 10.0.40219.1
EOF

# Standard C# project type GUID — every .NET SDK-style csproj uses this.
CSPROJ_TYPE_GUID="9A19103F-16F7-4668-BE54-9A1E7A4F7556"

# Collect all csprojs, generate stable per-project GUIDs from their paths so
# re-runs produce identical .sln files (deterministic for git diffs).
PROJECTS=()
while IFS= read -r -d '' csproj; do
  # Path relative to repo root, with Windows-style backslashes (sln spec).
  rel_path="${csproj#./}"
  rel_path_win="${rel_path//\//\\}"
  name=$(basename "$csproj" .csproj)
  # Deterministic GUID from path (SHA-1 reshaped to GUID format).
  guid=$(echo -n "$rel_path" | shasum | head -c 32 | \
    sed -E 's/^(.{8})(.{4})(.{4})(.{4})(.{12}).*/\1-\2-\3-\4-\5/' | tr '[:lower:]' '[:upper:]')

  cat >> "$OUT" <<EOF
Project("{$CSPROJ_TYPE_GUID}") = "$name", "$rel_path_win", "{$guid}"
EndProject
EOF
  PROJECTS+=("$guid")
done < <(find . -name "*.csproj" -not -path "*/bin/*" -not -path "*/obj/*" -print0 | sort -z)

# Global block — solution configurations + per-project configurations.
cat >> "$OUT" <<'EOF'
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|Any CPU = Debug|Any CPU
		Release|Any CPU = Release|Any CPU
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
EOF

for guid in "${PROJECTS[@]}"; do
  cat >> "$OUT" <<EOF
		{$guid}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{$guid}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{$guid}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{$guid}.Release|Any CPU.Build.0 = Release|Any CPU
EOF
done

cat >> "$OUT" <<'EOF'
	EndGlobalSection
	GlobalSection(SolutionProperties) = preSolution
		HideSolutionNode = FALSE
	EndGlobalSection
EndGlobal
EOF

echo "Wrote $OUT with ${#PROJECTS[@]} project entries"
