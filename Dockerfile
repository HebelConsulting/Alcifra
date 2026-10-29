# NO `# syntax=` DIRECTIVE, deliberately (2026-09-15). It makes BuildKit fetch a FRONTEND IMAGE from Docker
# Hub on every build — and that was the ONLY Docker Hub dependency here, since every base image below comes
# from mcr.microsoft.com precisely to avoid Docker Hub's pull limits. The directive quietly undid that for the
# whole image: every CI scan and every `docker compose up --build` paid a Docker Hub round trip.
#
# It cost a real outage: auth.docker.io answered 500 to the token request and the Trivy scan failed with
# "failed to resolve source metadata for docker.io/docker/dockerfile:1", on a build whose own images were all
# reachable. Re-running fixed it, which is exactly what makes this worth removing rather than retrying.
#
# Nothing here needs the pinned frontend: no --mount, no COPY --link, no heredocs, no --chmod. The one advanced
# construct is `FROM --platform=$BUILDPLATFORM` below, which BuildKit's BUILT-IN frontend supports. Re-add a
# syntax line only alongside a feature that genuinely requires it, knowing what it re-introduces.

# The brand's icons (ADR 0993), generated once for both stages below. Its own stage on the DEBIAN SDK image, because
# the generator renders with Skia, whose native library has no musl build for ARM64: on Alpine it would break
# `docker compose up --build` on an Apple Silicon Mac. $BUILDPLATFORM, so it runs natively and never under emulation.
# fontconfig comes from the distribution's package manager (ADR 0611).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS icons
WORKDIR /src
RUN apt-get update \
 && apt-get install -y --no-install-recommends libfontconfig1 \
 && rm -rf /var/lib/apt/lists/*
COPY . .
RUN dotnet build src/Alcifra.BrandIcons -c Release -p:BrandIconsPhase=Generate -p:BrandIconsDir=/icons/

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Version stamped into the desktop-client download artifacts + their file names (ADR 0490) and into the API
# assembly, which surfaces it as GET /api's serverVersion (ADR 0512). CI passes the release tag
# (docker/metadata-action's {{version}}). The Dockerfile can't derive it from git — .git is excluded from the
# build context (.dockerignore).
#
# The default is the non-release sentinel, matching the Api project's own default. It used to be 0.1.0 — a REAL
# past release — so an image built without --build-arg was indistinguishable from a genuine v0.1.0 deployment,
# which is a plausible-looking lie rather than an admission of not knowing (issue #425).
ARG VERSION=0.0.0-dev

# Copy all project files first (preserving relative paths, needed since Api's project references must resolve
# during restore) so the restore layer is cached independently of source-code changes.
COPY ["src/Alcifra.Localization/Alcifra.Localization.csproj", "src/Alcifra.Localization/"]
COPY ["src/SimplArchive.ModuleAbi/SimplArchive.ModuleAbi.csproj", "src/SimplArchive.ModuleAbi/"]
COPY ["src/Alcifra.Presentation/Alcifra.Presentation.csproj", "src/Alcifra.Presentation/"]
COPY ["src/Alcifra.TextLayout/Alcifra.TextLayout.csproj", "src/Alcifra.TextLayout/"]
COPY ["src/Alcifra.HardwareKeys/Alcifra.HardwareKeys.csproj", "src/Alcifra.HardwareKeys/"]
COPY ["src/Alcifra.BrowserHost/Alcifra.BrowserHost.csproj", "src/Alcifra.BrowserHost/"]
COPY ["src/Alcifra.Sapass/Alcifra.Sapass.csproj", "src/Alcifra.Sapass/"]
COPY ["src/Alcifra.SealedArchive/Alcifra.SealedArchive.csproj", "src/Alcifra.SealedArchive/"]
COPY ["src/Alcifra.Theming/Alcifra.Theming.csproj", "src/Alcifra.Theming/"]
COPY ["src/Alcifra.Domain/Alcifra.Domain.csproj", "src/Alcifra.Domain/"]
COPY ["src/Alcifra.Application/Alcifra.Application.csproj", "src/Alcifra.Application/"]
COPY ["src/Alcifra.Infrastructure/Alcifra.Infrastructure.csproj", "src/Alcifra.Infrastructure/"]
COPY ["src/Alcifra.Auth/Alcifra.Auth.csproj", "src/Alcifra.Auth/"]
COPY ["src/Alcifra.Client/Alcifra.Client.csproj", "src/Alcifra.Client/"]
COPY ["src/Alcifra.Api/Alcifra.Api.csproj", "src/Alcifra.Api/"]
RUN dotnet restore "src/Alcifra.Api/Alcifra.Api.csproj" -p:GenerateBrandIcons=false

COPY . .
# -p:Version=$VERSION stamps the API assembly's InformationalVersion with the release tag so the /api discovery
# document reports the real server version (ADR 0512, for the desktop self-update check); defaults to 0.1.0 locally.
# The icons come from the icons stage; this Alpine stage cannot run the generator (GenerateBrandIcons=false).
COPY --from=icons /icons /icons
RUN dotnet publish "src/Alcifra.Api/Alcifra.Api.csproj" -c Release -o /app/publish --no-restore -p:Version=$VERSION \
    -p:GenerateBrandIcons=false -p:BrandIconsDir=/icons/

# (The desktop-client archives are built in their own stage below and copied into the final image — they used to
#  be produced here, which made the ARM leg build them under emulation. See the `clients` stage for why.)


# ── Desktop-client archives, built ONCE on the BUILD platform (#701) ─────────────────────────────────────────
#
# `--platform=$BUILDPLATFORM` is the whole point: this stage runs on the machine doing the building, never on
# the image's target architecture, so it is never emulated. What it produces — win-x64 and linux-x64 archives —
# is x64 content either way, so there was never a reason to produce it twice, let alone once under QEMU.
#
# Measured on v0.5.1, when this ran inside the per-architecture build stage:
#
#     linux/amd64    99 s
#     linux/arm64  1350 s      — 13.6x, emulated
#
# Twenty-two minutes of emulated ARM for artefacts identical to the ones the amd64 leg had just built. That one
# step was most of the margin against the publish job's `timeout-minutes: 60`: the v0.5.1 run was cancelled at
# exactly sixty minutes, having pushed the image three minutes earlier and been killed during the cache export.
#
# Both final images still carry the archives, so /download/clients/{windows,linux}/ keeps working and downloads
# stay in lock-step with the API image (ADR 0490). macOS is unaffected — a Linux build cannot produce a .dmg, so
# clients/macos/ links to the brand's release repository (ADR 0997).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS clients
ARG VERSION=0.0.0-dev
WORKDIR /src
COPY . .
COPY --from=icons /icons /icons
# python3 reads the brand for the packaging scripts (scripts/brand.sh, ADR 0994); this build stage only.
RUN apk add --no-cache bash zip tar python3 \
 && BRAND_ICONS_DIR=/icons/ bash scripts/package-windows-linux.sh "$VERSION"


FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
ARG VERSION=0.0.0-dev
WORKDIR /app

# icu-libs: proper globalization/culture handling for multi-tenant deployments with varying locales
# curl: used by the container HEALTHCHECK below
# krb5-libs: provides libgssapi_krb5.so.2, which Npgsql probes for at startup (GSSAPI/Kerberos auth); without it
#   the app logs a harmless "Error loading shared library libgssapi_krb5.so.2" on every boot even though we use
#   password auth. Adding it silences that noise.
# Non-root 'app' user/group already exist in this base image; no addgroup/adduser needed.
#
# `apk upgrade` FIRST, and it is not belt-and-braces: the base image is rebuilt on its own schedule, so between
# rebuilds it carries whatever its packages were at that moment. When an advisory lands against one of them —
# CVE-2026-14456 in libcrypto3/libssl3, HIGH, fixed in 3.5.8-r0 while the base still had 3.5.7-r0 — the image
# scan fails for a package we never chose and cannot bump, and it blocks EVERY pull request until the base
# happens to be refreshed. Upgrading at build time takes the patched package as soon as Alpine publishes it,
# which is both the real fix and one that keeps working for the next advisory of this kind rather than needing
# a fresh .trivyignore entry each time.
#
# The build and clients stages are deliberately left alone: they are SDK images that never ship, and only this
# final stage is published and scanned.
# tzdata is NOT optional for this application (#1138). Alpine ships no IANA database, so
# TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich") throws — and CalendarInstants catches that and falls
# back to the container's own zone, which is UTC. The result was silent and severe: an appointment published
# as 10:00-22:00 Europe/Zurich was STORED as 10:00Z-22:00Z instead of 08:00Z-20:00Z, so every zoned entry sat
# two hours from where it belonged, and conflict checks, availability and CalDAV interop all judged the wrong
# instants. Nothing failed; the times were simply wrong.
RUN apk upgrade --no-cache && apk add --no-cache icu-libs curl krb5-libs tzdata

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    ASPNETCORE_HTTP_PORTS=8080

COPY --from=build /app/publish .

# The archives from the build-platform stage. The .gitkeep placeholders that kept the (otherwise-empty) folders
# in git are removed so they do not clutter the served listing.
# Named from the brand's file stem (ADR 0994), which this file cannot read, hence the stem wildcard; the clients stage
# builds exactly one package per platform.
COPY --from=clients /src/dist/*-${VERSION}-win-x64.zip wwwroot/download/clients/windows/
COPY --from=clients /src/dist/*-${VERSION}-linux-x64.tar.gz wwwroot/download/clients/linux/
RUN rm -f wwwroot/download/clients/windows/.gitkeep wwwroot/download/clients/linux/.gitkeep

USER app
EXPOSE 8080

# Kubernetes deployments use their own separate liveness/readiness/startup probes against /health/live and
# /health/ready instead of relying on this; this HEALTHCHECK covers plain `docker run` / docker-compose usage.
HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health/ready || exit 1

ENTRYPOINT ["dotnet", "Alcifra.Api.dll"]
