# The mail relay's base (#2325, ADR 1088): Postfix with its PostgreSQL maps, installed here on the weekly base-image
# build. postfix/Dockerfile builds FROM this image, pinned by digest.
FROM alpine:3.21
RUN apk add --no-cache postfix postfix-pgsql ca-certificates
