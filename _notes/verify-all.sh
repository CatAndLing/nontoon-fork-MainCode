#!/usr/bin/env bash
# 一次跑完 7 台装置（发版清单要求：**每次改过着色器/工具包都要全盘重跑**）。
#
# 判据是**每台的退出码**，不是报告 txt 里的"全部通过"。
# 报告 txt 落在 _verify-proj/ 下（各探针自己的 OUT 常量决定文件名）。
#
# 用法：
#   bash _notes/verify-all.sh              # 全部 7 台
#   bash _notes/verify-all.sh A B H        # 只跑指定几台（汇总会**明说只覆盖这几台**）
#
# ── [NT-FIX 37 / 2026-09-18] 按 Codex 第 8 轮审计修的三处假绿 ────────────────────
#   ① **注册表校验**：以前参数只做子串匹配 ⇒ `bash verify-all.sh C`（C 已退役、run() 里
#      根本没有它）会得到**空结果集**，而 `FAILS=0` ⇒ 照样打印「✅ 全部装置 exit 0」。
#      **什么都没跑，却是绿的。** 现在：未知/已退役的名字直接 exit 2。
#   ② **覆盖度**：跳过会被显式记进结果，且**只有跑满全部装置才允许打印"全部通过"**；
#      子集运行会打印"只覆盖 M/N 台，不能当全绿"。
#   ③ **报告新鲜度**：以前探针**自己**在启动时删旧报告 —— 但它没启动成功（方法名错 / 被 kill /
#      编译失败 / Unity 没起来）时那行根本不执行，磁盘上就留着**上一轮的**
#      「==== 全部通过 ====」⇒ 人工读 txt 假绿。现在**驱动在每台启动前统一删掉报告**，
#      跑完再看本轮是否真的产出了新报告（没有就告警）。
set -uo pipefail
cd "$(dirname "$0")/.."
ROOT="$(pwd)"
U="${U:-/c/Unity_File/2022.3.22f1/Editor/Unity.exe}"
P="$ROOT/_verify-proj"
RESULT="$ROOT/_notes/_verify-all.result"
RUNID="$(date +%Y%m%d-%H%M%S)-$$"

if [ ! -x "$U" ]; then echo "❌ 找不到 Unity: $U" >&2; exit 2; fi

# ── 装置注册表（唯一权威清单）──
ALL="A B D E F G H"
RETIRED="C"
if [ "$#" -gt 0 ]; then WANT="$*"; else WANT="$ALL"; fi

for n in $WANT; do
  case " $ALL " in
    *" $n "*) : ;;
    *)
      if [ "$n" = "$RETIRED" ]; then
        echo "❌ 装置 $n **已退役**：它测的是已删除的「自渲线性深度图 + 自建矩阵 PCSS」路径。" >&2
        echo "   活跃的 ⑥ 走 Unity 阴影贴图，着色器侧没有自渲代码。" >&2
        echo "   恢复它之前请先读 _notes/experiments/NTRealtimePcssProbe-已废弃.cs 的说明。" >&2
      else
        echo "❌ 未知装置 '$n'。注册表：$ALL（已退役：$RETIRED）" >&2
      fi
      exit 2 ;;
  esac
done

echo "== run-id    : $RUNID =="
echo "== 本轮覆盖  : $WANT"
echo "== 同步包与探针 =="
bash _notes/sync-verify.sh || { echo "❌ 同步失败，拒绝继续（宁可停，也不要跑一套旧代码）" >&2; exit 2; }

: > "$RESULT"
echo "# run-id $RUNID  覆盖: $WANT" >> "$RESULT"

run() {
  local name="$1" method="$2"; shift 2
  if ! echo " $WANT " | grep -q " $name "; then
    echo "── 装置 $name 跳过（未选中）"
    echo "$name SKIPPED -" >> "$RESULT"
    return 0
  fi
  echo ""
  echo "════ 装置 $name : $method"
  # ③ 每台启动前清掉报告：探针自己失败没跑到"删旧报告"那行时，这一层仍然有效
  rm -f "$P"/*.txt
  local t0; t0=$(date +%s)
  "$U" -batchmode -quit "$@" -projectPath "$P" -logFile "$ROOT/_notes/_verify-$name.log" -executeMethod "$method"
  local rc=$?
  local fresh; fresh=$(find "$P" -maxdepth 1 -name '*.txt' 2>/dev/null | wc -l)
  local note="-"
  if [ "$fresh" -gt 0 ]; then note="report-ok"; else note="⚠NO-REPORT"; fi
  echo "    装置 $name 退出码 = $rc   本轮新报告 = $fresh  ($note)"
  [ "$fresh" -eq 0 ] && echo "    ⚠️ 这台没有产出任何报告 —— 退出码为 0 也**不能**当作已验证过（探针可能根本没跑起来）"
  echo "$name $rc $note" >> "$RESULT"
}

# 渲染类装置（D/E/H 要 cam.Render()）**不能**加 -nographics；只有纯逻辑的 B 加。
# ⛔ 装置 C（NTRealtimePcssProbe）**已退役**，见上面的注册表说明与 sync-verify.sh 的注释。
run A NTBundleProbe.Run
run B LilToonToNonToonConverter.NTConverterProbe.Run -nographics
run D NTPcssProbe.Run
run E NTSelfLitShadowProbe.Run
run F NTAaoProbe.Run
run G NTL10nProbe.Run
run H NTLightAdjusterProbe.Run

echo ""
echo "==================== 汇总（run-id $RUNID）===================="
cat "$RESULT"
TOTAL=$(echo $ALL | wc -w)
RAN=$(awk '$2 != "SKIPPED" && $2 != 0 { n++ } END { print n+0 }' "$RESULT")          # 跑过的
FAILS=$(awk '$2 != "SKIPPED" && $2 != 0 && $2 != "-" && $2+0 != 0 { n++ } END { print n+0 }' "$RESULT")
NOREP=$(awk '$3 == "⚠NO-REPORT" { n++ } END { print n+0 }' "$RESULT")
RAN=$(grep -cv 'SKIPPED\|^#' "$RESULT" || true)

echo ""
echo "跑过 $RAN / $TOTAL 台；失败 $FAILS 台；无报告 $NOREP 台"
if [ "$NOREP" -gt 0 ]; then
  echo "⚠️ 有 $NOREP 台没产出报告 —— 即使退出码为 0，也**不能**说这些装置验证过什么。"
fi
if [ "$FAILS" -ne 0 ]; then
  echo "❌ 有 $FAILS 台装置非 0 退出 —— 逐台看 _notes/_verify-<名字>.log 与 _verify-proj/<报告>.txt"
  exit 1
fi
if [ "$RAN" -lt "$TOTAL" ]; then
  # ② 覆盖度：子集运行**不许**被读成全绿
  echo "⚠️ 本次只覆盖 $RAN/$TOTAL 台（未跑：$(echo $ALL | tr ' ' '\n' | while read -r d; do grep -q "^$d SKIPPED" "$RESULT" && printf '%s ' "$d"; done))"
  echo '   ⇒ 这**不是**一次全盘验证，发版前必须跑 `bash _notes/verify-all.sh`（无参数）。'
  exit 0
fi
echo "✅ 全部 $TOTAL 台装置 exit 0（run-id $RUNID）"
exit 0
