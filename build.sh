#!/usr/bin/env bash
# ============================================
# 构建 & 推送所有 Docker 镜像
# 共 3 个镜像：
#   1. apiswitch-all    — 合并部署（前后端同镜像）
#   2. apiswitch-api    — 后端独立镜像
#   3. apiswitch-ui     — 前端独立镜像
# ============================================
set -euo pipefail

echo "=========================================="
echo "  1/3  构建合并部署镜像 (apiswitch-all)..."
echo "=========================================="
docker compose -f docker-compose.full.yml --profile combined build app

echo ""
echo "=========================================="
echo "  2/3  构建后端独立镜像 (apiswitch-api)..."
echo "=========================================="
docker compose -f docker-compose.full.yml --profile separate build backend

echo ""
echo "=========================================="
echo "  3/3  构建前端独立镜像 (apiswitch-ui)..."
echo "=========================================="
docker compose -f docker-compose.full.yml --profile separate build frontend

echo ""
echo "=========================================="
echo "  推送所有镜像到仓库..."
echo "=========================================="
echo "  → docker.cnb.cool/vxlife/apiswitch/apiswitch-all:latest"
echo "  → docker.cnb.cool/vxlife/apiswitch/apiswitch-api:latest"
echo "  → docker.cnb.cool/vxlife/apiswitch/apiswitch-ui:latest"
echo ""

docker push docker.cnb.cool/vxlife/apiswitch/apiswitch-all:latest
docker push docker.cnb.cool/vxlife/apiswitch/apiswitch-api:latest
docker push docker.cnb.cool/vxlife/apiswitch/apiswitch-ui:latest

echo ""
echo "=========================================="
echo "  ✅ 全部构建并推送完成！"
echo "=========================================="