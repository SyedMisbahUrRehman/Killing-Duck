# Killing Duck

> Kills ~~You~~, Kills Brain Rot

A pixel-art duck companion for Chrome (Manifest V3). It watches how many tabs
you have open and how long you have been actively browsing, and steps in when
either gets out of hand. On Windows, a small companion program puts the duck
on Chrome's tab strip itself: it waddles across your tabs and pecks a tab's
close button to kill it. Everything stays on your computer; there is no
backend or account.

## Run it

1. Open `chrome://extensions` and turn on **Developer mode**.
2. Click **Load unpacked** and select this folder.
3. Pin the duck to the toolbar and open a few websites.
4. Click the duck icon to open the dashboard.

After changing code, click the reload icon on the extension card, then refresh
your test pages so they get the new content script.

## First run

On install, the duck opens `welcome.html`: it lists everything the extension
reads and keeps, and nothing is tracked until the user presses **I AGREE,
WAKE THE DUCK**. Until then the duck counts as switched off whatever the
switch says, and turning the switch on opens the welcome page instead. The
Chrome Web Store requires this consent step for any extension that handles
user data, even data that never leaves the device. Agreement is stored as
`consentedAt` in `chrome.storage.local`.

## Switching the duck off

The big switch at the top of the popup turns the whole extension on and off.
While it is off:

- the popup and the report page show only the switch (the report page shows
  a **TURN THE DUCK ON** button instead);
- nothing is tracked: no active minutes, no brain rot minutes;
- no overlay or focus block appears on any page, and the toolbar badge is
  empty;
- the duck leaves the tab strip (the helper quits), so it never hunts;
- a running focus session ends without XP;
- your streak is frozen. The day you switched off still counts, but days the
  duck was off the whole time neither extend nor break the streak.

Switching it back on brings everything back where it was.

## Put the duck on the tab strip (Windows)

Chrome does not let extensions draw on the tab strip, so `helper/` holds a
small Windows program that does it. Chrome starts and stops it through
[native messaging](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging).

1. Double-click `helper\install.cmd`. It compiles `KillingDuck.exe` from
   `KillingDuck.cs` with the C# compiler that ships with Windows, and registers
   it with Chrome for your Windows user (one registry key under
   `HKCU\Software\Google\Chrome\NativeMessagingHosts`).
2. Reload Killing Duck on `chrome://extensions`. The duck appears on the tab
   strip of your focused Chrome window.

`helper\uninstall.cmd` removes the registration. Without the helper the
extension works as before, and tabs close straight away.

### Playing with the duck

| Do this | And the duck |
| ------- | ------------ |
| Press **KILL** next to a tab in the popup, or **CLOSE THIS TAB** on the page overlay | runs over and pecks that tab's close button; the tab shatters and closes |
| Drag the duck and drop it on a tab | kills that tab |
| Fling it sideways | slides, bounces off the ends, and gets dizzy if you threw it hard |
| Click it | quacks. Clicking it while it pecks spares the tab |
| Press **SIC 'EM** in the popup | kills your stalest tab (never the active, pinned or noisy one) |
| Turn on **Hunt mode** in the popup | goes after a tab on its own when you are over the tab limit (below) |
| Right-click it | menu: mute, nap for 30 minutes, send it away until Chrome restarts |

Every kill earns XP toward the duck's level (see below).

### Hunt mode

Off by default. When it is on and you have `warningTabs` (6) or more tabs, the
duck picks one tab in your focused window, at most one every 45 seconds:

- never the active tab, a pinned tab, or a tab playing sound;
- sites on your brain rot list first (by default YouTube Shorts, TikTok,
  Instagram, Reddit, X, Facebook, 9GAG and Twitch) once you have not looked
  at them for 30 seconds;
- otherwise the tab you have ignored longest, once unseen for 5 minutes.

It shows a "!" and gives two warning pecks first, so you can click it to spare
the tab. A spared tab is safe for 30 minutes. Chrome's
**Ctrl+Shift+T** brings back a tab you wanted after all.

### How the helper works

- It finds each tab and its close button with Windows UI Automation, the
  accessibility interface Chrome exposes, and refreshes their positions five
  times a second.
- It draws into a layered window placed exactly over the tab strip and kept
  directly above the Chrome window in the z-order. Fully transparent pixels let
  the mouse through, so only the duck itself can be clicked.
- It only lives in windows of the Chrome profile running the extension: the
  extension sends the titles of its focused window's tabs, and the helper
  matches them against what UI Automation sees. Incognito windows and other
  profiles are left alone.
- The helper never closes tabs. When the final peck lands it tells the
  extension, and the extension calls `chrome.tabs.remove`. If the duck cannot
  reach the tab (window minimised, duck napping), a close you asked for
  happens straight away and a hunt is skipped.
- It logs start-up and errors to `%LOCALAPPDATA%\KillingDuck\helper.log`.

The extension's ID is pinned by the `key` field in `manifest.json`
(`foifgpacdfdginklojklodfnoljjhgkj`), because the helper registration only
accepts the IDs listed in `helper/install.ps1`.

