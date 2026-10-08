#!/bin/bash
#
# End-to-end test: drives the plugin with a real `dotnet restore` against a fake
# authenticated NuGet v3 feed, using a fake keyring tool. No real keyring involved.
#
# Asserts that NuGet discovers the plugin, negotiates the protocol, asks us for
# credentials, and presents the password we read to the feed.
#
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(dirname "$here")"
work="$(mktemp -d)"
trap 'rm -rf "$work"; [ -n "${feed_pid:-}" ] && kill "$feed_pid" 2>/dev/null' EXIT

PASSWORD="s3cr3t-pat-value"   # must match test/fakefeed.py and test/fake-kwallet-query
USERNAME="steve"

fail=0
check() {
  if [ "$2" = "$3" ]; then
    printf '  ok    %s\n' "$1"
  else
    printf '  FAIL  %s (expected %q, got %q)\n' "$1" "$3" "$2"
    fail=1
  fi
}

echo "== building"
dotnet publish "$root" -c Release -o "$work/plugin" --nologo >/dev/null || exit 1

echo "== starting fake feed"
export FAKE_FEED_LOG="$work/feed.log"
python3 -I "$here/fakefeed.py" >"$work/feed.out" 2>&1 &
feed_pid=$!

for _ in $(seq 50); do
  curl -s -o /dev/null "http://127.0.0.1:8099/v3/index.json" && break
  sleep 0.1
done

echo "== restoring"
cat > "$work/keyring.json" <<JSON
{
  "keyring": {
    "executable": "$here/fake-kwallet-query",
    "arguments": [["--read-password", "{entry}"]],
    "timeoutSeconds": 10
  },
  "sources": [
    { "name": "fakefeed", "uriPrefix": "http://127.0.0.1:8099/",
      "entry": "nuget-example-internal", "username": "$USERNAME" }
  ]
}
JSON

cp -r "$here/consumer" "$work/consumer"

NUGET_PLUGIN_PATHS="$work/plugin/nuget-plugin-keyring" \
NUGET_PLUGINS_CACHE_PATH="$work/plugin-cache" \
NUGET_HTTP_CACHE_PATH="$work/http-cache" \
KEYRING_NUGET_CREDPROVIDER_CONFIG="$work/keyring.json" \
KEYRING_NUGET_CREDPROVIDER_LOG="$work/plugin.log" \
KEYRING_NUGET_CREDPROVIDER_LOGLEVEL=Verbose \
  dotnet restore "$work/consumer" --nologo >"$work/restore.log" 2>&1

echo "== assertions"
check "plugin claimed the Authentication operation" \
  "$(grep -c 'Claiming the Authentication operation' "$work/plugin.log")" "1"
check "plugin was asked for credentials non-interactively" \
  "$(grep -c 'isNonInteractive: True' "$work/plugin.log")" "1"
check "plugin read the keyring and supplied credentials" \
  "$(grep -c 'Supplying credentials' "$work/plugin.log")" "1"
check "feed accepted our credentials" \
  "$(grep -c "Basic $USERNAME:$PASSWORD" "$FAKE_FEED_LOG")" "2"
check "feed served the service index" \
  "$(grep -c '200 service index' "$FAKE_FEED_LOG")" "1"
check "restore got past auth (fails only on the missing package)" \
  "$(grep -c 'NU1101' "$work/restore.log")" "1"
check "plugin never logged the secret" \
  "$(grep -c "$PASSWORD" "$work/plugin.log")" "0"

if [ "$fail" -eq 0 ]; then
  echo "== all assertions passed"
else
  echo "== FAILURES; logs in $work (not deleted)"
  trap - EXIT
  kill "$feed_pid" 2>/dev/null
fi
exit "$fail"
