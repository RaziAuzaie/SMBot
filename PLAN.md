# SMBot — Plan

A Clippy-style animated desktop assistant for Windows that acts as a personal assistant for support-maintenance work:
emails, reminders and a checklist, recent files, fast file search, and (planned) meeting reminders.

## Status (as of 2026-11-01)
| Area | Status |
|---|---|
| Character (3D "Soft Satin" look), onboarding, greetings, moods | ✅ Built |
| Left-click action list, right-click menu, drag, status peek on hover | ✅ Built |
| Feature 1: unread and unreplied emails | ✅ Built, ⚠️ not working yet: classic Outlook has no email account set up |
| Feature 2: calendar and Teams meetings | ⏳ Not started (needs classic Outlook, or another source, see Feature 2) |
| Feature 3: reminders and checklist | ✅ Core built; smart delivery, sub-tasks, tags, daily routine still planned |
| Feature 4: recent files | ✅ Built |
| Feature 5: file search (Ctrl + Alt + F) | ✅ Built |
| Single instance, app icon, crash log | ✅ Built |
| Settings window, tray icon, toasts, start with Windows | ⏳ Not built |

## Core rule: local only
- Runs fully on this PC. **No Claude/Anthropic API, no cloud AI, no telemetry, no auto-update.**
- "SMBot" (SM = Support Maintenance) is only the character's name and look.
- Data comes from Outlook on this PC (COM) and Windows itself. Settings and reminders are saved locally.
- Anything that would talk to an external server (for example Microsoft Graph, a published calendar link, or Outlook To Do sync)
  is off by default and needs explicit approval.

