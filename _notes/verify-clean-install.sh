#!/usr/bin/env bash
# 装置 I —— 干净工程 VPM 安装门禁的驱动器（2026-09-18 新增）
#
# 它回答的问题：**一个标准空工程，只加已发布的 listing，能不能独立解析依赖并真正用起来？**
# 这与"包在 _verify-proj / unitypkg 里能跑"**不是**一回事 —— 那两个工程里 MA / shadercore
# 本来就在，依赖解析根本没被考验过。
#
# 用法：
#   bash _notes/verify-clean-install.sh            # 用已搭好的 _gate-proj 跑（判据 = 退出码）
#   BUILD=1 bash _notes/verify-clean-install.sh    # 先重建测试床（vrc-get 从**已发布 listing** 装）
#
# ── 为什么是**两趟** Unity ────────────────────────────────────────────────────
# 实测（2026-09-18）：只要**包集发生变化**，Unity 会在导入尚未完成时先跑一次脚本编译，
# 然后以 "Aborting batchmode due to failure: Scripts have compiler errors" 中止。
# 那一趟的错误集逐轮收敛（实测 540 → 39 → 61 → 48 → …），是导入过程的中间态：
# **既不是本包的缺陷，也不是可以拿来判定的结果。**
#   ⇒ 第 1 趟：只为导入/收敛，**不带 -executeMethod**（因此不可能产出报告），退出码照实打印；
#      第 2 趟：才是判据（装置 I 的退出码）。
# ⛔ 真实的编译错误不会被两趟掩盖 —— 第 2 趟照样红。
#
# ── 测试床为什么必须含 VRChat SDK 与**标准模板**清单 ──────────────────────────
# 工具包的**必需**依赖 Modular Avatar（→ NDMF）的 asmdef 用 overrideReferences 引用
# VRCSDKBase.dll / VRCSDK3A.dll / VRC.Dynamics.dll / System.Collections.Immutable.dll；
# 而裁剪过的清单还会引入无关错误：{"dependencies": {}} ⇒ com.vrchat.base 的 DOTween 报
# CS1069「Rigidbody2D → UnityEngine.Physics2DModule」；只有内置模块 ⇒ VRCSDK 自带测试文件
# 缺 com.unity.test-framework。⇒ **测试床 = 2022.3 标准模板清单 + VRChat SDK**，
# 否则测的是测试床自己的残缺，而不是本包。
set -uo pipefail
cd "$(dirname "$0")/.."
ROOT="$(pwd)"
U="${U:-/c/Unity_File/2022.3.22f1/Editor/Unity.exe}"
P="$ROOT/_gate-proj"
VPMJSON="$P/Packages/vpm-manifest.json"
TEMPLATE="$ROOT/_notes/clean-install-testbed-manifest.json"
VRCGET="$ROOT/_notes/vrc-get.exe"

[ -x "$U" ] || { echo "❌ 找不到 Unity: $U" >&2; exit 2; }

if [ "${BUILD:-0}" = "1" ]; then
  echo "== 重建测试床 $P =="
  [ -f "$TEMPLATE" ] || { echo "❌ 缺少标准模板清单 $TEMPLATE" >&2; exit 2; }
  [ -x "$VRCGET" ] || { echo "❌ 缺少 vrc-get: $VRCGET" >&2; exit 2; }
  rm -rf "$P"
  mkdir -p "$P/Assets/Editor" "$P/Packages" "$P/ProjectSettings"
  cp "$TEMPLATE" "$P/Packages/manifest.json"
  printf 'm_EditorVersion: 2022.3.22f1\nm_EditorVersionWithRevision: 2022.3.22f1 (887be4894c44)\n' > "$P/ProjectSettings/ProjectVersion.txt"
  cp "$ROOT/_notes/probes/NTCleanInstallProbe.cs" "$P/Assets/Editor/"
  # 只装**工具包**：它自己会把着色器包 + shadercore + MA + NDMF 通过 listing 解析出来；
  # VRChat SDK 不是我们的依赖，但测试床必须有它（见上面的说明）。
  "$VRCGET" install -p "$P" com.catandling.nontoon-converter -y || { echo "❌ 工具包安装失败" >&2; exit 2; }
  "$VRCGET" install -p "$P" com.vrchat.avatars -y || { echo "❌ VRChat SDK 安装失败" >&2; exit 2; }
fi

[ -f "$VPMJSON" ] || { echo "❌ 测试床里没有 $VPMJSON —— 先跑 BUILD=1 重建" >&2; exit 2; }

# 测试床的安装声明必须是**新身份**：否则测的是手工嵌入的包，不是 listing 解析。
for id in com.catandling.nontoon com.catandling.nontoon-converter; do
  grep -q "$id" "$VPMJSON" || { echo "❌ 测试床的 vpm-manifest.json 里没有 $id —— 拒绝继续" >&2; exit 2; }
done
grep -q "com.vrchat" "$VPMJSON" || { echo "❌ 测试床缺少 VRChat SDK（MA 无法编译）—— 先跑 BUILD=1 重建" >&2; exit 2; }
grep -q "com.unity.feature.development" "$P/Packages/manifest.json" \
  || { echo "❌ 测试床清单不是标准模板（缺 com.unity.feature.development ⇒ VRCSDK 测试文件编译不过）" >&2; exit 2; }

RUNID="$(date +%Y%m%d-%H%M%S)-$$"
REPORT="$P/ntcleaninstall.txt"
echo "== run-id $RUNID / 测试床 $P =="
rm -f "$REPORT" "$ROOT/_notes/_gate-proj-pass1.log" "$ROOT/_notes/_gate-proj.log"

echo "── 第 1 趟：导入/收敛（不带 -executeMethod，退出码不作判据）"
"$U" -batchmode -quit -projectPath "$P" -logFile "$ROOT/_notes/_gate-proj-pass1.log"
RC1=$?
echo "   第 1 趟退出码 = $RC1  ← 中间态，照实记录；判据在下一趟"

echo "── 第 2 趟：判据"
"$U" -batchmode -quit -projectPath "$P" -logFile "$ROOT/_notes/_gate-proj.log" -executeMethod NTCleanInstallProbe.Run
RC2=$?
if [ -f "$REPORT" ]; then
  echo "--- 装置 I 报告（$REPORT）---"
  cat "$REPORT"
  mkdir -p "$ROOT/_notes/reports/$RUNID"
  cp "$REPORT" "$ROOT/_notes/reports/$RUNID/I-ntcleaninstall.txt"
else
  echo "   ⚠️ 第 2 趟没有产出报告 —— 退出码 $RC2 也**不能**当作已验证过（探针可能根本没跑起来）"
fi
echo "== 装置 I 退出码 = $RC2（第 1 趟 $RC1 不作判据）=="
exit "$RC2"
