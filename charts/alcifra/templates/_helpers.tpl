{{/* Expand the name of the chart. */}}
{{- define "alcifra.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/* Fully qualified app name. */}}
{{- define "alcifra.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- $name := default .Chart.Name .Values.nameOverride -}}
{{- if contains $name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{- define "alcifra.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "alcifra.labels" -}}
helm.sh/chart: {{ include "alcifra.chart" . }}
{{ include "alcifra.selectorLabels" . }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}

{{- define "alcifra.selectorLabels" -}}
app.kubernetes.io/name: {{ include "alcifra.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{- define "alcifra.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}
{{- default (include "alcifra.fullname" .) .Values.serviceAccount.name -}}
{{- else -}}
{{- default "default" .Values.serviceAccount.name -}}
{{- end -}}
{{- end -}}

{{- define "alcifra.image" -}}
{{- printf "%s:%s" .Values.image.repository (default .Chart.AppVersion .Values.image.tag) -}}
{{- end -}}

{{/*
Dependency URLs: when an in-cluster dep (ADR 0485) is enabled, point the app at its Service; otherwise use the
config.* value (external/managed). This is what lets `deps.<x>.enabled: true` auto-wire the app with no other change.
*/}}
{{- define "alcifra.gotenbergUrl" -}}
{{- if .Values.deps.gotenberg.enabled -}}http://{{ include "alcifra.fullname" . }}-gotenberg:3000{{- else -}}{{ .Values.config.gotenbergUrl }}{{- end -}}
{{- end -}}
{{- define "alcifra.tikaUrl" -}}
{{- if .Values.deps.tika.enabled -}}http://{{ include "alcifra.fullname" . }}-tika:9998{{- else -}}{{ .Values.config.tikaUrl }}{{- end -}}
{{- end -}}
{{- define "alcifra.ocrUrl" -}}
{{- if .Values.deps.ocr.enabled -}}http://{{ include "alcifra.fullname" . }}-ocr:8080{{- else -}}{{ .Values.config.ocrUrl }}{{- end -}}
{{- end -}}
{{- define "alcifra.valkeyConn" -}}
{{- if .Values.deps.valkey.enabled -}}{{ include "alcifra.fullname" . }}-valkey:6379{{- else -}}{{ .Values.config.valkeyConnectionString }}{{- end -}}
{{- end -}}
{{- define "alcifra.openSearchUrl" -}}
{{- if .Values.deps.opensearch.enabled -}}http://{{ include "alcifra.fullname" . }}-opensearch:9200{{- else -}}{{ .Values.config.openSearchUrl }}{{- end -}}
{{- end -}}
{{- define "alcifra.objectStorageServiceUrl" -}}
{{- if .Values.deps.seaweedfs.enabled -}}http://{{ include "alcifra.fullname" . }}-seaweedfs:8333{{- else -}}{{ .Values.config.objectStorage.serviceUrl }}{{- end -}}
{{- end -}}

{{/* Chart-generated Secret holding the in-cluster stateful deps' credentials (kiosk; phase 3 → OpenBao). */}}
{{- define "alcifra.statefulSecretName" -}}
{{- printf "%s-stateful" (include "alcifra.fullname" .) -}}
{{- end -}}
{{- define "alcifra.openBaoAddress" -}}
{{- if .Values.deps.openbao.enabled -}}http://{{ include "alcifra.fullname" . }}-openbao:8200{{- else -}}{{ .Values.config.openBao.address }}{{- end -}}
{{- end -}}
{{- define "alcifra.openBaoRoleId" -}}
{{- if .Values.deps.openbao.enabled -}}{{ .Values.deps.openbao.roleId }}{{- else -}}{{ .Values.config.openBao.roleId }}{{- end -}}
{{- end -}}
{{/*
Where the app reaches Postgres once OpenBao is issuing its credentials. OpenBao supplies the user and
password; the HOST is ours to state, and it is not always the in-cluster service: enabling OpenBao says
nothing about who runs the database. On the AWS installs it is RDS, and deps.openbao.externalDatabaseHost
already carries that address for OpenBao's own database engine — so hardcoding the in-cluster name here sent
the app to a Service that does not exist, and the migration failed with a bare "Name does not resolve" long
after the OpenBao half had visibly worked.
*/}}
{{- define "alcifra.openBaoDbTemplate" -}}
{{- if .Values.deps.openbao.enabled -}}Host={{ .Values.deps.openbao.externalDatabaseHost | default (printf "%s-postgres" (include "alcifra.fullname" .)) }};Port=5432;Database={{ .Values.deps.postgres.database }}{{- else -}}{{ .Values.config.openBao.databaseConnectionTemplate }}{{- end -}}
{{- end -}}

{{/* Component name for a dep workload/Service, e.g. "<fullname>-tika". */}}
{{- define "alcifra.depName" -}}
{{- printf "%s-%s" (include "alcifra.fullname" .root) .component -}}
{{- end -}}

{{/*
The non-secret application config as inline env. Used by both the Deployment and the migration Job (a pre-*
hook, which can't rely on a separately-created ConfigMap existing yet). Inline env also means a config change
rolls the Deployment automatically (the pod template changes). Secrets come from `existingSecret` via envFrom.
*/}}
{{- define "alcifra.appConfigEnv" -}}
- name: ASPNETCORE_ENVIRONMENT
  value: {{ .Values.config.aspnetCoreEnvironment | quote }}
- name: ASPNETCORE_URLS
  value: "http://+:8080"
- name: App__BaseUrl
  value: {{ .Values.config.app.baseUrl | quote }}
{{- /*
  Honour X-Forwarded-Proto/-Host, which is not optional behind an Ingress: TLS terminates at the ingress
  controller and the pod is reached over plain HTTP, so without this the application believes it is serving
  http://. OpenIddict keeps its transport-security requirement outside Development and refuses the
  authorization request, and the browser shows a bare "There was an error trying to log you in: ' (400)'" —
  LOGIN IS IMPOSSIBLE, on an install where every pod is ready and the health endpoint answers 200.

  It defaulted to off, and only the kiosk overlay and the compose stack ever turned it on, so the two places
  anyone actually logged in worked while the chart everybody else installs did not. Defaulting it to the
  ingress's own setting is the honest default: enabling an Ingress IS the statement that a proxy sits in
  front. Set config.app.trustProxyHeaders explicitly to override in either direction — needed when the
  Service is published directly, or when a proxy sits in front without this chart's Ingress.
*/}}
{{- $trustProxy := .Values.ingress.enabled }}
{{- if ne (typeOf .Values.config.app.trustProxyHeaders) "<nil>" }}
{{- $trustProxy = .Values.config.app.trustProxyHeaders }}
{{- end }}
- name: App__TrustProxyHeaders
  value: {{ $trustProxy | quote }}
{{- if .Values.config.app.knownProxyNetworks }}
- name: App__KnownProxyNetworks
  value: {{ .Values.config.app.knownProxyNetworks | quote }}
{{- end }}
{{- if .Values.config.app.knownProxies }}
- name: App__KnownProxies
  value: {{ .Values.config.app.knownProxies | quote }}
{{- end }}
- name: App__ApplyMigrationsAtStartup
  value: {{ .Values.config.app.applyMigrationsAtStartup | quote }}
{{- /* poolOverride lets the migration Job ask for a smaller pool than the serving pods (#750). */}}
{{- $pool := .poolOverride | default .Values.config.database.maxPoolSize }}
{{- if $pool }}
- name: Database__MaxPoolSize
  value: {{ $pool | quote }}
{{- end }}
{{- if .Values.imap.enabled }}
- name: Imap__Enabled
  value: "true"
- name: Imap__TlsPort
  value: {{ .Values.imap.tlsPort | quote }}
{{- with .Values.imap.publicTlsPort }}
- name: Imap__PublicTlsPort
  value: {{ . | quote }}
{{- end }}
{{- with .Values.imap.publicHost }}
- name: Imap__PublicHost
  value: {{ . | quote }}
{{- end }}
- name: Imap__IdleTimeoutSeconds
  value: {{ .Values.imap.idleTimeoutSeconds | quote }}
- name: Imap__PreAuthTimeoutSeconds
  value: {{ .Values.imap.preAuthTimeoutSeconds | quote }}
- name: Imap__MaxConnectionsPerUser
  value: {{ .Values.imap.maxConnectionsPerUser | quote }}
- name: Imap__MaxConnections
  value: {{ .Values.imap.maxConnections | quote }}
{{- end }}
{{- if .Values.lmtp.enabled }}
- name: Lmtp__Enabled
  value: "true"
- name: Lmtp__Port
  value: {{ .Values.lmtp.port | quote }}
- name: Lmtp__BindAddress
  value: {{ .Values.lmtp.bindAddress | quote }}
- name: Lmtp__MaxMessageBytes
  value: {{ .Values.lmtp.maxMessageBytes | int64 | quote }}
{{- end }}
- name: ObjectStorage__ServiceUrl
  value: {{ include "alcifra.objectStorageServiceUrl" . | quote }}
- name: ObjectStorage__PublicServiceUrl
  value: {{ .Values.config.objectStorage.publicServiceUrl | quote }}
- name: ObjectStorage__Region
  value: {{ .Values.config.objectStorage.region | quote }}
- name: ObjectStorage__BucketName
  value: {{ .Values.config.objectStorage.bucketName | quote }}
- name: ObjectStorage__BucketPrefix
  value: {{ .Values.config.objectStorage.bucketPrefix | quote }}
{{- with .Values.config.objectStorage.legacyBucketPrefix }}
- name: ObjectStorage__LegacyBucketPrefix
  value: {{ . | quote }}
{{- end }}
- name: Gotenberg__Url
  value: {{ include "alcifra.gotenbergUrl" . | quote }}
- name: OpenSearch__Url
  value: {{ include "alcifra.openSearchUrl" . | quote }}
- name: Tika__Url
  value: {{ include "alcifra.tikaUrl" . | quote }}
- name: Tika__OcrLanguages
  value: {{ .Values.config.tikaOcrLanguages | quote }}
- name: Ocr__Url
  value: {{ include "alcifra.ocrUrl" . | quote }}
- name: ConnectionStrings__Valkey
  value: {{ include "alcifra.valkeyConn" . | quote }}
{{- range $index, $network := .Values.config.outboundAllowedNetworks }}
- name: OutboundHttp__AllowedNetworks__{{ $index }}
  value: {{ $network | quote }}
{{- end }}
- name: Smtp__Host
  value: {{ .Values.config.smtp.host | quote }}
- name: Smtp__Port
  value: {{ .Values.config.smtp.port | quote }}
- name: Smtp__FromAddress
  value: {{ .Values.config.smtp.fromAddress | quote }}
- name: Smtp__FromName
  value: {{ .Values.config.smtp.fromName | quote }}
{{- if .Values.push.enabled }}
- name: PhonePush__ServerUrl
  value: {{ .Values.push.publicUrl | quote }}
{{- end }}
- name: OpenBao__Address
  value: {{ include "alcifra.openBaoAddress" . | quote }}
- name: OpenBao__RoleId
  value: {{ include "alcifra.openBaoRoleId" . | quote }}
- name: OpenBao__DatabaseConnectionTemplate
  value: {{ include "alcifra.openBaoDbTemplate" . | quote }}
{{- if .Values.deps.openbao.enabled }}
- name: OpenBao__SecretId
  valueFrom:
    secretKeyRef:
      name: {{ include "alcifra.fullname" . }}-openbao-approle
      key: OpenBao__SecretId
- name: OpenBao__DatabaseOwnerStaticRole
  value: "simplarchive-owner"
{{- /*
  The RUNTIME static role (ADR 0721, #1249). Program.cs gates the static-password provider on this being
  non-empty, so while it was unset the app silently took the DYNAMIC credential — read once at startup and
  revoked by Postgres at default_ttl, after which every new connection fails 28P01 until a restart.

  It is deliberately beside the owner's: the two were added by the same ADR and only one reached the chart,
  which is precisely the kind of half-applied change a reader cannot spot from either half alone.
*/}}
- name: OpenBao__DatabaseRuntimeStaticRole
  value: "simplarchive-runtime"
{{- end }}
- name: Bootstrap__PlatformAdministrator__Name
  value: {{ .Values.config.bootstrap.name | quote }}
- name: Bootstrap__PlatformAdministrator__ClientId
  value: {{ .Values.config.bootstrap.clientId | quote }}
{{- end -}}

{{/*
Hook annotations for the resources the migration Job depends on.

The migration Job is a pre-install hook, and Helm applies EVERY pre-install hook before it applies a single
ordinary resource. So anything the migration needs must be in that same phase at a lower weight, or it does
not exist yet when the migration runs. That is not a theoretical ordering: on a first install the chart used
to be circular — migrate (pre-install) needed OpenBao for its credentials and needed the roles db-init
creates, while both of those were applied after it. Upgrades hid it completely, because by then everything
exists from the release before.

The weights encode the real dependency chain, lowest first:

  -30  postgres      the database itself, when it is in-cluster
  -25  db-init       creates the simplarchive / _app / _vault roles the rest assume
  -20  openbao       needs those roles for its database engine
  -10  serviceaccount
   -5  migrate

Two consequences to know before using this. A hook resource is never part of the main release, so
`helm uninstall` does not reap it — harmless for a ServiceAccount or ConfigMap, and the volumes outlive an
uninstall anyway. And Helm's only delete policy for re-running a hook is before-hook-creation, so an upgrade
deletes and recreates these: the StatefulSets restart, their PVCs and data survive (a StatefulSet delete does
not touch its claims), and OpenBao unseals itself again through KMS. That restart is the price of having the
chart installable in one shot, which is what we sell.
*/}}
{{- define "alcifra.provisioningHook" -}}
"helm.sh/hook": pre-install,pre-upgrade
"helm.sh/hook-weight": {{ . | quote }}
"helm.sh/hook-delete-policy": before-hook-creation
{{- end -}}