## Tech stack
| Part | Choice |
|---|---|
| App | C# + WPF on .NET 10 (SDK 10.0.403 installed 2026-10-30) |
| Outlook data | Outlook COM, late-bound (`dynamic`), read-only, on its own STA thread |
| Teams meetings | Planned: read from the Outlook calendar (Teams join links in the meeting details) |
| Character art | Three.js model pre-rendered to transparent PNG frames (see Character) |
| Local storage | `settings.json`, `reminders.json`, `error.log` under `%LOCALAPPDATA%\SMBot\` (JSON instead of SQLite, so no extra package) |
| Packaging | `dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true` → one small `SMBot.exe` (uses the installed .NET 10 runtime), installed to `%LOCALAPPDATA%\Programs\SMBot\`, with a desktop shortcut |
| Not used yet | Toast notifications (`Microsoft.Toolkit.Uwp.Notifications`) and tray icon (`H.NotifyIcon.Wpf`) are planned |

### New Outlook + classic Outlook
- The user works in **New Outlook**. SMBot reads through **classic Outlook**, because New Outlook has no local interface an app can read.
- Classic Outlook 2016 is installed, but as of 2026-11-01 no email account is set up in it.
  Outlook 2016 is out of support and may not connect to Microsoft 365; if so, IT needs to install a newer classic Outlook (Microsoft 365 Apps).
- Mode: **check and close.** Every 60 seconds (`emailCheckSeconds`) SMBot starts classic Outlook hidden, waits about 8 seconds for its
  startup sync, reads, then quits it. Classic Outlook 2016 only syncs fully while starting up, so one left running hidden goes stale.
  An Outlook the user opened themselves is read as it is and left open.
- **No account, no Outlook:** before starting Outlook, SMBot checks the registry for an email account in any Outlook profile
  (`HKCU\Software\Microsoft\Office\16.0\Outlook\Profiles\*\9375CFF0413111d3B88A00104B2A6676`). Without one it doesn't start Outlook,
  because that only opens the setup window.
- **Stops after a failure:** if a check fails (no account, Outlook not responding within 2 minutes, any error), automatic checks stop and the bot
  says so once. "Check emails" in the action list tries again, and a successful check turns automatic checks back on.
- One-time setup in classic Outlook to avoid duplicate pop-ups: turn off Desktop Alerts and Reminders.

## Character
- A sleek, egg-shaped floating robot in the style of EVE from *WALL·E*, in **light purple**: a floating head with a big glossy
  black visor and glowing eyes, a short egg body, fins for arms, no legs (it hovers over a soft purple glow).
- It's an original design *inspired by* EVE, not a copy of the Disney/Pixar character, and uses no official artwork.
- **3D look: "Soft Satin"** (chosen 2026-10-31 from three concepts in `design/concept-*.png`): satin lavender shell (#C2B0F0),
  deep purple-black visor (#141026), glowing lavender eyes. Proportions tuned with the user: head scaled 1.22×, short body,
  visor radii 0.570 × 0.389 so the eyes sit inside it with a lavender rim showing, soft fill light so there's no dark band under the head.
- How it's made: modelled in Three.js (`design/smbot-sprites.html`) and pre-rendered by `design/render-sprites.ps1` (headless Edge)
  into 22 transparent PNG frames (460 × 520, shown at 230 × 260) in `src/SMBot/Assets/Bot/`. Real-time 3D isn't used because
  WebView2 can't draw into the transparent, always-on-top window.
- Expressions and animations (built): neutral, blink (every 3–6 s), happy `^ ^`, curious (head tilt), surprised (wide eyes),
  sleepy (after 1 minute idle), wave (4 frames), celebrate spin (12 frames). Expressions crossfade; hover bob, hop with squash
  and stretch, and sparkles are done in WPF.
- Planned: Pointing, Stretch, seasonal outfits, "remembers its position", hides at the screen edge.

### Interaction (built)
- **Left click:** "What would you like to do, {Name}?" with **Add reminder, Checklist, Recent files, Check emails, Search files**.
  Clicking again closes it. A click with a few pixels of hand wobble still counts as a click; more is a drag.
- **Right click:** only **Reset onboarding** and **Exit**.
- **Drag** to move it. Always on top, not on the taskbar.
- **Status peek:** hovering over the bot (0.7 s), or every 10 minutes while the bubble is closed (only if something is open),
  shows for 5 seconds:
  "{Name}, you have 2 reminders and 1 checklist item open (1 overdue)." / "Next: Honda car maintenance" / "11 Nov · 9:00 AM".
  It never covers typing, a due reminder or an open bubble.
- **Hotkeys:** `Ctrl + Alt + R` quick capture, `Ctrl + Alt + F` file search.
- **Single instance:** starting `SMBot.exe` again doesn't add a bot. The running one comes forward (back on screen if needed) and shows
  its action list. `SMBot.exe --checklist` opens the checklist (in the running bot if there is one).

## First-run onboarding (personalisation)
Shown the very first time SMBot starts, when no `settings.json` exists yet or `userName` is empty.

1. SMBot waves and the speech bubble says:
   **"Hi there! I'm SMBot, your personal assistant. What is your name?"**
2. A text box is pre-filled with a suggestion: the first name from the Windows account display name, read locally
   (generic names like "Admin" are left blank). The user can keep it or type another name or nickname.
3. The name is checked: it must not be empty, it is trimmed, and it can be at most 30 characters. If the box is empty, an inline hint says
   "Type a name so I know what to call you".
4. SMBot confirms: **"Nice to meet you, {Name}! I'll keep an eye on your emails and meetings."** (Celebrate animation.)
5. Saved to `settings.json`, for example `"userName": "Razi"`, and onboarding never shows again. Right click → Reset onboarding restarts it.

Planned: a short optional setup step (working hours, email check interval, VIP senders) and changing the name from Settings or by saying "call me ...".

The name is used everywhere the bot speaks: "Good morning, {Name}!", "{Name}, time to send the invoice to Lim!", and so on.

## Feature 1: unread and unreplied emails
Scan window: Inbox emails from the last 14 days. Your earlier replies are looked up over the last 30 days. Older mail is ignored.

The speech bubble has two tabs:
1. **Unread** (first tab): Inbox emails and meeting invites from the last 14 days that haven't been read, newest first.
   Reading an email in New Outlook syncs through the server, and it drops off the list on the next check.
2. **Unreplied** (follow-ups), with a **✓ Done** button. An email is listed only when all of these are true:
   - You already replied in that conversation before, within the last 30 days. This comes from a sent item with the same ConversationID,
     or from a "replied" mark (with its time) on a received email. The marks matter because classic Outlook can be slow to sync Sent Items.
   - A newer email arrived in the same conversation **after** your last reply.
   - You are named yourself in **To**. Being reached through a group such as "Support Maintenance", or being only on CC, doesn't count.
   - You haven't answered it yet: no newer sent item in the conversation, and no replied mark on it.
   New conversations you have never replied to only show in Unread. Ticket notifications (for example Careline) drop out
   automatically, because you don't reply to them.

When a new unread email arrives, SMBot alerts with "{Name}, new email from {Sender}!" and opens the Unread tab.
An open list refreshes quietly on every check.

How "replied" is detected:
1. Outlook's hidden replied mark `PR_LAST_VERB_EXECUTED` (0x10810003): 102 reply, 103 reply all, 104 forward.
2. A sent item in the same conversation (ConversationID) sent after the email arrived.
3. Only the newest incoming message in each thread counts. Threads are grouped so each one appears once.

Filters (applied on top of the follow-up rule):
- Skipped: newsletters (`List-Unsubscribe` header), noreply senders, auto-replies, meeting items, receipts, bounces, own emails.
- Lower priority: only on CC, sent to more than 10 people, subjects starting with "FYI".
- Higher priority: you are the only person in To, High importance, flagged, a question or a request phrase (English/Malay), waiting more than 2 days.

Planned: Snooze, "Never show from this sender", VIP senders, learning ("Hide emails from X from now on?"),
"waiting on others" (emails you sent that got no reply), end-of-day email recap, opening an email from the list.

## Feature 2: calendar and Teams meetings (not started)
- Today plus the next 24 hours, with recurring meetings included. Declined and "Free" items are skipped.
- Teams meetings are detected by their `teams.microsoft.com/l/meetup-join` link.
- Reminders 10 minutes and 1 minute before a meeting, with **Join Teams** and **Remind in 5 min** buttons.
- Clash detector, back-to-back warning, tomorrow preview at 5 PM, unanswered invites.
- Source options (discussed 2026-11-01): classic Outlook COM (preferred, needs an account in classic Outlook),
  a published calendar link (ICS, quick but an external connection, needs approval), or Microsoft Graph (needs IT app registration).

## Feature 3: reminders and checklist
Built:
- Quick capture: left click → **Add reminder**, or `Ctrl + Alt + R` from anywhere (`Win + Shift + R` is taken by the Snipping Tool recorder on Windows 11).
- A local rule-based parser (no AI) in English and Malay. It splits several items ("A, B and C"; items without a time share the time
  of the last item that has one) and understands days (today, tomorrow/esok, lusa, next week, Monday/Isnin, 15/11), times (3pm, 9.30am,
  15:00, pukul 3 petang, after lunch/lepas lunch, end of day, malam ni, in 30 minutes), repeats (every day, every weekday,
  every Friday/setiap Jumaat, every month end) and `!` for high priority. Tested on 15 English and Malay sentences.
- A confirmation step in the bubble shows what was understood before saving (Edit / Save).
- Due reminders: the bot waves, hops and says "{Name}, time to {task}!" with **✓ Done / 10 min / 1 hour**. Ignored ones pop up again
  after 10 minutes. Ticking a repeating reminder moves it to its next date.
- "Reminder" = has a time (alerts); "checklist item" = no time (sits under Anytime, no alert).
- **Checklist window** (white fading to light purple, light title bar, SMBot head in the header): sections Overdue / Today / Tomorrow /
  Upcoming / Anytime with count pills, white rounded task cards, round purple tick boxes, delete, today's progress bar, and an add box
  (adds straight away, no confirmation step).

Planned: calendar-relative times ("after my 10am meeting"), smart delivery (wait until a meeting ends, "when I'm free"),
reminders getting more insistent (bubble → bounce → toast), sub-tasks, tags (`#project`), links to emails or files,
morning brief, end-of-day review, Friday summary, streaks, export to CSV/Excel.

