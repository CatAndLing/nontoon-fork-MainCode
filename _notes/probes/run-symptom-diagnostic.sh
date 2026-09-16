#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
LOCK="$PWD/_notes/.symptom-diagnostic.lock"
( set -o noclobber; echo $$ > "$LOCK" ) 2>/dev/null || { echo 'BLOCKED: symptom diagnostic lock exists'; exit 2; }
trap 'rm -f "$LOCK"' EXIT
run="$(date -u +%Y%m%dT%H%M%SZ)-$$"
out="$PWD/_notes/symptom-diagnostic-batch3/$run"
mkdir -p "$out"
# Unique empty output; previous failures are never overwritten.
rm -f "$out/report.txt" "$out/exit.txt"
export NT_SYMPTOM_OUT="$(cygpath -w "$out")"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File _notes/probes/prepare-symptom-diagnostic.ps1 -Evidence "$NT_SYMPTOM_OUT" > "$out/deploy.log" 2>&1 || { rc=$?; echo "DEPLOY_EXIT=$rc" > "$out/exit.txt"; cat "$out/deploy.log"; exit "$rc"; }
cat "$out/deploy.log"
set +e
/c/Unity_File/2022.3.22f1/Editor/Unity.exe -batchmode -quit -force-d3d11 -projectPath "$PWD/_notes/symptom-project" -logFile "$out/unity.log" -executeMethod LilToonToNonToonConverter.NTSymptomDiagnostic.Run
rc=$?
set -e
echo "UNITY_EXIT=$rc" > "$out/exit.txt"
cat "$out/exit.txt"
echo "EVIDENCE=$out"
[ -s "$out/report.txt" ] || { echo 'FAIL: no fresh report'; if [ "$rc" -ne 0 ]; then exit "$rc"; else exit 2; fi; }
exit "$rc"
