#!/usr/bin/env bash
#
# POSTs (or PUTs) a FHIR resource to an endpoint described by FHIR_ENDPOINT_AUTH_JSON,
# acquiring an OAuth2 client_credentials token first when the endpoint needs one.
#
# Why this exists: talking to a real SDC/FHIR server by hand means retyping a token dance
# and a base URL every time, and the two things you must not get wrong are exactly the two
# that are easiest to fat-finger — which endpoint you are writing to, and whether the
# secret leaks. So the endpoint config is read from the environment (never from a file in
# this repo), the resource type is read from the payload rather than retyped, and the
# client secret is handed to curl over stdin so it never appears in argv where `ps` can
# see it.
#
# Config format (same shape the harness uses), keyed by FHIR base URL:
#
#   export FHIR_ENDPOINT_AUTH_JSON='{
#     "https://fhir-test.example/api/fhir": {
#       "type": "oauth2_client_credentials",
#       "client_id": "...",
#       "client_secret": "...",
#       "token_url": "https://fhir-test.example/auth/realms/r/protocol/openid-connect/token",
#       "scope": "optional space separated scopes"
#     }
#   }'
#
# "type": "none" (or a missing entry, with --insecure-no-auth) sends no Authorization
# header. Any other type is rejected rather than silently sent unauthenticated.
#
# Usage:
#   fhir-post.sh [options] <resource.json>     read the resource from a file
#   fhir-post.sh [options] -                   read the resource from stdin
#   fhir-post.sh --example > qr.json           emit the example QuestionnaireResponse
#
# The example is a real R5 QuestionnaireResponse captured from app.tiro.health -- nested
# items, per-answer Provenance in contained[], and the plain-text/RTF narrative
# alternatives -- so it exercises rather more of the write path than a two-answer stub.
# It lives in examples/questionnaireresponse-r5.json; edit it there.
#
# Options:
#   -b, --base <url>       FHIR base URL; required only when FHIR_ENDPOINT_AUTH_JSON
#                          holds more than one endpoint (otherwise the single key wins)
#   -t, --type <name>      resource type for the path; default: the payload's resourceType
#                          (the web app omits resourceType and leans on the path, so a
#                          payload copied straight out of devtools needs --type)
#   -X, --method <verb>    POST (default) or PUT; PUT needs an "id" in the payload
#   -c, --content-type <t> default application/fhir+json; some servers want
#                          application/json
#   -H, --header <h>       extra request header; repeatable
#   -n, --dry-run          resolve everything and print the request, but send nothing
#                          (still requests a token, so it does verify the credentials)
#   -q, --quiet            print only the response body
#   -h, --help             this text
#
# Exits non-zero on a 4xx/5xx, after printing the OperationOutcome.
#
# Requires: bash 3.2+, curl, jq.

set -euo pipefail

script_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

die() { printf 'fhir-post: %s\n' "$*" >&2; exit 1; }
log() { [ "$quiet" = 1 ] || printf '%s\n' "$*" >&2; }

base=""
res_type=""
method="POST"
content_type="application/fhir+json"
extra_headers=()
dry_run=0
quiet=0
allow_no_auth=0
payload_arg=""

usage() { sed -n '2,/^set -euo/p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//; $d'; }

example() {
  local f="$script_dir/examples/questionnaireresponse-r5.json"
  [ -f "$f" ] || die "example payload missing: $f"
  cat "$f"
}

while [ $# -gt 0 ]; do
  case "$1" in
    -b|--base)   base="${2:-}";     shift 2 ;;
    -t|--type)   res_type="${2:-}"; shift 2 ;;
    -X|--method) method="${2:-}";   shift 2 ;;
    -c|--content-type) content_type="${2:-}"; shift 2 ;;
    -H|--header) extra_headers+=("-H" "${2:-}"); shift 2 ;;
    -n|--dry-run) dry_run=1; shift ;;
    -q|--quiet)   quiet=1;   shift ;;
    --insecure-no-auth) allow_no_auth=1; shift ;;
    --example) example; exit 0 ;;
    -h|--help) usage; exit 0 ;;
    # Before -*), or a lone "-" reads as a malformed option instead of stdin.
    -)  [ -z "$payload_arg" ] || die "more than one resource given"
        payload_arg="-"; shift ;;
    -*) die "unknown option: $1 (try --help)" ;;
    *)  [ -z "$payload_arg" ] || die "more than one resource given"
        payload_arg="$1"; shift ;;
  esac
done

command -v jq   >/dev/null 2>&1 || die "jq is required"
command -v curl >/dev/null 2>&1 || die "curl is required"

[ -n "$payload_arg" ] || die "no resource given (try --help)"
case "$method" in POST|PUT) ;; *) die "--method must be POST or PUT, got: $method" ;; esac

# ---------------------------------------------------------------- payload

if [ "$payload_arg" = "-" ]; then
  payload=$(cat)
else
  [ -f "$payload_arg" ] || die "no such file: $payload_arg"
  payload=$(cat "$payload_arg")
fi

printf '%s' "$payload" | jq empty 2>/dev/null || die "resource is not valid JSON"

if [ -z "$res_type" ]; then
  res_type=$(printf '%s' "$payload" | jq -r '.resourceType // empty')
  [ -n "$res_type" ] || die "payload has no resourceType; pass --type (e.g. --type QuestionnaireResponse)"
fi

res_id=$(printf '%s' "$payload" | jq -r '.id // empty')
if [ "$method" = "PUT" ] && [ -z "$res_id" ]; then
  die "PUT needs an \"id\" in the payload (FHIR update is PUT [base]/[type]/[id])"
