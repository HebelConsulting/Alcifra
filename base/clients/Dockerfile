# The desktop-client packaging stage's base (#2325, ADR 1088): the .NET SDK on Alpine with bash, zip, tar and python3,
# installed here on the weekly base-image build. The main Dockerfile's clients stage builds FROM this image, pinned by
# digest.
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine
RUN apk add --no-cache bash zip tar python3
