#!/usr/bin/env bash
# Model-checks SchedulerCancellation.tla with TLC, one rule at a time, and says which hold.
# Exits 0 when every rule holds, 1 when a rule breaks and prints its path, and 2 when TLC is missing or errored.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
jar="${TLA2TOOLS:-$HOME/.cache/tlaplus/tla2tools-1.7.4.jar}"
if [ ! -f "$jar" ]; then
  echo "No TLC at $jar." >&2
  echo "Get tla2tools.jar v1.7.4, sha256 936a262061c914694dfd669a543be24573c45d5aa0ff20a8b96b23d01e050e88," >&2
  echo "from https://github.com/tlaplus/tlaplus/releases/tag/v1.7.4 and put it there, or set TLA2TOOLS." >&2
  exit 2
fi

native() { if command -v cygpath > /dev/null; then cygpath -w "$1"; else printf '%s' "$1"; fi; }

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
cp "$here/SchedulerCancellation.tla" "$work/"

status=0
for rule in $(tr -d '\r' < "$here/SchedulerCancellation.cfg" | sed -n 's/^INVARIANT //p'); do
  printf 'SPECIFICATION Spec\nINVARIANT %s\nCHECK_DEADLOCK FALSE\n' "$rule" > "$work/$rule.cfg"
  (cd "$work" && java -XX:+UseParallelGC -cp "$(native "$jar")" tlc2.TLC -workers 1 \
      -metadir "$(native "$work/meta-$rule")" -config "$rule.cfg" SchedulerCancellation.tla) > "$work/$rule.out" 2>&1 || true
  out="$(tr -d '\r' < "$work/$rule.out")"
  if grep -q "No error has been found" <<< "$out"; then
    echo "HOLDS  $rule ($(grep -o '[0-9]* distinct states found' <<< "$out" | head -1))"
  elif grep -q "Invariant $rule is violated" <<< "$out"; then
    echo "BREAKS $rule, shortest path:"
    grep -E '^State [0-9]+:' <<< "$out" | sed -e 's/ line .*//' -e 's/^/  /'
    [ "$status" -eq 2 ] || status=1
  else
    echo "ERROR  $rule: TLC did not finish. Its output ends:"
    tail -20 <<< "$out" | sed 's/^/  /'
    status=2
  fi
done
exit "$status"
