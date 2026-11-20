using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Alcifra.ApiClient;
using Microsoft.Extensions.Logging;

namespace Alcifra.ManualCapture;

/// <summary>
/// The invented hardware keys and the sealed document behind the manual's sealed-audience figures (#2314).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every key is INVENTED</b> (owner decision 2026-11-11): the certificates in
/// <c>SealedAudienceCertificates.pem</c> are self-signed, belong to no real device, and were never read from a card.
/// The private halves do not exist here — the capture enrols them and seals TO them, and nothing ever opens the
/// document. All six 5-series models appear, spread across the demo tenant's people, each labelled with its model
/// and an invented serial ("YubiKey 5C NFC · 67 240 118").
/// </para>
/// <para>
/// <b>The demo tenant admits unattested hardware for this</b> (<c>Demo:Certificates:UnattestedAllowed</c>, set by the
/// capture harness alone; <c>DemoDataSeeder</c> reads it), because an unattested certificate is otherwise refused at
/// the enrolment door and left out of every reader answer (ADR 0963) — and a certificate nothing is sealed to could
/// not appear in the figures at all. <see cref="GuardAsync"/> then refuses the capture if the tenant holds any
/// certificate that is not one of these, so a figure can never show a real person's key.
/// </para>
/// <para>
/// <b>The document is sealed through the product's own client</b> — <see cref="DocumentsClient"/>'s begin → declare →
/// seal → file sequence to the audience's certificates as the server answers them — and then one member is removed
/// from the audience WITHOUT re-sealing: the envelope still names her keys, which is the audience gap the seal
/// inspection figure shows ("no longer in the audience, yet can open this version").
/// </para>
/// </remarks>
public static class SealedAudienceFixture
{
    /// <summary>The sealed document's name, as the tree, the audience dialog and the seal inspection show it.</summary>
    /// <remarks>Also named by <c>MainWindowViewModel.LiveShotSealedDocument</c> (the desktop cannot reference this
    /// harness) and by <c>WebCapture</c>, which finds the row by it — the three move together.</remarks>
    public const string DocumentName = "Board minutes 2026";

    // One invented key: which model, its invented serial, and whose it is (the demo tenant's people, by local part —
    // the KRISTALL household the manual's figures show).
    private sealed record InventedKey(string Model, string Serial, string HolderLocalPart);

    // All six 5-series models, in this order matching `SealedAudienceCertificates.pem` (asserted below): two people
    // carry two keys each, as a real desk does.
    private static readonly InventedKey[] Keys =
    [
        new("YubiKey 5 NFC", "88 010 431", "lukas"),
        new("YubiKey 5C", "31 418 027", "lukas"),
        new("YubiKey 5C NFC", "67 240 118", "sofia"),
        new("YubiKey 5Ci", "29 773 506", "sofia"),
        new("YubiKey 5 Nano", "12 805 662", "admin"),
        new("YubiKey 5C Nano", "44 193 907", "admin"),
    ];

    private static string Label(InventedKey key) => $"{key.Model} · {key.Serial}";

    /// <summary>The six fixed certificates, public halves only, read from this project's embedded copy.</summary>
    private static IReadOnlyList<X509Certificate2> Certificates()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Alcifra.ManualCapture.SealedAudienceCertificates.pem")
            ?? throw new InvalidOperationException("SealedAudienceCertificates.pem is not embedded (the .csproj lost it).");
        using var reader = new StreamReader(stream);
        var pem = reader.ReadToEnd();
        var certificates = new List<X509Certificate2>();
        var begin = pem.IndexOf("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal);
        while (begin >= 0)
        {
            // The slice runs through the END marker, trailing newline included — a block with only its BEGIN line is
            // malformed to the parser, which is how this fixture first failed against the live app.
            var end = pem.IndexOf("-----END CERTIFICATE-----", begin, StringComparison.Ordinal) + "-----END CERTIFICATE-----".Length;
            certificates.Add(X509Certificate2.CreateFromPem(pem.AsSpan(begin, end - begin).ToString()));
            begin = pem.IndexOf("-----BEGIN CERTIFICATE-----", end, StringComparison.Ordinal);
        }

        return certificates.Count == Keys.Length
            ? certificates
            : throw new InvalidOperationException(
                $"SealedAudienceCertificates.pem holds {certificates.Count} certificates for {Keys.Length} keys — the "
                + "file and SealedAudienceFixture.Keys are out of step.");
    }

