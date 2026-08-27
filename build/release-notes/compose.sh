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
# `gh` authenticated to read the release and generate the "What's Changed" list.

set -euo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
COMPAT_SRC="src/Tiro.Health.FormSdk.Abstractions/SdcCompatibility.cs"
WEB_SDK_PIN="build/web-sdk/package.json"
cd "$repo_root"

# The generated block is fenced so it can be replaced in place on a re-run without
# touching anything a human wrote around it. See the release-already-exists path below.
MARK_START="<!-- tiro:compat:start -->"
MARK_END="<!-- tiro:compat:end -->"

# Matches the declaration itself, not the many <see cref="...MinimumSdcVersion"/> mentions
# in the doc comments above it. Deliberately brittle to a reformat: if the declaration is
# rewritten (split across lines, turned into a property), extraction fails loudly and the
# --check step reddens that pull request. A looser pattern that kept limping along could
# publish an empty or wrong floor, which is worse than a red build.
#
# Three outcomes, kept distinct: 0 = read it, 2 = the file is not at that ref at all,
# 3 = the file is there but the declaration did not match. Collapsing 2 and 3 would let a
# release whose declaration merely *looked different* be reported as a release that had no
# floor, which re-emits the one-time "this release introduces a minimum" note years late.
floor_from() {  # $1 = git ref, or "" to read the working tree
    local src out
    if [ -n "$1" ]; then
        src=$(git show "$1:$COMPAT_SRC" 2>/dev/null) || return 2
    else
        src=$(cat "$COMPAT_SRC")
    fi
    out=$(printf '%s\n' "$src" \
        | sed -n 's/.*static readonly string MinimumSdcVersion[[:space:]]*=[[:space:]]*"\([^"]*\)".*/\1/p' \
        | head -1)
    [ -n "$out" ] || return 3
    printf '%s' "$out"
}

# Splits a version into its numeric triple, ignoring any prerelease/build suffix. Empty
# output means "outside the grammar", which callers must treat as unknown rather than
# guessing. Two expressions rather than one optional group: BSD sed (macOS, where this gets
# developed) has no \? or \+ in a basic regex, and a pattern that works only under GNU sed
# would fail in exactly the place nobody runs it.
version_triple() {
    printf '%s' "${1#v}" | sed -n \
        -e 's/^\([0-9][0-9]*\)\.\([0-9][0-9]*\)\.\([0-9][0-9]*\)$/\1 \2 \3/p' \
        -e 's/^\([0-9][0-9]*\)\.\([0-9][0-9]*\)\.\([0-9][0-9]*\)[-+].*$/\1 \2 \3/p'
}

# True when $1 is strictly above $2; status 2 when either side is outside the grammar.
# Only (major, minor, patch) is compared and a -rc.N / +build suffix is ignored on both
# sides — the same rule SdcCompatibility.Evaluate applies, so these notes cannot describe
# the check in terms the check itself would disagree with.
version_gt() {
    local ta tb a1 a2 a3 b1 b2 b3
    ta=$(version_triple "$1")
    tb=$(version_triple "$2")
    { [ -n "$ta" ] && [ -n "$tb" ]; } || return 2
    read -r a1 a2 a3 <<<"$ta"
    read -r b1 b2 b3 <<<"$tb"
    if [ "$a1" -ne "$b1" ]; then [ "$a1" -gt "$b1" ]; return; fi
    if [ "$a2" -ne "$b2" ]; then [ "$a2" -gt "$b2" ]; return; fi
    [ "$a3" -gt "$b3" ]
}

FLOOR=$(floor_from "") || true
if [ -z "${FLOOR:-}" ]; then
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
prev_floor_status=0
if [ -n "$PREV" ]; then
    PREV_FLOOR=$(floor_from "$PREV") || prev_floor_status=$?
elif [ -n "$(git tag --list 'v*.*.*' | head -1)" ]; then
    # Other release tags exist but none is reachable from this one's parent. Almost always a
    # shallow clone or unfetched tags rather than a genuine first release — and the visible
    # symptom is only a missing floor comparison, so say so loudly instead of shipping notes
    # that quietly skipped the raise check.
    echo "::warning::no previous release tag reachable from $TAG^ — the floor-raise check and" \
         "the changelog range were both skipped. Check that the checkout fetched tags." >&2
fi

if [ "$prev_floor_status" = "3" ]; then
    echo "::warning::$PREV has $COMPAT_SRC but its MinimumSdcVersion declaration did not match" \
         "the pattern, so this release's notes cannot say whether the floor moved." >&2
fi

