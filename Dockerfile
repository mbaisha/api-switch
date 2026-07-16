# ============================================
# 合并部署镜像：前端 (Vue) + 后端 (ASP.NET Core)
# 多阶段构建，产物在同一个运行时镜像中
# ============================================

# ---- Stage 1: 构建前端 (Vue) ----
FROM node:22-alpine AS frontend-build
WORKDIR /app/frontend
COPY frontend/package*.json ./
RUN npm ci
COPY frontend/ .
RUN npm run build

# ---- Stage 2: 构建后端 (ASP.NET Core) ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
WORKDIR /src
COPY backend/ .
RUN dotnet restore
RUN dotnet publish -c Release -o /app

# ---- Stage 3: 运行时镜像 ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# 安装 libkrb5-3 解决 Npgsql GSSAPI 依赖警告
RUN apt-get update && apt-get install -y --no-install-recommends libkrb5-3 2>/dev/null || true

# 复制后端编译产物
COPY --from=backend-build /app .

# 复制前端构建产物到 wwwroot（ASP.NET Core 静态文件目录）
COPY --from=frontend-build /app/frontend/dist /app/wwwroot

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
EXPOSE 8080
ENTRYPOINT ["dotnet", "backend.dll"]