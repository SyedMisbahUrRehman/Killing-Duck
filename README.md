# Killing Duck

> Kills ~~You~~, Kills Brain Rot

A pixel-art duck companion for Chrome (Manifest V3). It watches how many tabs
you have open and how long you have been actively browsing, and steps in with
a small overlay when either gets out of hand. Everything is stored locally in
`chrome.storage.local`; there is no backend or account.

## Default thresholds

| Condition                     | Duck mood | Overlay message          |
| ----------------------------- | --------- | ------------------------ |
| 5 or fewer tabs               | happy     | hidden                   |
| 6-10 tabs                     | worried   | "Maybe close a few?"     |
| 11-15 tabs                    | stressed  | "Are you really using all of them?" |
| 16+ tabs                      | exhausted | "Help me clean this mess!" |
| 25 minutes of active browsing | worried   | time warning             |
| 45 minutes of active browsing | exhausted | strong break reminder    |

Thresholds live in `SETTINGS` at the top of `background.js`. The overlay reads
them from the background worker, so that is the only place to change them.

## Run it

1. Open `chrome://extensions` and turn on **Developer mode**.
2. Click **Load unpacked** and select this `tab-closing-animal` folder.
3. Pin the duck to the toolbar and open a few websites.
4. Click the duck icon to open the dashboard.

After changing code, click the reload icon on the extension card, then refresh
your test pages so they get the new content script. Pages that were already
open when the extension was installed also need a refresh before the overlay
appears on them.

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
- **CLOSE THIS TAB** closes the page it is on. The duck never closes tabs on its own.
- **I'LL BE BACK** hides the duck on that page until the mood gets worse, or
  until it recovers to happy and then worsens again.

## Permissions

| Permission | Why |
| ---------- | --- |
| `tabs`     | Count open tabs, list their titles in the popup, close tabs on request |
| `storage`  | Save today's active minutes locally |
| `alarms`   | Wake the service worker once a minute to count time |
| `idle`     | Skip minutes when the user is away from the computer |

The content script runs on all `http` and `https` pages to show the overlay.
Mention this in the Chrome Web Store privacy disclosure.
