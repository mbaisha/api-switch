#!/usr/bin/env bash
set -euo pipefail

# 1. 安装 atomcode
echo "==> 安装 atomcode..."
curl -fsSL https://raw.atomgit.com/atomgit_atomcode/atomcode/raw/main/scripts/install.sh | sh

# 2. 安装 .NET 10
echo "==> 安装 .NET 10 SDK..."

# 下载 dotnet-install 脚本
curl -L https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
chmod +x /tmp/dotnet-install.sh

# 安装 .NET 10
/tmp/dotnet-install.sh --channel 10.0

# 清理安装脚本
rm -f /tmp/dotnet-install.sh

# 设置 DOTNET_ROOT 并加入 PATH
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"

# 写入 shell 配置文件（兼容 bash 和 zsh）
SHELL_RC=""
if [ -f "$HOME/.zshrc" ]; then
    SHELL_RC="$HOME/.zshrc"
elif [ -f "$HOME/.bashrc" ]; then
    SHELL_RC="$HOME/.bashrc"
fi

if [ -n "$SHELL_RC" ]; then
    if ! grep -q "DOTNET_ROOT" "$SHELL_RC" 2>/dev/null; then
        echo "==> 将 .NET 环境变量写入 $SHELL_RC"
        {
            echo ''
            echo '# .NET SDK'
            echo 'export DOTNET_ROOT="$HOME/.dotnet"'
            echo 'export PATH="$DOTNET_ROOT:$PATH"'
        } >> "$SHELL_RC"
    fi
fi

# 3. 安装 Claude Code
echo "==> 安装 Claude Code..."
npm install -g @anthropic-ai/claude-code

# 4. 配置 Claude Code
echo "==> 配置 Claude Code..."

# 写入 onboarding 状态
echo '{"hasCompletedOnboarding": true}' > "$HOME/.claude.json"

# 写入 settings.json
mkdir -p "$HOME/.claude"
cat > "$HOME/.claude/settings.json" << 'EOF'
{
    "env": {
        "ANTHROPIC_AUTH_TOKEN": "sk-6b03c79e4bcf4a75bc657c0d8c2fd746",
        "ANTHROPIC_BASE_URL": "http://127.0.0.1",
        "ANTHROPIC_MODEL": "gpt-5.5",
        "ANTHROPIC_DEFAULT_HAIKU_MODEL": "gpt-5.5",
        "ANTHROPIC_DEFAULT_SONNET_MODEL": "gpt-5.5",
        "ANTHROPIC_DEFAULT_OPUS_MODEL": "gpt-5.5",
        "CLAUDE_CODE_SUBAGENT_MODEL": "gpt-5.5"
    }
}
EOF
# docker compose -f ./ai-api/docker-api.yml up -d
docker compose -f ./bin/docker-api.yml --profile external-db up -d
echo "==> 安装完成！"
echo "    atomcode、.NET 10 SDK 和 Claude Code 已安装。"
echo "    请执行 'source $SHELL_RC' 或重新打开终端以加载环境变量。"
