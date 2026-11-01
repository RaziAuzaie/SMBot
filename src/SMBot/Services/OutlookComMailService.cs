using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SMBot.Services;

public record PendingEmail(string EntryId, string Sender, string Subject, DateTime Received, int Priority)
{
    public string Age
    {
        get
        {
            var age = DateTime.Now - Received;
            return age.TotalDays >= 1 ? $"{(int)age.TotalDays}d" : age.TotalHours >= 1 ? $"{(int)age.TotalHours}h" : $"{(int)age.TotalMinutes}m";
        }
    }
}

public record EmailCheck(List<PendingEmail> Unread, List<PendingEmail> Unreplied);

public class OutlookNotReadyException(string message) : Exception(message);

// Reads classic Outlook through COM (late-bound, read-only) on one dedicated STA thread, in "check and close" mode:
// starts Outlook hidden, gives it a few seconds to sync, reads, then quits it again.
// Classic Outlook 2016 here only syncs fully while starting up (new mail and read/unread changes from New Outlook),
// so an Outlook left running hidden goes stale. An Outlook the user opened themselves is read as it is and left open.
public static class OutlookComMailService
{
    const int DaysBack = 14, SentDaysBack = 30;
    const int SyncSeconds = 8;
    const int OlMail = 43, OlMeetingRequest = 53, OlFolderInbox = 6, OlFolderSent = 5, OlTo = 1;
    const string RecipientSmtp = "http://schemas.microsoft.com/mapi/proptag/0x39FE001F";
    const string LastVerb = "http://schemas.microsoft.com/mapi/proptag/0x10810003";   // 102 reply, 103 reply all, 104 forward
    const string LastVerbTime = "http://schemas.microsoft.com/mapi/proptag/0x10820040"; // when that was done, in UTC
    const string Headers = "http://schemas.microsoft.com/mapi/proptag/0x007D001F";
    const string SenderSmtp = "http://schemas.microsoft.com/mapi/proptag/0x5D01001F";

    static readonly string[] NoReplySenders = ["noreply", "no-reply", "donotreply", "do-not-reply", "mailer-daemon", "postmaster", "notification"];
    static readonly string[] AutoSubjects = ["automatic reply", "auto:", "out of office", "undeliverable", "delivery status notification",
                                             "read:", "not read:", "accepted:", "declined:", "tentative:", "receipt"];
    static readonly string[] RequestPhrases = ["?", "please", "pls", "kindly", "can you", "could you", "would you", "need your", "let me know",
                                               "advise", "urgent", "asap", "tolong", "mohon", "boleh", "sila", "minta", "perlu"];

    static bool _startedByUs;          // never quit an Outlook the user opened themselves
    static DateTime _startedAt, _lastQuit;

    public static async Task<EmailCheck> CheckAsync(IReadOnlySet<string> dismissed)
    {
        if (Type.GetTypeFromProgID("Outlook.Application") is null)
            throw new OutlookNotReadyException("Classic Outlook isn't installed.");
        // Starting Outlook without an account only opens its setup window, so don't
        if (!IsOutlookRunning() && !HasMailAccount())
            throw new OutlookNotReadyException("No email account is set up in classic Outlook yet.");

        try
        {
            // A first-run setup screen would block forever, so give up after 2 minutes
            return await RunSta(() => Read(dismissed)).WaitAsync(TimeSpan.FromMinutes(2));
        }
        catch (TimeoutException)
        {
            if (_startedByUs)
                foreach (var p in Process.GetProcessesByName("OUTLOOK").Where(p => p.StartTime >= _startedAt.AddSeconds(-5)))
                    p.Kill();
            throw new OutlookNotReadyException("Classic Outlook didn't respond. Is your email account set up in it?");
        }
    }

    static EmailCheck Read(IReadOnlySet<string> dismissed)
    {
        // The Outlook quit by the last check can take a few seconds to exit; don't mistake it for the user's
        for (var i = 0; i < 20 && IsOutlookRunning() && DateTime.Now - _lastQuit < TimeSpan.FromSeconds(30); i++)
            Thread.Sleep(TimeSpan.FromSeconds(1));

        _startedByUs = !IsOutlookRunning();
        _startedAt = DateTime.Now;
        var app = StartOutlook();
        try
        {
            dynamic ns = app.GetNamespace("MAPI");
            if (_startedByUs) Thread.Sleep(TimeSpan.FromSeconds(SyncSeconds));   // let the startup sync finish
            var since = DateTime.Now.AddDays(-DaysBack).ToUniversalTime().ToString("yyyy-MM-dd HH:mm");
            dynamic inboxItems = ns.GetDefaultFolder(OlFolderInbox).Items;
            return new(ReadUnread(inboxItems, since), ReadUnreplied(ns, inboxItems, since, dismissed));
        }
        finally
        {
            if (_startedByUs)
            {
                try { app.Quit(); } catch (COMException) { }
                _lastQuit = DateTime.Now;
            }
            Marshal.FinalReleaseComObject(app);
        }
    }

