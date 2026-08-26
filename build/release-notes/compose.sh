#!/usr/bin/env bash
#
# Composes the GitHub release body for a tag, or (--check) verifies that the values it
# needs are still readable.
#
# Why this exists: the release notes are a load-bearing part of the versioning contract
# (GH-64), not a courtesy. The integrator story is "pin the harness NuGet; run an SDC
# server at or above MinimumSdcVersion", and a raised floor only reaches an integrator
# when they deliberately upgrade the harness — so the notes are the one channel that can
# announce it. README.md states outright that the value is here.
#
# Every number is read from the source the build itself uses, never retyped. Two copies of
# a version number drifting is the failure this whole workstream exists to prevent, and a
# release note quoting a stale floor would be exactly that, aimed at the people least able
# to detect it.
#
# Usage:
#   compose.sh --check          verify the values are extractable (CI guard); prints them
#   compose.sh <tag>            write the release body for <tag> to stdout
#
# The <tag> form needs full git history (fetch-depth: 0) to find the previous release, and
# `gh` authenticated to generate the "What's Changed" list.

set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
COMPAT_SRC="src/Tiro.Health.FormSdk.Abstractions/SdcCompatibility.cs"
WEB_SDK_PIN="build/web-sdk/package.json"
cd "$repo_root"

# Matches the declaration itself, not the many <see cref="...MinimumSdcVersion"/> mentions
# in the doc comments above it. Deliberately brittle to a reformat: if the declaration is
# rewritten (split across lines, turned into a property), extraction fails loudly and the
# --check step below reddens that pull request. A looser pattern that kept limping along
# could publish an empty or wrong floor, which is worse than a red build.
floor_from() {  # $1 = git ref, or "" to read the working tree
    local src
    if [ -n "$1" ]; then
        src=$(git show "$1:$COMPAT_SRC" 2>/dev/null) || return 1
    else
        src=$(cat "$COMPAT_SRC")
    fi
    printf '%s\n' "$src" \
        | sed -n 's/.*static readonly string MinimumSdcVersion[[:space:]]*=[[:space:]]*"\([^"]*\)".*/\1/p' \
        | head -1
}

FLOOR=$(floor_from "" || true)
if [ -z "$FLOOR" ]; then
    echo "::error file=$COMPAT_SRC::could not read MinimumSdcVersion — the declaration was" \
         "reformatted, or moved. Update the pattern in build/release-notes/compose.sh; the" \
         "release notes must state this value (see README, 'SDC server version compatibility')." >&2
    exit 1
fi

WEB_SDK=$(jq -r '.dependencies["@tiro-health/web-sdk"] // empty' "$WEB_SDK_PIN")
if [ -z "$WEB_SDK" ]; then
    echo "::error file=$WEB_SDK_PIN::could not read the pinned @tiro-health/web-sdk version." >&2
    exit 1
fi

if [ "${1:-}" = "--check" ]; then
    echo "MinimumSdcVersion=$FLOOR"
    echo "web-sdk=$WEB_SDK"
    exit 0
fi

TAG=${1:?usage: compose.sh --check | compose.sh <tag>}
SERVER=${GITHUB_SERVER_URL:-https://github.com}
REPO=${GITHUB_REPOSITORY:-Tiro-health/net-integration-harness}

# The newest release tag reachable from this one's parent. Empty for the first release ever,
# which is handled below rather than treated as an error.
PREV=$(git describe --tags --abbrev=0 --match 'v*.*.*' "$TAG^" 2>/dev/null || true)

PREV_FLOOR=""
if [ -n "$PREV" ]; then
    PREV_FLOOR=$(floor_from "$PREV" || true)
elif [ -n "$(git tag --list 'v*.*.*' | head -1)" ]; then
    # Other release tags exist but none is reachable from this one's parent. Almost always a
    # shallow clone or unfetched tags rather than a genuine first release — and the visible
    # symptom is only a missing floor comparison, so say so loudly instead of shipping notes
    # that quietly skipped the raise check.
    echo "::warning::no previous release tag reachable from $TAG^ — the floor-raise check and" \
         "the changelog range were both skipped. Check that the checkout fetched tags." >&2
fi

# GitHub's own "What's Changed" list, generated against the previous release so the range is
# right. Asked for explicitly rather than via `gh release create --generate-notes`, because
# that flag's interaction with a supplied body is not something to leave to a CLI version.
notes_args=(--method POST "repos/$REPO/releases/generate-notes" -f "tag_name=$TAG")
if [ -n "$PREV" ]; then
    notes_args+=(-f "previous_tag_name=$PREV")
fi
GENERATED=$(gh api "${notes_args[@]}" --jq .body)

# README anchored at this tag, so a link in a two-year-old release still lands on the text
# that release actually shipped.
COMPAT_DOC="$SERVER/$REPO/blob/$TAG/README.md#sdc-server-version-compatibility"

if [ -n "$PREV_FLOOR" ] && [ "$PREV_FLOOR" != "$FLOOR" ]; then
    # The commit that introduced the current value carries the reason it was raised — the
    # README's rule is to raise the floor alongside it. Linking beats paraphrasing: this
    # script cannot know why, and a generated sentence pretending to would be noise.
    RAISE=$(git log -1 --format=%H -S"MinimumSdcVersion = \"$FLOOR\"" -- "$COMPAT_SRC" || true)
    echo "## ⚠️ Upgrade required — minimum SDC server raised"
    echo
    echo "**Minimum SDC server: \`$PREV_FLOOR\` → \`$FLOOR\`**"
    echo
    echo "Upgrade your SDC server to \`$FLOOR\` or newer before deploying this harness release."
    if [ -n "$RAISE" ]; then
        echo "Why it was raised: $SERVER/$REPO/commit/$RAISE"
    fi
    echo
elif [ -n "$PREV" ] && [ -z "$PREV_FLOOR" ]; then
    # First release that declares a floor at all. Not a raise — nobody's server has to move —
    # but the check is new behaviour an integrator should hear about once.
    echo "## New: the harness now states a minimum SDC server version"
    echo
    echo "It is **reported, not enforced** — a server below \`$FLOOR\` produces a warning through"
    echo "\`SdcServerVersionCheck\` and telemetry; nothing is refused. See the link below for why."
    echo
fi

cat <<BODY
## Compatibility

| | |
|---|---|
| **Minimum SDC server** | \`$FLOOR\` |
| Embedded \`@tiro-health/web-sdk\` | \`$WEB_SDK\` — ships inside the package; not a version you choose |

Pin this NuGet version, and run an SDC server at or above the minimum — that pair is the whole
compatibility surface. Your \`index.html\` needs no \`<script>\` tag: the harness serves the
web-sdk bundle it was validated against. See [SDC server version compatibility]($COMPAT_DOC).

$GENERATED
BODY
