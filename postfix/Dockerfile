# The MTA (ADR 0628): it terminates SMTP from the open internet, applies the protections mature mail software
# has spent thirty years developing, and hands us LMTP on a private network. We never see a byte that has not
# already been through software designed for that job.
#
# Built from the distribution's own package rather than pulled from a community image (ADR 0611): its provenance is
# Alpine's package database. Installed in base/postfix (with postfix-pgsql, the map type that reads the accepted domains
# straight out of the application's own table, ADR 0628), built weekly into our registry (#2325, ADR 1088).
FROM ghcr.io/hebelconsulting/alcifra-base-postfix@sha256:38e1f76c62c2156a417b49a02790c8c107c2dd3980b9019478e40b011a954117

COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

# 25 is not published to the host by the compose stack. Reaching this from the internet is a deliberate,
# documented act by an operator who controls a public IP, correct reverse DNS and an MX record.
EXPOSE 25

ENTRYPOINT ["/entrypoint.sh"]
