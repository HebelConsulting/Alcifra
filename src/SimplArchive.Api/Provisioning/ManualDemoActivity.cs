using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Notifications;
using static SimplArchive.Api.Provisioning.ManualDemoSeeder;

namespace SimplArchive.Api.Provisioning;

/// <summary>
/// What people DID in the Manual tenant (#1787): the review task, annotations, tags, a conversation with a
/// mention, the notifications those produced, a shared link and a recycle bin with something in it.
/// </summary>
/// <remarks>
/// Every page of the manual that shows work in progress needs some, and an archive of untouched documents
/// photographs as an empty product. Each item is here because a chapter shows it.
/// </remarks>
internal static class ManualDemoActivity
{
    internal static async Task SeedAsync(ManualSeed seed, ManualTeam team, ManualTree tree)
    {
        var invoice = await AlpenglasAsync(seed, team, tree);

        await DemoDataSeeder.SeedExternalLinkAsync(seed.Db, seed.TenantId, tree.SeewelleAgreement.Id, seed.AdminId,
            seed.Now, "external-link/seewelle-agreement", "simplarchive-manual-seewelle-service-agreement-v1");

        await NotificationsAsync(seed, team, invoice);
        await RecycleBinAsync(seed, tree);
    }

    // The supplier folder carries the showcase: an invoice under review with three annotations, tags and a
    // conversation; a multi-page agreement; an offer in two versions to compare.
    private static async Task<Document> AlpenglasAsync(ManualSeed seed, ManualTeam team, ManualTree tree)
    {
        var offer = await seed.DocAsync(tree.Alpenglas.Id, "Offer 2026-022", "ManualAlpenglasOfferV1.pdf",
            new DateOnly(2026, 2, 9), at: seed.Now.AddDays(-3), comment: "First offer for the entrance façade.");
        await seed.NextVersionAsync(offer, "ManualAlpenglasOfferV2.pdf", new DateOnly(2026, 2, 20),
            seed.Now.AddDays(-2), "Revised: six panes instead of four, disposal added.");

        // Checked out by the administrator, so the Check-out lists (the desktop's tab, the phone's tile) show one.
        var agreement = await seed.DocAsync(tree.Alpenglas.Id, "Maintenance agreement WV-2026-07", "ManualAlpenglasMaintenance.pdf",
            new DateOnly(2026, 3, 25));
        agreement.CheckedOutByUserId = seed.AdminId;
        agreement.CheckedOutAt = seed.Now.AddHours(-2);
        await seed.Db.SaveChangesAsync();

        var invoice = await seed.DocAsync(tree.Alpenglas.Id, "Invoice AG-2026-031", "ManualAlpenglasInvoice.pdf",
            new DateOnly(2026, 3, 12), at: seed.Now.AddDays(-1));
        var version = await seed.CurrentVersionAsync(invoice);

        DemoDataSeeder.AddAnnotation(seed.Db, seed.TenantId, invoice.Id, version.Id, seed.AdminId, seed.Now,
            AnnotationKind.Highlight, 0.085, 0.395, 0.830, 0.030, string.Empty, "#ffd54a");
        DemoDataSeeder.AddAnnotation(seed.Db, seed.TenantId, invoice.Id, version.Id, seed.AdminId, seed.Now,
            AnnotationKind.Note, 0.085, 0.640, 0.330, 0.085, "Six panes, as in the revised offer ✓", "#fff59d");
        DemoDataSeeder.AddAnnotation(seed.Db, seed.TenantId, invoice.Id, version.Id, seed.AdminId, seed.Now,
            AnnotationKind.Stamp, 0.600, 0.700, 0.230, 0.090, "CHECKED", "#2e7d32");

        foreach (var (name, colour) in new[] { ("invoice", "#e53935"), ("contract", "#1e88e5"), ("urgent", "#fb8c00"), ("checked", "#43a047") })
        {
            seed.Db.TagDefinitions.Add(new TagDefinition
            {
                Id = DemoId.For(seed.TenantId, $"tag-def/{name}"),
                TenantId = seed.TenantId,
                Name = name,
                Color = colour,
                CreatedAt = seed.Now,
            });
        }

        foreach (var tag in new[] { "invoice", "checked" })
        {
            seed.Db.DocumentTags.Add(new DocumentTag
            {
                Id = DemoId.For(seed.TenantId, $"tag/alpenglas-invoice/{tag}"),
                TenantId = seed.TenantId,
                DocumentId = invoice.Id,
                Tag = tag,
                CreatedAt = seed.Now,
            });
        }

        await seed.Db.SaveChangesAsync();

        // Under review, assigned to the administrator and a day overdue: the Tasks list's first row.
        await DemoDataSeeder.AddInReviewWorkflowAsync(seed.Db, seed.TenantId, version.Id, seed.AdminId, seed.Now);
        await DemoDataSeeder.AddReferenceAsync(seed.Db, seed.TenantId, tree.March.Id, invoice.Id, seed.AdminId, seed.Now);

        // A question, a mention and a reply: Lukas asks the administrator by name, she answers in the thread.
        // Every entry is in the past and in order, so the thread reads the same on every capture (#832).
        var question = new ChatMessage
        {
            Id = DemoId.For(seed.TenantId, "chat/alpenglas-invoice/1"),
            TenantId = seed.TenantId,
            DocumentId = invoice.Id,
            Body = $"{ChatMentions.Token(seed.AdminId)} the invoice bills six panes — did we confirm six?",
            CreatedByUserId = team.LukasId,
            CreatedAt = seed.Now.AddHours(-3),
        };
        seed.Db.ChatMessages.Add(question);
        seed.Db.ChatMessageMentions.Add(new ChatMessageMention
        {
            Id = DemoId.For(seed.TenantId, "chat/alpenglas-invoice/1/mention"),
            TenantId = seed.TenantId,
            ChatMessageId = question.Id,
            UserId = seed.AdminId,
            CreatedAt = question.CreatedAt,
        });
        seed.Db.ChatMessages.Add(new ChatMessage
        {
            Id = DemoId.For(seed.TenantId, "chat/alpenglas-invoice/2"),
            TenantId = seed.TenantId,
            DocumentId = invoice.Id,
            ParentMessageId = question.Id,
            Body = "Yes — version 2 of the offer has six. I'll approve it once the delivery note is filed.",
            CreatedByUserId = seed.AdminId,
            CreatedAt = seed.Now.AddHours(-2),
        });
        await seed.Db.SaveChangesAsync();

        return invoice;
    }