    /// <summary>
    /// Enrols the invented keys, files the sealed document and removes one member from its audience without
    /// re-sealing. Answers the document's name for the capture steps to open.
    /// </summary>
    /// <remarks>
    /// A fresh stack per capture run, so this runs against an empty tenant and needs no idempotence; the guard makes
    /// the assumption it does rely on — that nobody else's certificate is enrolled — fail loudly rather than quietly
    /// putting a real key before a reader.
    /// </remarks>
    public static async Task<string> EnsureAsync(string baseUrl, string token, ILogger log)
    {
        ApiClientSettings.ApiRootUrl = baseUrl;
        var core = new ApiCore(token);
        var certificates = Certificates();

        await AllowEnrolmentAsync(core, log);

        for (var i = 0; i < Keys.Length; i++)
        {
            var key = Keys[i];
            using var response = await core.Http.PostAsJsonAsync("/api/certificates", new
            {
                certificatePem = certificates[i].ExportCertificatePem(),
                label = Label(key),
                holder = HolderEmail(key.HolderLocalPart),
            });
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"The fixture could not enrol '{Label(key)}': {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            }
        }

        await GuardAsync(core, certificates);
        log.LogInformation("[sealed-fixture] {Count} invented keys enrolled for the demo tenant", Keys.Length);
        await FileSealedDocumentAsync(core, certificates, log);
        return DocumentName;
    }

    private static string HolderEmail(string localPart) =>
        $"{localPart}@{SelfHosting.SelfHostedApp.AdminEmail.Split('@')[1]}";

    /// <summary>
    /// Chooses where reader certificates live, which the tenant must declare before anyone may enrol (ADR 0936/1021).
    /// The Demo Repository root, as the shared test helper (<c>TestKeyEnrolment</c>) does — NOT the core's Installation
    /// folder: that one is a core-owned exclusive folder admitting only its Public keys (ADR 1095), so a "Reader
    /// certificates" folder inside it is refused. Through the product's own settings endpoint, as an administrator would.
    /// </summary>
    private static async Task AllowEnrolmentAsync(ApiCore core, ILogger log)
    {
        using var read = await core.Http.GetAsync("/api/certificate-settings");
        if (!read.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"The fixture could not read the certificate settings: {(int)read.StatusCode} {await read.Content.ReadAsStringAsync()}");
        }

        var current = await read.Content.ReadFromJsonAsync<JsonElement>();
        if (current.TryGetProperty("parentFolderId", out var parent) && parent.ValueKind == JsonValueKind.String)
        {
            return;   // already declared (a second capture run against the same stack)
        }