fi

# ---------------------------------------------------------------- endpoint

config="${FHIR_ENDPOINT_AUTH_JSON:-}"
if [ -z "$config" ]; then
  [ -n "$base" ] || die "FHIR_ENDPOINT_AUTH_JSON is unset and no --base given"
  [ "$allow_no_auth" = 1 ] || die "FHIR_ENDPOINT_AUTH_JSON is unset; pass --insecure-no-auth to send without a token"
  config='{}'
fi
printf '%s' "$config" | jq empty 2>/dev/null || die "FHIR_ENDPOINT_AUTH_JSON is not valid JSON"

if [ -z "$base" ]; then
  count=$(printf '%s' "$config" | jq 'keys | length')
  case "$count" in
    1) base=$(printf '%s' "$config" | jq -r 'keys[0]') ;;
    0) die "FHIR_ENDPOINT_AUTH_JSON has no endpoints; pass --base" ;;
    *) printf 'fhir-post: FHIR_ENDPOINT_AUTH_JSON has %s endpoints; pass --base with one of:\n' "$count" >&2
       printf '%s' "$config" | jq -r 'keys[] | "  " + .' >&2
       exit 1 ;;
  esac
fi

base="${base%/}"
entry=$(printf '%s' "$config" | jq -c --arg b "$base" '.[$b] // empty')

if [ -z "$entry" ]; then
  [ "$allow_no_auth" = 1 ] || die "no entry for $base in FHIR_ENDPOINT_AUTH_JSON (pass --insecure-no-auth to send without a token)"
  auth_type="none"
else
  auth_type=$(printf '%s' "$entry" | jq -r '.type // "none"')
fi

# ---------------------------------------------------------------- token

access_token=""
case "$auth_type" in
  none)
    log "auth:     none"
    ;;
  oauth2_client_credentials)
    token_url=$(printf '%s' "$entry" | jq -r '.token_url // empty')
    client_id=$(printf '%s' "$entry" | jq -r '.client_id // empty')
    scope=$(printf '%s'     "$entry" | jq -r '.scope // empty')
    [ -n "$token_url" ] || die "endpoint $base has no token_url"
    [ -n "$client_id" ] || die "endpoint $base has no client_id"
    printf '%s' "$entry" | jq -e 'has("client_secret")' >/dev/null || die "endpoint $base has no client_secret"

    log "auth:     oauth2_client_credentials as $client_id"

    # The secret goes to curl through a config file on stdin, not on the command line:
    # argv is world-readable via `ps` on a shared box. @json, not @text: a curl config
    # value ends at the first space unless it is double-quoted, and JSON's escaping of "
    # and \ is exactly what curl's config parser un-escapes, so a secret with spaces or
    # quotes in it survives whole instead of being silently truncated.
    token_response=$(
      printf '%s' "$entry" | jq -r --arg scope "$scope" '
        "--data-urlencode " + ("grant_type=client_credentials" | @json),
        "--data-urlencode " + ("client_id=" + .client_id | @json),
        "--data-urlencode " + ("client_secret=" + .client_secret | @json),
        (if $scope == "" then empty else "--data-urlencode " + ("scope=" + $scope | @json) end)
      ' | curl -sS -X POST "$token_url" \
                -H 'Content-Type: application/x-www-form-urlencoded' \
                -H 'Accept: application/json' \
                --config -
    ) || die "token request to $token_url failed"

    access_token=$(printf '%s' "$token_response" | jq -r '.access_token // empty' 2>/dev/null || true)
    if [ -z "$access_token" ]; then
      # Safe to show: a failed token response carries an error, not a token.
      printf 'fhir-post: no access_token from %s\n' "$token_url" >&2
      printf '%s\n' "$token_response" >&2
      exit 1
    fi
    ;;
  *)
    die "unsupported auth type \"$auth_type\" for $base"
    ;;
esac

# ---------------------------------------------------------------- request

if [ "$method" = "PUT" ]; then
  url="$base/$res_type/$res_id"
else
  url="$base/$res_type"
fi

log "$method     $url"

if [ "$dry_run" = 1 ]; then
  log "dry run:  not sent"
  printf '%s\n' "$payload"
  exit 0
fi

headers=$(mktemp); body=$(mktemp)
trap 'rm -f "$headers" "$body"' EXIT

# --data-binary, not -d: -d strips newlines, and a narrative div is not something to
# reflow on the way to a server that may be checksumming it.
set -- -sS --compressed -X "$method" "$url" \
  -H "Content-Type: $content_type" \
  -H 'Accept: application/fhir+json' \
  -D "$headers" -o "$body" -w '%{http_code}' \
  --data-binary @-
[ ${#extra_headers[@]} -eq 0 ] || set -- "$@" "${extra_headers[@]}"
[ -z "$access_token" ] || set -- "$@" -H "Authorization: Bearer $access_token"

status=$(printf '%s' "$payload" | curl "$@") || die "request to $url failed"

log "status:   $status"
# grep -i, not awk's IGNORECASE: that is a gawk extension and macOS ships BSD awk.
# The `|| true` is load-bearing: no Location header (i.e. every error response) makes
# grep exit 1, and under set -e + pipefail that killed the script here, one line before
# it would have printed the OperationOutcome explaining the failure.
location=$(grep -i '^location:' "$headers" | tail -1 | sed 's/^[^:]*: *//; s/\r$//' || true)
[ -z "$location" ] || log "location: $location"

if jq . "$body" >/dev/null 2>&1; then
  jq . "$body"
else
  cat "$body"
fi

case "$status" in
  2*) exit 0 ;;
  *)  exit 1 ;;
esac
