FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# 安装 libkrb5-3 解决 Npgsql GSSAPI 依赖警告
RUN apt-get update && apt-get install -y --no-install-recommends libkrb5-3 2>/dev/null || true

COPY --from=build /app .
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
EXPOSE 8080
ENTRYPOINT ["dotnet", "backend.dll"]