        using var put = new HttpRequestMessage(HttpMethod.Put, "/api/certificate-settings")
        {
            Content = JsonContent.Create(new
            {
                selfServiceEnabled = current.GetProperty("selfServiceEnabled").GetBoolean(),
                parentFolderId = (await DemoRepositoryAsync(core)).GetProperty("id").GetGuid(),
                unattestedHardwareAllowed = current.GetProperty("unattestedHardwareAllowed").GetBoolean(),
            }),
        };
        put.Headers.TryAddWithoutValidation("If-Match", read.Headers.ETag?.Tag);
        using var saved = await core.Http.SendAsync(put);
        if (!saved.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"The fixture could not choose the reader-certificates folder: {(int)saved.StatusCode} {await saved.Content.ReadAsStringAsync()}");
        }

        log.LogInformation("[sealed-fixture] reader certificates live under the Demo Repository");
    }

    private static async Task<JsonElement> DemoRepositoryAsync(ApiCore core)
    {
        var repositories = await core.Http.GetFromJsonAsync<JsonElement>("/api/repositories");
        return repositories.GetProperty("repositories").EnumerateArray()
            .First(r => r.GetProperty("name").GetString() == "Demo Repository");
    }

    private static string Href(JsonElement resource, string rel) =>
        resource.GetProperty("links").EnumerateArray()
            .First(l => l.GetProperty("rel").GetString() == rel)
            .GetProperty("href").GetString()!;

    /// <summary>
    /// Refuses the capture when the tenant holds a certificate that is not one of the fixture's own (#2314): the
    /// figures are only honest if every key in them is invented, and this is the one place that can be checked.
    /// </summary>
    private static async Task GuardAsync(ApiCore core, IReadOnlyList<X509Certificate2> ours)
    {
        // The listing's thumbprint is SHA-256 of the DER (CertificateEnrolment), not X509Certificate2.Thumbprint's SHA-1.
        var expected = ours
            .Select(c => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(c.RawData)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = await core.Http.GetFromJsonAsync<JsonElement>("/api/certificates/tenant");
        var foreign = rows.EnumerateArray()
            .Where(row => !expected.Contains(row.GetProperty("thumbprint").GetString() ?? string.Empty))
            .Select(row => row.GetProperty("label").GetString() ?? string.Empty)
            .ToList();
        if (foreign.Count > 0)
        {
            throw new InvalidOperationException(
                "The capture's tenant holds certificate(s) the fixture did not make (" + string.Join(", ", foreign)
                + "). A figure must never show a real person's key — use a fresh fixture stack.");
        }
    }

    /// <summary>
    /// Files the sealed document the figures show, through the same steps a client takes: create it, declare its
    /// audience, seal on this machine to the audience's certificates as the server answers them, then remove one
    /// member from the audience without re-sealing — the gap the inspection figure shows.
    /// </summary>
    private static async Task FileSealedDocumentAsync(ApiCore core, IReadOnlyList<X509Certificate2> certificates, ILogger log)
    {
        var repository = await DemoRepositoryAsync(core);
        var children = Href(repository, "children");

        var documents = new DocumentsClient(core, () => throw new InvalidOperationException(
            "The fixture files one sealed document; reminders are not part of it."));
        var draft = await documents.BeginSealedDocumentAsync(children, DocumentName);

        // The manager (the caller) is the draft's first member; the two colleagues join it as readers.
        var audience = new ContentAudienceClient(core);
        List<AudienceChange> members = [.. draft.Audience.Members.Select(AudienceChange.Of)];
        foreach (var person in new[] { "Lukas Brunner", "Sofia Meier" })
        {
            var candidate = draft.Audience.Candidates.FirstOrDefault(c => c.DisplayName == person)
                ?? throw new InvalidOperationException(
                    $"'{person}' cannot be added to '{DocumentName}': the server offers no such reader. The audience is "
                    + $"sealed from the readers of the Demo Repository, and the seed grants it to {person}. Candidates: "
                    + $"{string.Join(", ", draft.Audience.Candidates.Select(c => c.DisplayName))}.");
            members.Add(AudienceChange.Of(candidate));
        }

        var saved = await documents.SaveDraftAudienceAsync(draft, members);
        if (saved.Audience.Recipients.SelectMany(r => r.CertificatePems).Distinct(StringComparer.Ordinal).Count() != Keys.Length)
        {
            throw new InvalidOperationException(
                $"The audience answers fewer certificates than the {Keys.Length} invented keys: {saved.Audience.WithoutCertificate.Count} "
                + $"member(s) hold none ({string.Join(", ", saved.Audience.WithoutCertificate.Select(r => r.DisplayName))}). "
                + "The tenant's unattested-hardware switch (Demo:Certificates:UnattestedAllowed) is what lets the server "
                + "answer them at all.");
        }

        var content = Encoding.UTF8.GetBytes(
            "Board minutes, 1 June 2026. Present: the chair, Lukas Brunner and Sofia Meier.\n"
            + "Resolved: the Seewelle renewal is signed as negotiated.\n");
        await documents.SealAndFileAsync(saved, content, "text/plain", $"{DocumentName}.txt");

        // THE GAP: Sofia leaves the audience, and the envelope is deliberately not re-sealed — her two keys still open
        // this version, which is what the inspection figure shows beside them. Re-read first: filing a version moves
        // the document's tag, and the save carries the tag of the read that filled the editor (ADR 0794).
        var sofia = saved.Audience.Members.First(m => m.DisplayName == "Sofia Meier").UserId;
        var current = await audience.GetAsync(saved.Audience.Links.Href("self")!);
        await audience.SaveAsync(current, [.. members.Where(m => m.UserId != sofia)]);
        log.LogInformation("[sealed-fixture] sealed '{Name}' for {Count} invented keys; Sofia Meier left the audience without a re-seal",
            DocumentName, certificates.Count);
    }
}
