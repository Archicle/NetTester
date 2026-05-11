# 请参阅 https://aka.ms/customizecontainer 以了解如何自定义调试容器，以及 Visual Studio 如何使用此 Dockerfile 生成映像以更快地进行调试。

# 此阶段用于在快速模式(默认为调试配置)下从 VS 运行时
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
# --- 修改部分：安装 ping 工具 ---
USER root
RUN apt-get update && \
    apt-get install -y --no-install-recommends iputils-ping tzdata && \
    ln -snf /usr/share/zoneinfo/Asia/Shanghai /etc/localtime && \
    echo Asia/Shanghai > /etc/timezone && \
    rm -rf /var/lib/apt/lists/*
ENV TZ=Asia/Shanghai
# --- 修改结束 ---
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

# 此阶段用于生成服务项目
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["NetTester.csproj", "."]
RUN dotnet restore "./NetTester.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "./NetTester.csproj" -c $BUILD_CONFIGURATION -o /app/build

# 此阶段用于发布要复制到最终阶段的服务项目
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./NetTester.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# 此阶段在生产中使用，或在常规模式下从 VS 运行时使用(在不使用调试配置时为默认值)
# 由于 final 是基于 base 构建的，它会自动包含上面安装的 iputils-ping
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "NetTester.dll"]