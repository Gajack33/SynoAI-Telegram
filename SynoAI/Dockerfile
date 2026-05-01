FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        curl \
        libfontconfig1 \
        fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["SynoAI.csproj", "./"]
RUN dotnet restore "SynoAI.csproj"
COPY . .

WORKDIR "/src/."
RUN dotnet build "SynoAI.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "SynoAI.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
RUN mkdir -p /app/Captures && chown -R app:app /app
USER app
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 CMD curl --fail http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "SynoAI.dll"]