## Feature 4: recent files (built)
- Left click → **Recent files** lists the last 5 files opened in Windows (from `%APPDATA%\Microsoft\Windows\Recent`, all local):
  name, how long ago, folder. Folders and deleted files are skipped.
- Clicking one opens File Explorer at its folder with the file selected.

## Feature 5: file search (built)
Faster and more forgiving than Windows Search for the user's own files.
- Opens with `Ctrl + Alt + F` or left click → **Search files**: a Spotlight-style popup (rounded, white to light purple).
  ↑ ↓ to pick, Enter opens, Ctrl + Enter opens the folder, Esc or clicking elsewhere closes.
- Searches file and folder **names** in Desktop, Downloads, Videos and both OneDrive folders (Documents and Pictures are inside OneDrive).
  About 98,000 entries, listed in about 2 seconds in the background at startup, kept current with folder watchers.
  Skips `bin`, `obj`, `node_modules`, `.git`, `.vs` and hidden/system files. All in memory, nothing leaves the PC.
- Matching: whole name > start of name > start of a word (camelCase aware) > anywhere > initials ("qr" → Quarterly Report)
  > folder path > one typo ("remnder" → Reminder; a missed letter ranks above an extra one) > letters in order ("bdywnd" → BuddyWindow).
  All words must match; if none do, the closest partial matches show instead.
