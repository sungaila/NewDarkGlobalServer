ARG LAUNCHING_FROM_VS
ARG FINAL_BASE_IMAGE=${LAUNCHING_FROM_VS:+aotdebug}

FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
RUN apk update \
    && apk add build-base zlib-dev
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/GlobalServer/NewDarkGlobalServer.csproj", "src/GlobalServer/"]
COPY ["src/Core/Core.csproj", "src/Core/"]
RUN dotnet restore "./src/GlobalServer/NewDarkGlobalServer.csproj"
COPY . .
WORKDIR "/src/src/GlobalServer"
RUN dotnet build "./NewDarkGlobalServer.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./NewDarkGlobalServer.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=true

FROM base AS aotdebug
USER root
RUN apk update \
    && apk add gdb
USER app

FROM ${FINAL_BASE_IMAGE:-mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine} AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["./NewDarkGlobalServer"]
EXPOSE 5199/tcp