# See src/RetailFlow.Api/Dockerfile for why this isn't wired into docker-compose.yml
# yet. Build from the REPO ROOT:
#   docker build -f src/RetailFlow.Worker/Dockerfile -t retailflow-worker .

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props RetailFlow.slnx global.json ./
COPY src/RetailFlow.Domain/RetailFlow.Domain.csproj src/RetailFlow.Domain/
COPY src/RetailFlow.Application/RetailFlow.Application.csproj src/RetailFlow.Application/
COPY src/RetailFlow.Infrastructure/RetailFlow.Infrastructure.csproj src/RetailFlow.Infrastructure/
COPY src/RetailFlow.Shared/RetailFlow.Shared.csproj src/RetailFlow.Shared/
COPY src/RetailFlow.Worker/RetailFlow.Worker.csproj src/RetailFlow.Worker/
RUN dotnet restore src/RetailFlow.Worker/RetailFlow.Worker.csproj

COPY src/ src/
RUN dotnet publish src/RetailFlow.Worker/RetailFlow.Worker.csproj \
    -c Release -o /app --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS runtime
WORKDIR /app

RUN addgroup -S retailflow && adduser -S retailflow -G retailflow
USER retailflow

COPY --from=build /app .

ENTRYPOINT ["dotnet", "RetailFlow.Worker.dll"]