- Ranking boosts files opened recently (Windows Recent items) and recently changed, and slightly prefers files over folders and shallow paths.
- Filters: `.pdf`, `type:excel|word|pdf|ppt|image|video|zip|text`, `in:downloads`. They combine.
- Searches take about 5–90 ms (tested on the real folders).
- Planned: searching inside text files (.txt, .log, .csv), choosing which folders to include.

## Other ideas (later)
- "In a call" detection (Teams using the microphone → stay quiet), system/website watcher with log-file error alerts,
  promise tracker (promises in sent emails → reminders), automatic timesheet, ticket hub (INC numbers across emails/files),
  quick replies and snippets, morning brief and end-of-day wrap-up.
- Break and water reminders, Pomodoro, Do Not Disturb when an app is full-screen, prayer times (calculated offline),
  Malaysian public holidays, clipboard history, low disk / battery alerts, a count badge on the bot.
- Optional local AI (Ollama / LM Studio on this PC; RTX 3070 is enough for a 7–8B model) for chat in the bubble,
  reply drafts and summaries, strictly opt-in. Microsoft 365 Copilot isn't available (no licence).

## Build phases
1. ✅ **Character and shell:** transparent window, 3D character frames and animations, `settings.json`, first-run onboarding, single instance, icon.
2. ✅ **Outlook reader:** COM check-and-close, unread and unreplied detection and filters, account check, stop after failure. Waiting on a classic Outlook account.
3. ⏳ **Calendar:** today's agenda, Teams link detection, scheduled meeting reminders.
4. 🔶 **Bubble and notifications:** speech bubble, action list, status peek, Done / snooze ✅; toasts and tray icon ⏳.
5. 🔶 **Reminders and checklist:** quick capture, parser, confirmation, checklist window, repeats ✅; smart delivery ⏳.
6. ⏳ **Polish:** settings window, start with Windows, quiet hours, end-of-day review.
- ✅ Extras built along the way: recent files, file search.

## Project layout
```
ClaudeBot/                          # project folder (still named ClaudeBot)
  PLAN.md
  design/
    smbot-sprites.html              # Three.js model of the bot; ?frame=<name> renders one pose, ?frame=icon the head
    render-sprites.ps1              # renders all 22 frames into src/SMBot/Assets/Bot (headless Edge)
    make-icon.ps1                   # renders the head and builds Assets/smbot.ico + smbot-head.png
    smbot-3d-concepts.html          # the three look concepts; concept-variants.png, concept-closeup.png
  src/SMBot/
    SMBot.csproj
    App.xaml(.cs)                   # startup, single instance (named mutex + signals), crash log
    Views/BuddyWindow.xaml(.cs)     # the bot: frames, bubble, action list, onboarding, emails, reminders, peek, hotkeys
    Views/ChecklistWindow.xaml(.cs) # checklist window
    Views/SearchWindow.xaml(.cs)    # file search popup
    Services/SettingsService.cs     # userName, emailCheckSeconds, dismissed emails
    Services/OutlookComMailService.cs
    Services/ReminderService.cs, ReminderParser.cs
    Services/RecentFilesService.cs
    Services/FileSearchService.cs
    Assets/Bot/*.png                # 22 pre-rendered 3D frames
    Assets/smbot.ico, smbot-head.png
```

## Risks
- COM only works with classic Outlook, so it must stay installed and signed in (Microsoft supports it until at least 2029).
  Outlook 2016 may not connect to Microsoft 365 at all.
- A possible Outlook security prompt when the app connects through COM. Usually fine with up-to-date antivirus, otherwise it needs IT policy.
- Classic Outlook's cache may hold only recent mail. The bot only needs the last 14–30 days.
- The parser can misread a time. The confirmation step catches this before saving.
- File search watches whole OneDrive folders; very large syncs can overflow the watcher, and changes made then show up after a restart.
- The hotkeys can't be claimed if another app already owns them; the action list still works.
