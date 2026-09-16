#!/usr/bin/env bash
# 把工作区的两个包 + 所有探针同步到验证工程。
#
# 两条硬规则（都是踩过坑来的）：
#   1. 包目录必须「先删后拷」—— cp -a 不删多余文件，残留的旧代码/旧 po 会让验证结果假绿。
#   2. 探针的**唯一权威副本**在 _notes/probes/，反向同步进验证工程。
#      以前探针只存在于 _verify-proj 里（正好在本脚本 rm -rf 的目录内），靠临时备份续命，
#      而且 NTConverterProbe 的报告路径还是硬编码到 _verify-proj 的绝对路径 —— 一旦换工程跑
#      就会覆盖验证工程的报告。现在归档目录是源，验证工程是产物。
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT="$(pwd)"
PROJ="$ROOT/_verify-proj"
PKG="$PROJ/Packages"
ASSETS="$PROJ/Assets/Editor"
SRC="$ROOT/_notes/probes"

if [ ! -d "$SRC" ]; then
  echo "❌ 找不到探针归档目录 $SRC —— 拒绝继续（宁可停下来，也不要同步出一套没有探针的工程）" >&2
  exit 1
fi

# 1) 包目录：先删后拷
rm -rf "$PKG/jp.lilxyzw.nontoon" "$PKG/com.123cy321.nontoon-converter"
cp -a "$ROOT/NonToon" "$PKG/jp.lilxyzw.nontoon"
cp -a "$ROOT/nontoon-converter" "$PKG/com.123cy321.nontoon-converter"

# 2) 探针：从归档铺进去（缺一个就失败，避免"探针没部署 → 什么都没测 → 却报通过"）
mkdir -p "$ASSETS"
copy_probe() {
  local name="$1" dest="$2"
  if [ ! -f "$SRC/$name" ]; then
    echo "❌ 归档里缺少探针 $name" >&2
    exit 1
  fi
  cp "$SRC/$name" "$dest/$name"
  [ -f "$SRC/$name.meta" ] && cp "$SRC/$name.meta" "$dest/$name.meta" || true
  echo "  probe  -> $dest/$name"
}

copy_probe NTBundleProbe.cs "$ASSETS"
copy_probe NTVerify.cs     "$ASSETS"
copy_probe NTDiag.cs       "$ASSETS"
copy_probe NTGpuProbe.cs   "$ASSETS"
copy_probe NTL10nProbe.cs  "$ASSETS"
copy_probe NTLightAdjusterProbe.cs "$ASSETS"   # ③ 亮度插件：**43 项断言**（26 项 MA-only 契约 + 2026-09-17 新增 17 项 NTAmbient 环境色温：往返精度/暖色地图/偏绿判定/属性类型契约）；断言"descriptor 未被改动"的 MA-only 契约
copy_probe NTAaoProbe.cs   "$ASSETS"           # AAO/NDMF 构建验证（全反射，不装 AAO 也能编译）；**工程里没装 AAO/NDMF 时会优雅跳过并 exit 0**
copy_probe NTPcssProbe.cs  "$ASSETS"           # PCSS 实测：采样器 filter/address、半影宽度、分辨率依赖
copy_probe NTSelfLitShadowProbe.cs "$ASSETS"   # ④ 自带光照与阴影：虚拟光烘焙 == 真 Light 同方向 + 材质写入
# copy_probe NTRealtimePcssProbe.cs "$ASSETS"  # ⛔ 已退役：它只测已删除的"自渲线性深度图"PCSS 路径。
                                               #    探针归档在 _notes/experiments/NTRealtimePcssProbe-已废弃.cs。
                                               #    活跃的 ⑥ 走 Unity 阴影贴图，着色器侧无自渲代码。
# NTMaProbe 只有在工程里装了真实 MA + NDMF 时才有意义（见 项目状态与交接.md §6 的搭建方法），
# 所以**不随 sync-verify 自动部署**，用的时候手工 cp 进 Assets/Editor。
# NTConverterProbe 必须放在包内（同程序集才能访问 internal 类型）
copy_probe NTConverterProbe.cs "$PKG/com.123cy321.nontoon-converter/Editor"

# 归档但**不部署**的（属于 _e2e-proj 或一次性实验，见 _notes/evidence/README.md）：
#   NTE2EProbe3/6/7.cs（端到端装置 E）、NTExitProbe.cs（退出码实验）、
#   NTToggleIsolation{,2}.shader（SetInt vs SetInteger 隔离实验）

# 3) 报数（数量只作参考，判据是各探针的退出码）
echo "synced:"
echo "  NonToon   -> $(find "$PKG/jp.lilxyzw.nontoon" -type f | wc -l) files"
echo "  converter -> $(find "$PKG/com.123cy321.nontoon-converter" -type f | wc -l) files"
echo "  probes    -> $(ls -1 "$SRC"/*.cs | wc -l) 个归档探针"
echo
echo "⚠️  注意：探针现在会 exit 非 0。发版时必须检查退出码，不要只看 txt 里的「全部通过」。"
