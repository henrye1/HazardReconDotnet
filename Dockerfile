# Render builds this with `runtime: docker` in render.yaml.
#
# Only the web app and the engine it depends on are built. The CLI is a local
# tool and the test project is not needed at runtime, so neither is copied - see
# .dockerignore, which also keeps host bin/obj out of the build context so a
# local Debug build cannot leak into the image.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# The project files come first on their own so restore is cached: editing source
# then rebuilds without re-downloading every package.
COPY src/HazardRecon.Core/HazardRecon.Core.csproj src/HazardRecon.Core/
COPY src/HazardRecon.Web/HazardRecon.Web.csproj src/HazardRecon.Web/
RUN dotnet restore src/HazardRecon.Web/HazardRecon.Web.csproj

COPY src/HazardRecon.Core/ src/HazardRecon.Core/
COPY src/HazardRecon.Web/ src/HazardRecon.Web/

RUN dotnet publish src/HazardRecon.Web/HazardRecon.Web.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

# .dockerignore should already have kept this out of the build context, but the
# guarantee is worth restating where it does not depend on ignore-pattern
# semantics: nothing environment-specific belongs in a published image.
RUN rm -f appsettings.Development.json

# A run writes its inputs and outputs under the app directory before they are
# copied to object storage. The image runs as a non-root user, so the directory
# has to exist and be owned by that user before the switch - otherwise the first
# run fails on a read-only path.
RUN mkdir -p /app/runs && chown -R $APP_UID:$APP_UID /app/runs
USER $APP_UID

# Documentation only: the app binds whatever HOST and PORT Render supplies, which
# render.yaml sets to 0.0.0.0 and 10000.
EXPOSE 10000

ENTRYPOINT ["dotnet", "HazardRecon.Web.dll"]
