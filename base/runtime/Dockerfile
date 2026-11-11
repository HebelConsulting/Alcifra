# The API image's runtime base (#2325, ADR 1088): what the final stage installed from Alpine at every build, installed
# here on the weekly base-image build (and on demand, when an advisory should not wait), pinned by digest in Dockerfile.
#
# `apk upgrade` takes Alpine's patched packages as they are published; weekly, a fix reaches the image within a week
# (owner, 2026-11-11). tzdata is NOT optional (#1138): Alpine ships no IANA database, and without it every zoned entry
# was stored in UTC. icu-libs for globalization, curl for the container's HEALTHCHECK, krb5-libs for the
# libgssapi_krb5.so.2 Npgsql probes for at startup (the reasons as the main Dockerfile gives them).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
RUN apk upgrade --no-cache && apk add --no-cache icu-libs curl krb5-libs tzdata
