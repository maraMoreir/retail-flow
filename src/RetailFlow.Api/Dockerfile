# Not wired into docker-compose.yml yet (local dev runs the API via `dotnet run`
# against the containerized dependencies for a fast inner loop) - this exists for
# Phase 9 (Kubernetes/production deployment) and for anyone who wants to build a
# real image today. Build from the REPO ROOT so the relative COPY paths below and
# Directory.Build.props/Directory.Packages.props resolve correctly:
#   docker build -f src/RetailFlow.Api/Dockerfile -t retailflow-api .

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props RetailFlow.slnx global.json ./
COPY src/RetailFlow.Domain/RetailFlow.Domain.csproj src/RetailFlow.Domain/
COPY src/RetailFlow.Application/RetailFlow.Application.csproj src/RetailFlow.Application/
COPY src/RetailFlow.Infrastructure/RetailFlow.Infrastructure.csproj src/RetailFlow.Infrastructure/
COPY src/RetailFlow.Shared/RetailFlow.Shared.csproj src/RetailFlow.Shared/
COPY src/RetailFlow.Reporting/RetailFlow.Reporting.csproj src/RetailFlow.Reporting/
COPY src/RetailFlow.Api/RetailFlow.Api.csproj src/RetailFlow.Api/
RUN dotnet restore src/RetailFlow.Api/RetailFlow.Api.csproj

COPY src/ src/
RUN dotnet publish src/RetailFlow.Api/RetailFlow.Api.csproj \
    -c Release -o /app --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

RUN addgroup -S retailflow && adduser -S retailflow -G retailflow
USER retailflow

COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD wget -qO- http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "RetailFlow.Api.dll"]
