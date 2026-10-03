#!/usr/bin/env bash
# Checks both models and that every design variant fails the way it is documented to.
# Needs Java 11+, tla2tools.jar ($TLA_JAR) and Quint (npm i -g @informalsystems/quint).
# Quint's first `verify` downloads Apalache into ~/.quint.
set -uo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
jar="${TLA_JAR:-$HOME/.local/share/tla/tla2tools.jar}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
failures=0

ok() { printf '  ok    %s\n' "$1"; }
bad() { printf '  FAIL  %s\n' "$1"; failures=$((failures + 1)); }

# tlc <cfg> <expected: pass | text that must appear in the output>
tlc() {
  local out
  out="$(cd "$root/tla" && java -XX:+UseParallelGC -cp "$jar" tlc2.TLC -workers auto \
    -metadir "$work/tlc-$1" -config "$1.cfg" QueryLifecycle.tla 2>&1)"
  local code=$?
  if [[ $2 == pass ]]; then
    [[ $code == 0 ]] && ok "tla $1: no error" || { bad "tla $1: expected no error"; echo "$out" | tail -20; }
  else
    grep -q "$2" <<<"$out" && ok "tla $1: $2" || { bad "tla $1: expected '$2'"; echo "$out" | tail -20; }
  fi
}

# qrun <module> <invariant> <pass | violation>: random simulation
qrun() {
  local out
  out="$(cd "$root/quint" && quint run client.qnt --main="$1" --invariant="$2" \
    --max-steps=30 --max-samples=20000 --seed=1 --verbosity=1 2>&1)"
  if [[ $3 == pass ]]; then
    grep -q 'No violation' <<<"$out" && ok "quint $1: $2 holds (simulation)" || { bad "quint $1: $2 should hold"; echo "$out" | tail -20; }
  else
    grep -q '\[violation\]' <<<"$out" && ok "quint $1: $2 violated" || { bad "quint $1: $2 should be violated"; echo "$out" | tail -20; }
  fi
}

echo "TLA+: spec/tla/QueryLifecycle.tla"
tlc Proposed pass
tlc ProposedLive pass
tlc Current 'Invariant NoSilentLoss is violated'
tlc BeforeQueue 'Temporal properties were violated'
tlc NoRefire 'Temporal properties were violated'
tlc NoConnCheck 'Temporal properties were violated'

echo "Quint: spec/quint/client.qnt"
if (cd "$root/quint" && quint typecheck client.qnt >/dev/null); then ok "typecheck"; else bad "typecheck"; fi
out="$(cd "$root/quint" && quint verify client.qnt --backend=tlc --main=Proposed --invariant=Safe 2>&1)"
if grep -q 'No violation found' <<<"$out"; then
  ok "quint Proposed: Safe holds (TLC, exhaustive within the bounds)"
else
  bad "quint Proposed: Safe should hold"; echo "$out" | grep -vi protobuf | tail -30
fi
rm -rf "$root/quint/_apalache-out"
for w in NeverActiveView NeverAbortedView NeverReconnected NeverTxRolledBackOnDisconnect; do
  qrun Proposed "$w" violation   # reachability: the properties are not vacuous
done
qrun Current NoSilentRollback violation
qrun Current NoOrphanSession violation
qrun Current HonestTxView violation
qrun NoServerTruth NoSilentRollback violation
qrun NoServerTruth HonestTxView violation
qrun LeaseBeforeWrite HonestTxView violation
qrun LuaByName NoOrphanSession violation

echo
if [[ $failures == 0 ]]; then echo "all checks passed"; else echo "$failures check(s) failed"; exit 1; fi
