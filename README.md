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

Every tab the duck kills counts toward its level: a red headband at 10 kills,
sunglasses at 30.

### Hunt mode

Off by default. When it is on and you have `warningTabs` (6) or more tabs, the
duck picks one tab in your focused window, at most one every 45 seconds:

- never the active tab, a pinned tab, or a tab playing sound;
- brain rot sites first (YouTube Shorts, TikTok, Instagram, Reddit, X,
  Facebook, 9GAG, Twitch) once you have not looked at them for 30 seconds;
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
- `KillingDuck.exe --demo` runs it without Chrome's connection (pecks are
  pretend). `--probe out.txt` dumps what it sees of each Chrome window's tabs.
  It logs to `%LOCALAPPDATA%\KillingDuck\helper.log`.

The extension's ID is pinned by the `key` field in `manifest.json`
(`foifgpacdfdginklojklodfnoljjhgkj`), because the helper registration only
accepts that ID. Before publishing to the Chrome Web Store, remove `key` and
add the store's extension ID to `allowed_origins` in `helper/install.ps1`.

## Default thresholds

| Condition                     | Duck mood | Overlay message          |
| ----------------------------- | --------- | ------------------------ |
| 5 or fewer tabs               | happy     | hidden                   |
| 6-10 tabs                     | worried   | "Maybe close a few?"     |
| 11-15 tabs                    | stressed  | "Are you really using all of them?" |
| 16+ tabs                      | exhausted | "Help me clean this mess!" |
| 25 minutes of active browsing | worried   | time warning             |
| 45 minutes of active browsing | exhausted | strong break reminder    |

Thresholds live in `SETTINGS` at the top of `background.js`. The overlay and
the tab strip duck read them from the background worker, so that is the only
place to change them.

## How time is counted

An alarm fires once a minute. It adds one minute to today's total only when
all of these are true:

- the user has used the keyboard or mouse in the last 2 minutes (`chrome.idle`),
- a normal Chrome window is focused and not minimized,
- the active tab is an `http` or `https` page.

The total resets at local midnight. It is an approximate measure, not a
stopwatch. To test the time warnings quickly, temporarily set
`warningMinutes` and `criticalMinutes` to 1 and 2.

## Overlay behaviour

- The overlay appears on regular web pages when the mood is not happy.
- **CLOSE THIS TAB** closes the page it is on (through the duck, when it is on
  the tab strip). Outside hunt mode the duck never closes tabs on its own.
- **I'LL BE BACK** hides the duck on that page until the mood gets worse, or
  until it recovers to happy and then worsens again.

## Permissions

| Permission        | Why |
| ----------------- | --- |
| `tabs`            | Count open tabs, list their titles in the popup, close tabs on request |
| `storage`         | Save today's active minutes, kill counts and settings locally |
| `alarms`          | Wake the service worker once a minute to count time |
| `idle`            | Skip minutes when the user is away from the computer |
| `nativeMessaging` | Talk to the optional tab strip helper on Windows |

The content script runs on all `http` and `https` pages to show the overlay.
Mention this in the Chrome Web Store privacy disclosure.
