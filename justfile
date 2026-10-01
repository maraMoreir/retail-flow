# Dev task runner - https://github.com/casey/just
# Install: `winget install --id Casey.Just` (Windows) / `brew install just` (macOS)
# Every recipe here is also just a plain dotnet/docker command, so `just` is a
# convenience, never a requirement - see the command in each recipe if you don't
# have it installed.

# List available recipes.
default:
    @just --list

# Start local dependencies (Postgres, RabbitMQ, Redis, Seq, Keycloak).
up:
    docker compose up -d

# Stop local dependencies, keeping data volumes.
down:
    docker compose down

# Stop local dependencies AND delete their data volumes (fresh slate).
reset:
    docker compose down -v

# Tail logs from every local dependency container.
logs:
    docker compose logs -f

# Restore NuGet packages for the whole solution.
restore:
    dotnet restore

# Build the whole solution (Debug).
build: restore
    dotnet build --no-restore

# Run every test project except benchmarks (RetailFlow.IntegrationTests needs
# Docker for its Testcontainers - make sure `just up`'s dependencies, or at least
# a running Docker daemon, are available first).
test: build
    dotnet test RetailFlow.slnx --no-build

# Run only the fast, no-Docker-required test projects.
test-fast: build
    dotnet test tests/RetailFlow.UnitTests --no-build
    dotnet test tests/RetailFlow.ArchitectureTests --no-build
    dotnet test tests/RetailFlow.ChaosTests --no-build

# Run RetailFlow.Api locally against the containers started by `just up`.
run-api:
    dotnet run --project src/RetailFlow.Api

# Run RetailFlow.Worker locally against the containers started by `just up`.
run-worker:
    dotnet run --project src/RetailFlow.Worker

# Apply .editorconfig formatting rules to the whole solution.
format:
    dotnet format RetailFlow.slnx

# Check formatting without changing anything (what CI would enforce, once wired).
format-check:
    dotnet format RetailFlow.slnx --verify-no-changes

# Remove all bin/ and obj/ folders.
clean:
    dotnet clean
    find . -type d \( -name bin -o -name obj \) -not -path "./docker/*" -exec rm -rf {} +