    // Outlook rejects new connections for a few seconds while it's still closing from the last check
    static dynamic StartOutlook()
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return Activator.CreateInstance(Type.GetTypeFromProgID("Outlook.Application")!)!; }
            catch (COMException ex) when (attempt < 10 && (uint)ex.HResult is 0x80010001 or 0x80080005)
            {
                Thread.Sleep(TimeSpan.FromSeconds(3));
            }
        }
    }

    static List<PendingEmail> ReadUnread(dynamic inboxItems, string since)
    {
        var result = new List<PendingEmail>();
        dynamic unread = inboxItems.Restrict($"@SQL=\"urn:schemas:httpmail:datereceived\" >= '{since}' AND \"urn:schemas:httpmail:read\" = 0");
        for (var i = 1; i <= (int)unread.Count; i++)
        {
            dynamic item = unread.Item(i);
            if ((int)item.Class is not (OlMail or OlMeetingRequest)) continue;
            string sender = item.SenderName ?? "";
            result.Add(new PendingEmail(item.EntryID, sender, item.Subject ?? "", item.ReceivedTime, 0));
        }
        return result.OrderByDescending(e => e.Received).ToList();
    }

    // Unreplied = follow-ups: conversations I already replied in, where a newer email has arrived
    // since my last reply, addressed to me directly in To, and I haven't answered it yet
    static List<PendingEmail> ReadUnreplied(dynamic ns, dynamic inboxItems, string since, IReadOnlySet<string> dismissed)
    {
        // My SMTP addresses, Exchange address and display name, to spot me among senders and recipients
        string myName = ns.CurrentUser.Name;
        var me = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { myName, (string)ns.CurrentUser.Address ?? "" };
        for (var i = 1; i <= (int)ns.Accounts.Count; i++)
            me.Add((string)ns.Accounts.Item(i).SmtpAddress);

        // Latest time I sent something in each conversation. Looks further back than the inbox,
        // so a reply to an older conversation still counts
        var sentSince = DateTime.Now.AddDays(-SentDaysBack).ToUniversalTime().ToString("yyyy-MM-dd HH:mm");
        var lastSent = new Dictionary<string, DateTime>();
        dynamic sent = ns.GetDefaultFolder(OlFolderSent).Items.Restrict($"@SQL=\"urn:schemas:httpmail:date\" >= '{sentSince}'");
        for (var i = 1; i <= (int)sent.Count; i++)
        {
            dynamic item = sent.Item(i);
            if ((int)item.Class != OlMail) continue;
            string conv = item.ConversationID;
            DateTime sentOn = item.SentOn;
            if (!lastSent.TryGetValue(conv, out var t) || sentOn > t) lastSent[conv] = sentOn;
        }

        // Replied marks on received emails count as my replies too. Classic Outlook can be slow to sync
        // Sent Items, while the marks set by New Outlook arrive with the Inbox
        dynamic replied = inboxItems.Restrict(
            $"@SQL=\"{LastVerb}\" >= 102 AND \"{LastVerb}\" <= 104 AND \"urn:schemas:httpmail:datereceived\" >= '{sentSince}'");
        for (var i = 1; i <= (int)replied.Count; i++)
        {
            dynamic item = replied.Item(i);
            string conv = item.ConversationID;
            DateTime repliedOn = Prop(item, LastVerbTime) is DateTime utc ? utc.ToLocalTime() : item.ReceivedTime;
            if (!lastSent.TryGetValue(conv, out var t) || repliedOn > t) lastSent[conv] = repliedOn;
        }

        // My own emails that come back to my Inbox (e.g. sent to a group I'm in) are my replies too
        var incoming = new List<(string Conv, dynamic Item, DateTime Received)>();
        dynamic inbox = inboxItems.Restrict($"@SQL=\"urn:schemas:httpmail:datereceived\" >= '{since}'");
        for (var i = 1; i <= (int)inbox.Count; i++)
        {
            dynamic item = inbox.Item(i);
            if ((int)item.Class != OlMail) continue;
            string conv = item.ConversationID;
            DateTime received = item.ReceivedTime;
            if (me.Contains((string)item.SenderName ?? ""))
            {
                if (!lastSent.TryGetValue(conv, out var t) || received > t) lastSent[conv] = received;
                continue;
            }
            incoming.Add((conv, item, received));
        }

        // Newest incoming message in each conversation I've written in
        var newest = new Dictionary<string, (dynamic Item, DateTime Received)>();
        foreach (var (conv, item, received) in incoming)
        {
            if (!lastSent.ContainsKey(conv)) continue;
            if (!newest.TryGetValue(conv, out var n) || received > n.Received) newest[conv] = (item, received);
        }

        var result = new List<PendingEmail>();
        foreach (var (conv, (item, received)) in newest)
        {
            Dbg($"{received:dd/MM HH:mm} mine={lastSent[conv]:dd/MM HH:mm} {(string)item.Subject}"); if (lastSent[conv] > received) { Dbg("  drop: mine newer"); continue; }   // my reply is newer, nothing to answer
            string entryId = item.EntryID;
            if (dismissed.Contains(entryId)) continue;
            if (Prop(item, LastVerb) is int verb && verb is 102 or 103 or 104) { Dbg("  drop: verb"); continue; }
            if (!IsMeInTo(item, me)) { Dbg("  drop: not to me"); continue; }

            string subject = item.Subject ?? "";
            string senderAddress = item.SenderEmailAddress ?? "";
            var sender = (Prop(item, SenderSmtp) as string ?? senderAddress).ToLowerInvariant();
            var headers = (Prop(item, Headers) as string ?? "").ToLowerInvariant();

            Dbg($"  sender={sender}"); if (me.Contains(sender)) { Dbg("  drop: me"); continue; }
            if (NoReplySenders.Any(sender.Contains)) { Dbg("  drop: noreply"); continue; }
            if (headers.Contains("list-unsubscribe:") || (headers.Contains("auto-submitted:") && !headers.Contains("auto-submitted: no"))) { Dbg("  drop: headers"); continue; }
            if (AutoSubjects.Any(s => subject.StartsWith(s, StringComparison.OrdinalIgnoreCase) || (s == "receipt" && subject.Contains(s, StringComparison.OrdinalIgnoreCase)))) continue;

            Dbg("  KEEP"); string senderName = item.SenderName ?? sender;
            int score = Score(item, subject, received, myName);
            result.Add(new PendingEmail(entryId, senderName, subject, received, score));
        }

        return result.OrderByDescending(e => e.Priority).ThenBy(e => e.Received).ToList();
    }

    static int Score(dynamic item, string subject, DateTime received, string myName)
    {
        string to = item.To ?? "";
        string cc = item.CC ?? "";
        string body = item.Body ?? "";
        var text = (subject + " " + (body.Length > 600 ? body[..600] : body)).ToLowerInvariant();

        var score = 0;
        if (to.Equals(myName, StringComparison.OrdinalIgnoreCase)) score += 3;                 // only me in To
        if ((int)item.Importance == 2) score += 3;                                             // high importance
        if ((int)item.FlagStatus == 2) score += 3;                                             // flagged
        if (RequestPhrases.Any(text.Contains)) score += 2;                                     // question or request
        if (DateTime.Now - received > TimeSpan.FromDays(2)) score += 2;                         // waiting a while
        if (!to.Contains(myName, StringComparison.OrdinalIgnoreCase) && cc.Contains(myName, StringComparison.OrdinalIgnoreCase)) score -= 3; // only on CC
        if ((int)item.Recipients.Count > 10) score -= 2;                                       // mass email
        if (subject.StartsWith("fyi", StringComparison.OrdinalIgnoreCase)) score -= 3;
        return score;
    }

    // True when I'm named myself in To (not just reached through a group like "Support Maintenance")
    static bool IsMeInTo(dynamic item, HashSet<string> me)
    {
        dynamic recipients = item.Recipients;
        for (var i = 1; i <= (int)recipients.Count; i++)
        {
            dynamic r = recipients.Item(i);
            if ((int)r.Type != OlTo) continue;
            string address = r.Address ?? "", name = r.Name ?? "";
            if (me.Contains(address) || me.Contains(name) || (Prop(r, RecipientSmtp) is string smtp && me.Contains(smtp)))
                return true;
        }
        return false;
    }

    static void Dbg(string line) => System.IO.File.AppendAllText(
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SMBot", "debug.log"), line + "\n");

    static object? Prop(object item, string schema)
    {
        try { return ((dynamic)item).PropertyAccessor.GetProperty(schema); }
        catch (COMException) { return null; }
    }

    static bool IsOutlookRunning() => Process.GetProcessesByName("OUTLOOK").Length > 0;

    // True when any classic Outlook mail profile has an email account in it (read from the registry, without starting Outlook)
    static bool HasMailAccount()
    {
        const string AccountsKey = "9375CFF0413111d3B88A00104B2A6676";
        using var profiles = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Office\16.0\Outlook\Profiles");
        if (profiles is null) return false;
        foreach (var profile in profiles.GetSubKeyNames())
        {
            using var accounts = profiles.OpenSubKey($@"{profile}\{AccountsKey}");
            if (accounts is null) continue;
            foreach (var account in accounts.GetSubKeyNames())
            {
                using var key = accounts.OpenSubKey(account);
                if (key?.GetValue("Account Name") is not null) return true;
            }
        }
        return false;
    }

    // Outlook COM needs a single-threaded apartment thread of its own, away from the UI.
    // A fresh thread per check: when it ends, every COM reference it held is released, which lets the quit Outlook exit
    static Task<T> RunSta<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true, Name = "Outlook" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