## Publishing to the Chrome Web Store

The store refuses a manifest with a `key` field, so don't zip the folder by
hand. Double-click `pack.cmd`: it writes `dist\killing-duck-<version>.zip`
with `key` removed from the manifest and only the extension's own files
inside (no helper, README or scripts). Upload that zip.

The store copy's ID is `bcebhbmffhngbmdigmkfgeiecpmbaale`, listed in
`$extensionIds` in `helper/install.ps1` next to the unpacked copy's ID, so
the helper accepts both.

The store also needs a privacy policy URL. The policy text is in
[`PRIVACY.md`](PRIVACY.md); keep it in step with `welcome.html` when what the
extension reads or keeps changes.

## Levels, XP and streaks

| You | XP |
| --- | -- |
| Kill a tab (any way: popup, overlay, drag, hunt) | 10 |
| Kill a tab on your brain rot list | 20 |
| Finish a focus session | 1 per minute (25 for the default session) |
| Finish a day you browsed on within your brain rot budget | 25 |

Level *n* starts at 50 × *n* × (*n* − 1) XP: level 2 at 100, level 3 at 300,
level 4 at 600, level 5 at 1,000. The duck's rank goes DUCKLING, BANDIT,
HITMAN, ASSASSIN, WARLORD, LEGEND. On the tab strip it wears a red headband
from level 2 and sunglasses from level 3, and says LEVEL UP! when it gains one.

Minutes on brain rot sites are counted the same way as active time (below).
A day with no more brain rot than your daily budget (15 minutes by default)
adds one to your streak; a day over it resets the streak to zero. Days
Chrome was not open count as clean but earn no XP. The streak is settled
once a day has ended, so today never breaks it until midnight.

Updating from 1.1 keeps your kills: each one becomes 10 XP, so the headband
and sunglasses stay where they were.

## Focus mode

Press **START A 25-MINUTE FOCUS** in the popup. Until the timer runs out:

- every page on your brain rot list is covered by a full-page block with the
  time left, and its videos are paused;
- with the duck on the tab strip, it runs over and pecks a brain rot tab as
  soon as you look at it (click the duck to spare the tab, as with any peck);
- the toolbar badge shows the minutes left instead of the tab count.

Let the timer run out to earn the XP. **END EARLY** in the popup, or on the
block, stops the session without XP. A session still pays out if Chrome was
closed when it ended.

## Report and settings

**WEEKLY REPORT & SETTINGS** at the bottom of the popup (or **Extension
options** on `chrome://extensions`) opens a page with:

- your level, streak, brain rot over the last 7 days compared with the 7
  before, and tabs killed;
- a chart of active browsing for each of the last 7 days, split into brain
  rot and everything else, with a day-by-day table underneath;
- every setting: the tab and time thresholds below, the daily brain rot
  budget, the focus length, and the brain rot site list (one per line; add a
  path such as `youtube.com/shorts` to count only part of a site).

History is kept for 60 days, on this computer only.

## Default thresholds

| Condition                     | Duck mood | Overlay message          |
| ----------------------------- | --------- | ------------------------ |
| 5 or fewer tabs               | happy     | hidden                   |
| 6-10 tabs                     | worried   | "Maybe close a few?"     |
| 11-15 tabs                    | stressed  | "Are you really using all of them?" |
| 16+ tabs                      | exhausted | "Help me clean this mess!" |
| 25 minutes of active browsing | worried   | time warning             |
| 45 minutes of active browsing | exhausted | strong break reminder    |

Change them on the report page. The defaults live in `DEFAULT_SETTINGS` at
the top of `background.js`; the overlay, popup and tab strip duck all read the
current values from the background worker.

## How time is counted

An alarm fires once a minute. It adds one minute to today's total only when
all of these are true:

- the user has used the keyboard or mouse in the last 2 minutes (`chrome.idle`),
- a normal Chrome window is focused and not minimized,
- the active tab is an `http` or `https` page.

The total resets at local midnight. It is an approximate measure, not a
stopwatch. To test the time warnings quickly, set the time thresholds to 1
and 2 minutes on the report page.

## Overlay behaviour

- The overlay appears on regular web pages when the mood is not happy.
- **CLOSE THIS TAB** closes the page it is on (through the duck, when it is on
  the tab strip). Outside hunt mode and focus mode the duck never closes tabs
  on its own.
- **I'LL BE BACK** hides the duck on that page until the mood gets worse, or
  until it recovers to happy and then worsens again.

## Permissions

| Permission        | Why |
| ----------------- | --- |
| `tabs`            | Count open tabs, list their titles in the popup, close tabs on request |
| `storage`         | Save daily history, XP, streaks, focus sessions and settings locally |
| `alarms`          | Wake the service worker once a minute to count time |
| `idle`            | Skip minutes when the user is away from the computer |
| `nativeMessaging` | Talk to the optional tab strip helper on Windows |

The content script runs on all `http` and `https` pages to show the overlay.
`welcome.html` and `PRIVACY.md` both say so.
