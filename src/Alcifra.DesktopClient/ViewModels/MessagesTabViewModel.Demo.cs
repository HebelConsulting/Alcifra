using Alcifra.DesktopClient.Services;
using Alcifra.Messenger.Client;
using Alcifra.Messenger.Client.Api;
using Alcifra.Messenger.Client.Display;

namespace Alcifra.DesktopClient.ViewModels;

/// <summary>The headless screenshots' demo conversations (<c>--screenshot --demo --messages</c>), in their own part.</summary>
public sealed partial class MessagesTabViewModel
{
    /// <summary>
    /// Demo conversations for the headless hook: a group with a reply, two files in one message, a receipt and a
    /// restored conversation, the composer replying with a file waiting as a chip — built through the same display rules
    /// as a real thread, with no server. <paramref name="pane"/> picks the right pane: the thread, a group's members, or
    /// the people picker making a new group.
    /// </summary>
    public void PopulateDemoForScreenshot(MessagesPane pane = MessagesPane.Thread)
    {
        var (me, tom, ina) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var start = DateTimeOffset.Now.Date.AddHours(9);
        Member Of(Guid id, string name, bool admin = false, DateTimeOffset? read = null)
        {
            var member = new Member { UserId = id, DisplayName = name, IsAdmin = admin, ReadUpTo = read };
            member.Links.Add(new Link("remove", "/demo", "POST"));
            member.Links.Add(new Link(admin ? "revoke-admin" : "make-admin", "/demo", "POST"));
            return member;
        }

        var crew = new Conversation
        {
            Id = Guid.NewGuid(),
            Name = "Flight crew",
            Members = [Of(me, "Anna", admin: true), Of(tom, "Tom", read: start.AddMinutes(30)), Of(ina, "Ina")],
        };
        crew.Links.Add(new Link("add-member", "/demo", "POST"));
        Message At(Guid sender, int minutes, Guid? replyTo = null)
        {
            var message = new Message { Id = Guid.NewGuid(), SenderUserId = sender, SentAt = start.AddMinutes(minutes), ReplyToId = replyTo };
            message.Links.Add(new Link("hide", "/demo", "POST"));
            return message;
        }

        var hello = new OpenedMessage(At(tom, 0), "Briefing moved to 10:30, hangar 2.", MessageState.Readable);
        List<SentFile> route = [new("route-LSZH-EDDM.pdf", "application/pdf"), new("weather-1030.png", "image/png")];
        var opened = new List<OpenedMessage>
        {
            hello,
            new(At(me, 4, hello.Message.Id), "Thanks, I'll bring the weather sheet.", MessageState.Readable),
            new(At(ina, 9), "Here is the route.", MessageState.Readable) { File = route[0], Files = route },
            new(At(me, 12), "Looks good.", MessageState.Readable),
        };
        Conversations.Clear();
        Conversations.Add(new MessagesConversationViewModel { Conversation = crew });
        Conversations.Add(new MessagesConversationViewModel { Conversation = new Conversation { Name = "Tom", IsDirect = true, CounterpartName = "Tom", Unread = 2 } });
        Conversations.Add(new MessagesConversationViewModel { Conversation = new Conversation { Name = "Club (restored)", Restored = true, IsReadOnly = true } });

        // A session that never reaches a server: the screenshot draws, it does not read.
        var session = new ChatSession(new MessengerApi(Alcifra.ApiClient.ApiCore.Anonymous), new ConversationKeyOpener(), me, "anna@example.com");
        var thread = new MessengerThreadViewModel(session, crew, _live, _shell, Dialogs, new MessengerThreadHost(_ => { }, _ => { }, _ => { }, () => { }, () => { }));
        var rows = ThreadRows.Build(crew, opened, me);
        thread.ShowForScreenshot(rows);
        thread.Composer.StartReply(rows[0], "Tom");
        thread.Composer.Add(new PickedFile("fuel-plan.xlsx", new byte[48_000]));
        thread.Composer.Text = "Fuel plan attached";
        thread.TypingLine = Alcifra.Messenger.Client.Display.TypingIndicator.Wording(["Tom"]) ?? string.Empty;   // as a live event would show it (#26)
        Selected = Conversations[0];   // no session: the selection marks the row and builds no thread of its own
        (Thread, IsOffered) = (thread, true);
        Pane = pane;
        if (pane == MessagesPane.File)
        {
            // A plain text opened from memory: drawn at once, so the screenshot need not wait for it.
            var text = "Weather briefing LSZH 10:30\n\nWind 240/12, visibility 10 km, few clouds at 4500 ft.\nQNH 1018."u8.ToArray();
            File = MessengerFileViewModel.OpenAsync(text, new SentFile("weather-1030.txt", "text/plain"), filesStayInApp: false, Dialogs, _shell, () => { })
                .GetAwaiter().GetResult();
        }
        if (pane == MessagesPane.Members)
        {
            Members = new MessengerMembersViewModel(session, crew, Dialogs, _ => { }, () => { }, () => { }, _ => { });
            foreach (var row in MemberRows.Build(crew, me))
            {
                Members.Members.Add(new MessengerMemberViewModel(row));
            }

            Members.CanAdd = true;
        }
        else if (pane == MessagesPane.People)
        {
            People = new MessengerPeopleViewModel(session, MessengerPeopleMode.Group, new ConversationList(), null, Dialogs, _ => { }, _ => { })
            {
                GroupName = "Hangar 2",
            };
            foreach (var (name, chosen) in new[] { ("Ina Berger", true), ("Tom Keller", true), ("Lea Wolf", false) })
            {
                People.People.Add(new MessengerPersonViewModel(new Person
                {
                    Id = Guid.NewGuid(),
                    DisplayName = name,
                    Email = $"{name.Split(' ')[0].ToLowerInvariant()}@example.com",
                })
                { IsChosen = chosen });
            }
        }
    }
}
