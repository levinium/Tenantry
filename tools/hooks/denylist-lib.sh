#!/bin/sh
# Shared by the leak-guard hooks (pre-commit, commit-msg, pre-push).
#
# The denylist is kept OUTSIDE the repository because its terms (organisation names, domains,
# tenant IDs, people) are exactly the data this guard exists to keep out of a public repo.
# Location, first match wins:
#   $TENANTRY_DENYLIST
#   $APPDATA/tenantry-dev/denylist.txt                          (Windows)
#   ${XDG_CONFIG_HOME:-$HOME/.config}/tenantry-dev/denylist.txt (macOS, Linux)
# One case-insensitive literal per line; blank lines and lines starting with # are ignored.

denylist_path() {
  if [ -n "$TENANTRY_DENYLIST" ]; then
    printf '%s\n' "$TENANTRY_DENYLIST"
  elif [ -n "$APPDATA" ]; then
    printf '%s\n' "$APPDATA/tenantry-dev/denylist.txt"
  else
    printf '%s\n' "${XDG_CONFIG_HOME:-$HOME/.config}/tenantry-dev/denylist.txt"
  fi
}

# Writes the active patterns to a temp file and prints its path. Fails closed: a missing
# denylist blocks the operation instead of quietly checking nothing. An empty file is allowed,
# for contributors who have no private terms to protect.
load_patterns() {
  list=$(denylist_path)
  if [ ! -f "$list" ]; then
    echo "leak-guard: denylist not found at $list" >&2
    echo "leak-guard: create it (an empty file is fine) or set TENANTRY_DENYLIST." >&2
    return 1
  fi
  patterns=$(mktemp)
  tr -d '\r' < "$list" | grep -v '^[[:space:]]*#' | grep -v '^[[:space:]]*$' > "$patterns"
  printf '%s\n' "$patterns"
}

leak_fail() {
  echo "" >&2
  echo "leak-guard: BLOCKED. $1" >&2
  echo "leak-guard: remove the term, or edit the denylist if it is a false positive." >&2
  exit 1
}