# --- the generated block ---------------------------------------------------------------
compose_block() {
    printf '%s\n' "$MARK_START"

    if [ -n "$PREV_FLOOR" ] && [ "$PREV_FLOOR" != "$FLOOR" ]; then
        local direction=unknown
        if version_gt "$FLOOR" "$PREV_FLOOR"; then
            direction=raised
        elif version_gt "$PREV_FLOOR" "$FLOOR"; then
            direction=lowered
        fi

        case "$direction" in
        raised)
            # The commit that introduced the current value carries the reason it was raised —
            # the README's rule is to raise the floor alongside it. Linking beats paraphrasing:
            # this script cannot know why, and a generated sentence pretending to would be noise.
            #
            # --pickaxe-regex, built from the same shape the extraction accepts. A fixed-string
            # search demanding exactly one space either side of `=` would miss a declaration the
            # extraction is happy with, and the only symptom would be this line quietly absent.
            local floor_re raise
            floor_re=$(printf '%s' "$FLOOR" | sed 's/\./\\./g')
            raise=$(git log -1 --format=%H --pickaxe-regex \
                -S"static readonly string MinimumSdcVersion[[:space:]]*=[[:space:]]*\"$floor_re\"" \
                -- "$COMPAT_SRC" 2>/dev/null || true)
            echo "## ⚠️ Upgrade required — minimum SDC server raised"
            echo
            echo "**Minimum SDC server: \`$PREV_FLOOR\` → \`$FLOOR\`**"
            echo
            echo "Upgrade your SDC server to \`$FLOOR\` or newer before deploying this harness release."
            if [ -n "$raise" ]; then
                echo "Why it was raised: $SERVER/$REPO/commit/$raise"
            else
                echo "::warning::could not locate the commit that set MinimumSdcVersion=$FLOOR;" \
                     "the notes name the raise but cannot link its reason." >&2
            fi
            echo
            ;;
        lowered)
            # Not an upgrade demand, and must never render as one: this release supports an
            # OLDER server than its predecessor, so nobody has to move. Saying so is still
            # worth a line — it is the one case where an integrator can stop planning work.
            echo "## Minimum SDC server lowered"
            echo
            echo "**Minimum SDC server: \`$PREV_FLOOR\` → \`$FLOOR\`**"
            echo
            echo "No action needed. This release supports an older SDC server than the previous one."
            echo
            ;;
        *)
            echo "::warning::the floor changed from '$PREV_FLOOR' to '$FLOOR' but one of them is" \
                 "outside the version grammar, so the notes cannot say which direction it moved." >&2
            ;;
        esac
    elif [ -n "$PREV" ] && [ "$prev_floor_status" = "2" ]; then
        # First release that declares a floor at all — the previous release genuinely had no
        # SdcCompatibility.cs. Not a raise (nobody's server has to move), but the check is new
        # behaviour an integrator should hear about once.
        echo "## New: the harness now states a minimum SDC server version"
        echo
        echo "It is **reported, not enforced** — a server below \`$FLOOR\` produces a warning through"
        echo "\`SdcServerVersionCheck\` and telemetry; nothing is refused. See the link below for why."
        echo
    fi

    # Deliberately just the two numbers. Everything else that could go here — what the pairing
    # means, why the page needs no script tag, how the check behaves, a link to all of that — is
    # standing explanation that would repeat verbatim in every release forever. It lives in the
    # README, which is where someone reads it once. Release notes carry what is different about
    # THIS release; the blocks above carry that on the releases where something actually moved.
    cat <<BODY
## Compatibility

| | |
|---|---|
| **Minimum SDC server** | \`$FLOOR\` |
| Embedded \`@tiro-health/web-sdk\` | \`$WEB_SDK\` |
BODY

    printf '%s\n' "$MARK_END"
}

# --- what goes below it ------------------------------------------------------------------
# Two cases, and the difference is not cosmetic.
#
# A release that ALREADY EXISTS was almost certainly written by a person: the normal way to
# push a v*.*.* tag here is "Draft a new release" in the GitHub UI, which creates the release
# and the tag together, so by the time this runs the body holds hand-written prose (and
# possibly notes generated by the UI's own button). Overwriting that would destroy the one
# part of a release note a generator cannot produce — and it would do it silently, on every
# release. So the existing body is kept verbatim below the block, and a previously generated
# block is stripped first so re-runs replace rather than accumulate.
#
# A release that does NOT exist was tagged from git, and there is no prose to preserve; ask
# GitHub for the "What's Changed" list so the notes are not just a compatibility table.
EXISTING=""
if EXISTING=$(gh release view "$TAG" --repo "$REPO" --json body --jq .body 2>/dev/null); then
    TAIL=$(printf '%s\n' "$EXISTING" | awk -v s="$MARK_START" -v e="$MARK_END" '
        index($0, s) { skip = 1 }
        skip == 0    { print }
        index($0, e) { skip = 0 }
    ' | sed -e '/./,$!d')
else
    notes_args=(--method POST "repos/$REPO/releases/generate-notes" -f "tag_name=$TAG")
    if [ -n "$PREV" ]; then
        notes_args+=(-f "previous_tag_name=$PREV")
    fi
    TAIL=$(gh api "${notes_args[@]}" --jq .body)
fi

compose_block
if [ -n "$TAIL" ]; then
    echo
    printf '%s\n' "$TAIL"
fi