    // What the bell shows the administrator: the mention and the review request, unread. Written as rows rather
    // than through the notification service, which would also send them by mail; EmailedAt says they were.
    private static async Task NotificationsAsync(ManualSeed seed, ManualTeam team, Document invoice)
    {
        var lukas = await seed.Db.Users.Where(u => u.Id == team.LukasId).Select(u => u.DisplayName).SingleAsync();

        foreach (var (slug, type, title, body, at) in new[]
                 {
                     ("review", NotificationType.ReviewAssigned, "Review requested", $"You've been asked to review '{invoice.Name}'.", seed.Now.AddDays(-1)),
                     ("mention", NotificationType.ChatMentioned, "You were mentioned", $"{lukas} mentioned you on '{invoice.Name}'.", seed.Now.AddHours(-3)),
                 })
        {
            seed.Db.Notifications.Add(new Notification
            {
                Id = DemoId.For(seed.TenantId, $"notification/admin/{slug}"),
                TenantId = seed.TenantId,
                RecipientUserId = seed.AdminId,
                Type = type,
                Title = title,
                Body = body,
                DocumentId = invoice.Id,
                CreatedAt = at,
                EmailedAt = at,
            });
        }

        await seed.Db.SaveChangesAsync();
    }

    // Two superseded papers in the recycle bin, so restore and delete-for-good have something to act on.
    private static async Task RecycleBinAsync(ManualSeed seed, ManualTree tree)
    {
        foreach (var (name, resource, date, daysAgo) in new[]
                 {
                     ("Press release — draft", "ManualPressReleaseDraft.pdf", new DateOnly(2026, 2, 2), 2),
                     ("Price list 2025", "ManualPriceList2025.pdf", new DateOnly(2025, 1, 1), 5),
                 })
        {
            var document = await seed.DocAsync(tree.General.Id, name, resource, date, at: seed.Now.AddDays(-30));
            document.DeletedAt = seed.Now.AddDays(-daysAgo);
        }

        await seed.Db.SaveChangesAsync();
    }
}
