#!/usr/bin/env bash
# 打包 FFmpeg WebUI 的发布版本。
#
# 用法：
#   ./scripts/publish.sh              # 自动识别当前平台
#   ./scripts/publish.sh osx-arm64    # 指定运行时标识
#   ./scripts/publish.sh all          # 打包全部平台（需要 .NET SDK 支持交叉发布）
#
# 产物位于 bin/Release/net10.0/publish/<rid>/，自包含、目标机器无需安装 .NET。

set -euo pipefail

cd "$(dirname "$0")/.."
ROOT="$(pwd)"
OUT_ROOT="$ROOT/dist"

detect_rid() {
    local os arch
    os="$(uname -s)"
    arch="$(uname -m)"
    case "$os" in
        Darwin) echo "osx-$([ "$arch" = "arm64" ] && echo arm64 || echo x64)" ;;
        Linux)  echo "linux-$([ "$arch" = "aarch64" ] && echo arm64 || echo x64)" ;;
        *)      echo "win-x64" ;;
    esac
}

TARGET="${1:-$(detect_rid)}"

if [ "$TARGET" = "all" ]; then
    RIDS=(win-x64 win-arm64 osx-arm64 osx-x64 linux-x64 linux-arm64)
else
    RIDS=("$TARGET")
fi

mkdir -p "$OUT_ROOT"

for rid in "${RIDS[@]}"; do
    echo ""
    echo "=============================================================="
    echo "  发布 $rid"
    echo "=============================================================="

    dotnet publish FFmpegWebUI.csproj \
        -c Release \
        -r "$rid" \
        --self-contained true \
        -p:PublishReadyToRun=true \
        -o "$OUT_ROOT/$rid"

    # 复制说明文档，方便直接分发给用户
    cp -f README.md "$OUT_ROOT/$rid/" 2>/dev/null || true

    echo "✅ 完成：$OUT_ROOT/$rid"
done

echo ""
echo "发布结束。启动方式："
for rid in "${RIDS[@]}"; do
    case "$rid" in
        win-*) echo "  $rid → 运行 dist/$rid/FFmpegWebUI.exe" ;;
        *)     echo "  $rid → 运行 ./dist/$rid/FFmpegWebUI" ;;
    esac
done
echo ""
echo "提示：应用会只监听 127.0.0.1，并自动打开浏览器。"
echo "      需要局域网访问时设置 FFMPEGWEBUI_BIND=0.0.0.0 后启动。"
