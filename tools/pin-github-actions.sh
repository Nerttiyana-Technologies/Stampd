#!/usr/bin/env bash
# Pin every `uses: owner/repo@<tag>` in .github/workflows/*.yml to a full 40-char
# SHA, with the original tag preserved as an inline comment so humans (and
# Dependabot) can still read which version is intended.
#
# Why: OSSF Scorecard's PinnedDependenciesID rule (also CISA / Slsa Level-2)
# requires Actions to be pinned to SHAs because tag references can be silently
# rewritten by the Action's maintainer (or a compromised maintainer). A SHA is
# immutable — what you reviewed is exactly what runs.
#
# Idempotent: re-runs only touch lines whose @ref isn't already a 40-char SHA.
# Requires: bash, sed, and either `gh` CLI (preferred) or `git ls-remote`.
#
# After running: `git diff .github/workflows/` to inspect, then commit.
# Going forward: Dependabot (configured in .github/dependabot.yml) opens PRs
# to bump these SHAs when new versions ship.

set -euo pipefail

cd "$(dirname "$0")/.."

WORKFLOW_DIR=".github/workflows"

if [ ! -d "$WORKFLOW_DIR" ]; then
  echo "No $WORKFLOW_DIR — run from repo root."
  exit 1
fi

# Resolve a tag (or branch) to its commit SHA on github.com/<owner>/<repo>.
# Uses `gh api` if available (uses your auth, no rate limits), falls back to
# `git ls-remote` (no auth, hits GitHub's 60-req/hr unauth limit).
resolve_sha() {
  local owner_repo="$1"
  local ref="$2"

  if command -v gh >/dev/null 2>&1; then
    # gh api resolves both tags AND branches. Try tag refs first, then branches.
    gh api "repos/${owner_repo}/git/ref/tags/${ref}" --jq '.object.sha' 2>/dev/null && return 0
    gh api "repos/${owner_repo}/git/ref/heads/${ref}" --jq '.object.sha' 2>/dev/null && return 0
  fi

  # Fallback: git ls-remote against the public repo.
  git ls-remote "https://github.com/${owner_repo}.git" "refs/tags/${ref}" \
    | awk '{print $1; exit}' \
    | grep -qE '^[a-f0-9]{40}$' \
    && git ls-remote "https://github.com/${owner_repo}.git" "refs/tags/${ref}" | awk '{print $1; exit}' \
    && return 0

  git ls-remote "https://github.com/${owner_repo}.git" "refs/heads/${ref}" \
    | awk '{print $1; exit}'
}

PINNED=0
SKIPPED=0
FAILED=0

# Process each workflow file in turn.
for f in "$WORKFLOW_DIR"/*.yml "$WORKFLOW_DIR"/*.yaml; do
  [ -f "$f" ] || continue
  echo ""
  echo "==> $f"

  # Extract uses: lines, one per call. Each look like:
  #   - uses: actions/checkout@v4
  #         uses: actions/setup-dotnet@v4
  while IFS= read -r line; do
    # Strip leading whitespace + the literal "uses:" prefix.
    raw="${line#*uses: }"
    raw="${raw%% *}"  # drop trailing whitespace/comments

    if [[ ! "$raw" =~ ^([^@]+)@(.+)$ ]]; then
      continue
    fi
    full_action="${BASH_REMATCH[1]}"
    ref="${BASH_REMATCH[2]}"

    # Sub-path actions like `github/codeql-action/upload-sarif` live inside the
    # parent repo `github/codeql-action`. Strip the third+ path segments so we
    # resolve against the actual GitHub repo, not a non-existent sub-repo.
    owner_repo=$(echo "$full_action" | cut -d/ -f1-2)

    # Already pinned (40-char hex SHA)?
    if [[ "$ref" =~ ^[a-f0-9]{40}$ ]]; then
      echo "   [skip] $owner_repo@$ref (already pinned)"
      SKIPPED=$((SKIPPED + 1))
      continue
    fi

    sha=$(resolve_sha "$owner_repo" "$ref" || true)
    if [[ ! "$sha" =~ ^[a-f0-9]{40}$ ]]; then
      echo "   [FAIL] $owner_repo@$ref — could not resolve (rate limit? typo?)"
      FAILED=$((FAILED + 1))
      continue
    fi

    # Replace `owner/repo@ref` with `owner/repo@<SHA> # ref` in the file.
    # Escape the ref for use in sed (some refs contain dots).
    escaped_ref=$(printf '%s\n' "$ref" | sed 's/[.[\*^$/]/\\&/g')
    sed -i.bak -E "s|${owner_repo}@${escaped_ref}([[:space:]]*\$\|[[:space:]])|${owner_repo}@${sha} # ${ref}\1|g" "$f"
    rm -f "${f}.bak"

    echo "   [pin ] $owner_repo@$ref -> $sha"
    PINNED=$((PINNED + 1))
  done < <(grep -E '^\s*-?\s*uses:\s+' "$f")
done

echo ""
echo "=========================================="
echo "Pinned: $PINNED   Skipped: $SKIPPED   Failed: $FAILED"
echo "=========================================="
echo ""
echo "Review with: git diff .github/workflows/"
echo "Commit with: git add .github/workflows/ && git commit -m 'chore(security): pin GitHub Actions to SHAs (OSSF Scorecard PinnedDependenciesID)'"
echo ""
if [ "$FAILED" -gt 0 ]; then
  echo "WARNING: $FAILED Actions could not be resolved. If you hit GitHub's"
  echo "60-req/hr unauth limit, install gh CLI and re-run: brew install gh && gh auth login"
  exit 1
fi
