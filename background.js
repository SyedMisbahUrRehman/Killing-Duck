// Killing Duck - background service worker.
//
// Manifest V3 service workers are stopped when idle and restarted on the next
// event, so nothing important lives in global variables. Browsing time is
// persisted in chrome.storage.local and periodic work runs on chrome.alarms.
//
// When the Windows helper (helper/KillingDuck.exe) is installed, this worker
// keeps a native messaging port open to it. The helper draws the duck on the
// tab strip; an open port also keeps this worker alive, so the job queue
// below can safely live in memory.

// Defaults for everything the user can change on the report page. The
// stored copy lives under "settings" and is always passed through
// cleanSettings() before use.
const DEFAULT_SETTINGS = {
  warningTabs: 6,
  stressedTabs: 11,
  criticalTabs: 16,
  warningMinutes: 25,
  criticalMinutes: 45,
  // A day keeps the streak alive if it has at most this much brain rot.
  rotBudgetMinutes: 15,
  focusMinutes: 25,
  brainRot: [
    "youtube.com/shorts",
    "tiktok.com",
    "instagram.com",
    "reddit.com",
    "x.com",
    "twitter.com",
    "facebook.com",
    "9gag.com",
    "twitch.tv"
  ]
};

const SETTING_LIMITS = {
  warningTabs: [2, 98],
  stressedTabs: [3, 99],
  criticalTabs: [4, 100],
  warningMinutes: [1, 599],
  criticalMinutes: [2, 600],
  rotBudgetMinutes: [0, 600],
  focusMinutes: [5, 180]
};

const ALARM_NAME = "duck-browsing-monitor";
const FOCUS_ALARM = "duck-focus-end";

// Experience. Level n starts at 50 * n * (n - 1) XP, so 10 plain kills
// reach level 2 (headband) and 30 reach level 3 (sunglasses).
const KILL_XP = 10;
const BRAIN_ROT_KILL_XP = 20;
const CLEAN_DAY_XP = 25;          // per finished day under the brain rot budget
const FOCUS_XP_PER_MINUTE = 1;
const RANKS = ["DUCKLING", "BANDIT", "HITMAN", "ASSASSIN", "WARLORD", "LEGEND"];

const HISTORY_DAYS = 60;

// A minute only counts as active browsing if the user touched the keyboard
// or mouse within this many seconds (chrome.idle minimum is 15).
const IDLE_THRESHOLD_SECONDS = 120;

// Collapse bursts of tab events (opening a session, closing a window) into
// one broadcast instead of messaging every tab once per event.
const BROADCAST_DELAY_MS = 250;

const HELPER_NAME = "com.killingduck.tabstrip";

// Hunt mode: when you are over the tab limit, the duck goes after one tab
// at a time. It never touches the active tab, pinned tabs or tabs playing
// sound, and clicking the duck while it pecks spares the tab.
const HUNT_COOLDOWN_MS = 45 * 1000;
const HUNT_IDLE_MS = 5 * 60 * 1000;          // ordinary tabs: unseen for 5 minutes
const HUNT_BRAIN_ROT_IDLE_MS = 30 * 1000;    // brain rot tabs: unseen for 30 seconds
const SPARED_FOR_MS = 30 * 60 * 1000;

const DEFAULT_PREFS = {
  // The master switch. Off: no tracking, no overlays, no duck on the tab
  // strip, and the streak is frozen until it is back on.
  enabled: true,
  huntMode: false,
  sound: true
};

function isWebUrl(url) {
  return typeof url === "string" &&
    (url.startsWith("http://") || url.startsWith("https://"));
}

// list: entries such as "tiktok.com" or "youtube.com/shorts"
function isBrainRot(url, list) {
  if (!isWebUrl(url)) return false;

  try {
    const { hostname, pathname } = new URL(url);
    const host = hostname.replace(/^www\.|^m\./, "");
    return list.some(entry => {
      const [entryHost, entryPath = ""] = entry.split(/\/(.*)/);
      return (host === entryHost || host.endsWith(`.${entryHost}`)) &&
        pathname.startsWith(`/${entryPath}`);
    });
  } catch {
    return false;
  }
}

// "https://www.TikTok.com/" -> "tiktok.com"; null if it is not a site
function normalizeSite(entry) {
  const text = String(entry).trim().toLowerCase()
    .replace(/^[a-z][a-z0-9+.-]*:\/\//, "")
    .replace(/^www\.|^m\./, "")
    .replace(/[?#].*$/, "")
    .replace(/\/+$/, "");

  return /^[a-z0-9-]+(\.[a-z0-9-]+)+(\/\S*)?$/.test(text) ? text : null;
}

function cleanSettings(raw) {
  const settings = { ...DEFAULT_SETTINGS };

  for (const [key, [min, max]] of Object.entries(SETTING_LIMITS)) {
    const value = Math.round(Number(raw?.[key]));
    if (raw?.[key] !== "" && raw?.[key] !== null && Number.isFinite(value)) {
      settings[key] = Math.min(max, Math.max(min, value));
    }
  }

  // Each mood needs more than the one before it.
  settings.stressedTabs = Math.max(settings.stressedTabs, settings.warningTabs + 1);
  settings.criticalTabs = Math.max(settings.criticalTabs, settings.stressedTabs + 1);
  settings.criticalMinutes = Math.max(settings.criticalMinutes, settings.warningMinutes + 1);

  if (Array.isArray(raw?.brainRot)) {
    settings.brainRot = [...new Set(raw.brainRot.map(normalizeSite).filter(Boolean))].slice(0, 200);
  }

  return settings;
}

async function getSettings() {
  const { settings } = await chrome.storage.local.get("settings");
  return cleanSettings(settings);
}

function toDayKey(date) {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");

  return `${year}-${month}-${day}`;
}

// Today's date in local time, used to reset statistics daily
function getTodayKey() {
  return toDayKey(new Date());
}

function addDays(dayKey, days) {
  const [year, month, day] = dayKey.split("-").map(Number);
  return toDayKey(new Date(year, month - 1, day + days));
}

async function getPrefs() {
  const { consentedAt, ...data } =
    await chrome.storage.local.get([...Object.keys(DEFAULT_PREFS), "consentedAt"]);
  const prefs = { ...DEFAULT_PREFS, ...data, consented: Boolean(consentedAt) };

  // Until the user agrees on welcome.html to what the duck reads and keeps,
  // it is off whatever the switch says: nothing is tracked or shown.
  if (!prefs.consented) prefs.enabled = false;
  return prefs;
}

// Kills, minute ticks and focus sessions can land at the same moment, and
// each one reads, changes and writes storage. Running them one after
// another keeps any of them from overwriting another's update.
let storageChain = Promise.resolve();

function mutate(change) {
  const run = storageChain.then(change);
  storageChain = run.catch(error => console.warn("Duck storage update failed:", error));
  return run;
}

// ---------------------------------------------------------------------------
// Daily history, XP and streaks
// ---------------------------------------------------------------------------

function emptyDay() {
  return {
    minutes: 0,         // active browsing
    rotMinutes: 0,      // of which on brain rot sites
    kills: 0,
    rotKills: 0,
    focusMinutes: 0,
    focusSessions: 0
  };
}

function getLevel(xp) {
  let level = 1;
  while (xp >= 50 * (level + 1) * level) level++;
  return level;
}

function getRank(level) {
  return RANKS[Math.min(level, RANKS.length) - 1];
}

function levelStartXp(level) {
  return 50 * level * (level - 1);
}

// Runs inside mutate(). Fills in what older versions stored (minutes, kill
// counts) the first time this version starts.
async function loadProgress() {
  const data = await chrome.storage.local.get([
    "history", "xp", "streak", "killsTotal",
    "activeMinutes", "date", "killsToday", "killsDate"
  ]);

  const today = getTodayKey();
  const progress = {
    history: data.history || {},
    xp: data.xp,
    streak: data.streak,
    killsTotal: data.killsTotal || 0
  };

  let migrated = false;

  if (progress.xp === undefined) {
    progress.xp = progress.killsTotal * KILL_XP;

    const day = { ...emptyDay(), ...progress.history[today] };
    if (data.date === today) day.minutes = Math.max(day.minutes, data.activeMinutes || 0);
    if (data.killsDate === today) day.kills = Math.max(day.kills, data.killsToday || 0);
    progress.history[today] = day;
    migrated = true;
  }

  // The streak starts counting from the first day this version runs.
  if (!progress.streak) {
    progress.streak = { count: 0, best: 0, through: addDays(today, -1) };
    migrated = true;
  }

  if (migrated) {
    await chrome.storage.local.set(progress);
    await chrome.storage.local.remove(["activeMinutes", "date", "killsToday", "killsDate"]);
  }

  return progress;
}

async function saveProgress(progress) {
  const oldest = addDays(getTodayKey(), -HISTORY_DAYS);

  for (const day of Object.keys(progress.history)) {
    if (day < oldest) delete progress.history[day];
  }

  await chrome.storage.local.set(progress);
}

// Change today's entry (and the rest of the progress) in one step.
function updateToday(change) {
  return mutate(async () => {
    const progress = await loadProgress();
    const today = getTodayKey();
    const day = { ...emptyDay(), ...progress.history[today] };

    const result = change(day, progress);
    progress.history[today] = day;

    await saveProgress(progress);
    return result;
  });
}

// Close the books on every finished day before `until` (today by default)
// since the last check. A day with at most the brain rot budget extends the
// streak; days Chrome was not open at all count too, but only days you
// browsed earn XP.
function settleStreak(settings, until = getTodayKey()) {
  return mutate(async () => {
    const progress = await loadProgress();
    const today = getTodayKey();
    const { streak } = progress;

    let day = addDays(streak.through, 1);
    if (day < addDays(today, -400)) day = addDays(today, -400);

    let changed = false;
    for (; day < until && day < today; day = addDays(day, 1)) {
      const entry = progress.history[day];

      if (!entry || entry.rotMinutes <= settings.rotBudgetMinutes) {
        streak.count++;
        if (entry && entry.minutes > 0) progress.xp += CLEAN_DAY_XP;
      } else {
        streak.count = 0;
      }

      streak.best = Math.max(streak.best, streak.count);
      streak.through = day;
      changed = true;
    }

    if (changed) await saveProgress(progress);
  });
}

async function getProgress() {
  const progress = await mutate(loadProgress);
  const today = { ...emptyDay(), ...progress.history[getTodayKey()] };
  const level = getLevel(progress.xp);

  return {
    today,
    xp: progress.xp,
    level,
    rank: getRank(level),
    levelXp: levelStartXp(level),
    nextLevelXp: levelStartXp(level + 1),
    streak: progress.streak.count,
    bestStreak: progress.streak.best,
    killsTotal: progress.killsTotal
  };
}

function recordKill(wasBrainRot) {
  return updateToday((day, progress) => {
    day.kills++;
    if (wasBrainRot) day.rotKills++;
    progress.killsTotal++;
    progress.xp += wasBrainRot ? BRAIN_ROT_KILL_XP : KILL_XP;
  });
}

// The last 14 days, oldest first, for the report page
async function getReport() {
  const [progress, settings] = await Promise.all([mutate(loadProgress), getSettings()]);
  const today = getTodayKey();
  const days = [];

  for (let i = 13; i >= 0; i--) {
    const key = addDays(today, -i);
    days.push({ date: key, ...emptyDay(), ...progress.history[key] });
  }

  return {
    enabled: (await getPrefs()).enabled,
    days,
    today,
    settings,
    xp: progress.xp,
    level: getLevel(progress.xp),
    rank: getRank(getLevel(progress.xp)),
    streak: progress.streak.count,
    bestStreak: progress.streak.best,
    killsTotal: progress.killsTotal
  };
}

// ---------------------------------------------------------------------------
// Focus mode
// ---------------------------------------------------------------------------

async function getFocus() {
  const { focus } = await chrome.storage.local.get("focus");
  return focus && Date.now() < focus.until ? focus : null;
}

async function startFocus() {
  const settings = await getSettings();
  const now = Date.now();
  const focus = {
    startedAt: now,
    until: now + settings.focusMinutes * 60 * 1000,
    minutes: settings.focusMinutes
  };

  await chrome.storage.local.set({ focus });
  await chrome.alarms.create(FOCUS_ALARM, { when: focus.until });
  helperSay("FOCUS TIME!");
  scheduleBroadcast();
  return focus;
}

// completed: the timer ran out (XP), not ended early (no XP)
// quiet: no speech bubble (the duck is leaving anyway)
async function finishFocus(completed, quiet = false) {
  // Claim the session in one step, so the end-of-focus alarm and the
  // minute tick cannot both pay out for it.
  const focus = await mutate(async () => {
    const { focus: stored } = await chrome.storage.local.get("focus");
    if (stored) await chrome.storage.local.remove("focus");
    return stored;
  });
  if (!focus) return;

  await chrome.alarms.clear(FOCUS_ALARM);

  if (completed) {
    const xp = focus.minutes * FOCUS_XP_PER_MINUTE;
    await updateToday((day, progress) => {
      day.focusMinutes += focus.minutes;
      day.focusSessions++;
      progress.xp += xp;
    });
    helperSay(`FOCUS DONE! +${xp} XP`);
  } else if (!quiet) {
    helperSay("GAVE UP?");
  }

  scheduleBroadcast();
}

// ---------------------------------------------------------------------------
// The master switch
// ---------------------------------------------------------------------------

async function isEnabled() {
  return (await getPrefs()).enabled;
}

async function setEnabled(on) {
  // Switching on before agreeing to the disclosure shows it instead.
  if (on && !(await getPrefs()).consented) {
    await openWelcome();
    return { success: false, needsConsent: true };
  }

  const settings = await getSettings();
  const today = getTodayKey();

  if (!on) {
    // Stop counting first, so a minute tick that is already running does
    // not add to today after the switch.
    await chrome.storage.local.set({ enabled: false, disabledSince: today });
    await finishFocus(false, true);
    await settleStreak(settings);
    disconnectHelper();
  } else {
    const { disabledSince } = await chrome.storage.local.get("disabledSince");
    await chrome.storage.local.set({ enabled: true });

    // Days the duck was fully off neither extend nor break the streak. The
    // day it was switched off had it on for part of the day, so that one
    // still counts.
    if (disabledSince) {
      const dayAfter = addDays(disabledSince, 1);
      await settleStreak(settings, dayAfter < today ? dayAfter : today);
      await mutate(async () => {
        const progress = await loadProgress();
        const yesterday = addDays(today, -1);
        if (progress.streak.through < yesterday) {
          progress.streak.through = yesterday;
          await saveProgress(progress);
        }
      });
      await chrome.storage.local.remove("disabledSince");
    }

    helper.status = "connecting";
    helper.retries = 0;
    helper.napUntil = 0;
    connectHelper();
  }

  await broadcastState();
  return { success: true, enabled: on };
}

async function giveConsent() {
  await chrome.storage.local.set({ consentedAt: Date.now() });
  return setEnabled(true);
}

// Opens the first-run disclosure, or brings an open one to the front.
async function openWelcome() {
  const url = chrome.runtime.getURL("welcome.html");
  const open = (await chrome.tabs.query({})).find(tab => tab.url === url);

  if (open) {
    await chrome.tabs.update(open.id, { active: true });
    await chrome.windows.update(open.windowId, { focused: true });
  } else {
    await chrome.tabs.create({ url });
  }
}

// A focus session whose end passed while Chrome or the worker was asleep
async function finishExpiredFocus() {
  const { focus } = await chrome.storage.local.get("focus");
  if (focus && Date.now() >= focus.until) await finishFocus(true);
}

// During focus, the duck goes after a brain rot tab as soon as you look at
// it. Clicking the duck still spares the tab, as with any peck.
async function enforceFocus(settings, focus) {
  if (!focus || helper.status !== "connected" || Date.now() < helper.napUntil) return;
  if (helper.current?.reason === "focus" || helper.queue.some(job => job.reason === "focus")) return;

  let win;
  try {
    win = await chrome.windows.getLastFocused({ populate: true, windowTypes: ["normal"] });
  } catch {
    return;
  }

  if (!win.focused || win.state === "minimized") return;

  const tab = (win.tabs || []).find(t => t.active);
  if (!tab || tab.id === undefined || !isBrainRot(tab.url, settings.brainRot)) return;

  const sparedAt = helper.spared.get(tab.id);
  if (sparedAt && Date.now() - sparedAt < SPARED_FOR_MS) return;

  await requestClose(tab.id, "focus");
}

// Count tabs in normal browser windows
async function getTabCount() {
  const tabs = await chrome.tabs.query({
    windowType: "normal"
  });

  return tabs.length;
}

// Determine the duck's mood
function getDuckMood(tabCount, minutes, settings) {
  if (tabCount >= settings.criticalTabs ||
      minutes >= settings.criticalMinutes) {
    return "exhausted";
  }

  if (tabCount >= settings.stressedTabs) {
    return "stressed";
  }

  if (tabCount >= settings.warningTabs ||
      minutes >= settings.warningMinutes) {
    return "worried";
  }

  return "happy";
}

// Current dashboard state, shared by the popup and the webpage overlay
async function getDashboard() {
  const [tabCount, prefs, settings, progress, focus] = await Promise.all([
    getTabCount(),
    getPrefs(),
    getSettings(),
    getProgress(),
    getFocus()
  ]);

  const { today } = progress;

  return {
    enabled: prefs.enabled,
    tabCount,
    activeMinutes: today.minutes,
    rotMinutes: today.rotMinutes,
    killsToday: today.kills,
    focusToday: today.focusSessions,
    mood: getDuckMood(tabCount, today.minutes, settings),
    settings,
    prefs,
    xp: progress.xp,
    level: progress.level,
    rank: progress.rank,
    levelXp: progress.levelXp,
    nextLevelXp: progress.nextLevelXp,
    streak: progress.streak,
    bestStreak: progress.bestStreak,
    killsTotal: progress.killsTotal,
    focus,
    helper: helper.status,
    napping: Date.now() < helper.napUntil
  };
}

let lastBroadcastEnabled;

// Send the current state to every open webpage and refresh the badge
async function broadcastState() {
  const state = await getDashboard();
  const { settings, focus } = state;

  const tabs = await chrome.tabs.query({});

  // Switched off: take every overlay down once and clear the badge. Pages
  // opened later ask for themselves and learn the duck is off.
  if (!state.enabled) {
    if (lastBroadcastEnabled !== false) {
      await Promise.all(
        tabs
          .filter(tab => tab.id !== undefined && isWebUrl(tab.url))
          .map(tab => chrome.tabs.sendMessage(tab.id, { type: "DUCK_UPDATE", state }).catch(() => {}))
      );
      await chrome.action.setBadgeText({ text: "" });
    }
    lastBroadcastEnabled = false;
    return;
  }
  lastBroadcastEnabled = true;

  await Promise.all(
    tabs
      .filter(tab => tab.id !== undefined && isWebUrl(tab.url))
      .map(tab =>
        chrome.tabs.sendMessage(tab.id, {
          type: "DUCK_UPDATE",
          state: { ...state, blocked: Boolean(focus) && isBrainRot(tab.url, settings.brainRot) }
        }).catch(() => {
          // Page has no content script yet (still loading, or opened
          // before the extension was installed).
        })
      )
  );

  // The badge shows the minutes left in a focus session, otherwise the
  // tab count.
  if (focus) {
    await chrome.action.setBadgeText({
      text: `${Math.max(1, Math.ceil((focus.until - Date.now()) / 60000))}m`
    });
    await chrome.action.setBadgeBackgroundColor({ color: "#15803d" });
  } else {
    await chrome.action.setBadgeText({ text: String(state.tabCount) });
    await chrome.action.setBadgeBackgroundColor({
      color: state.tabCount >= settings.criticalTabs ? "#dc2626" : "#64748b"
    });
  }

  await sendStateToHelper(state);
  await enforceFocus(settings, focus);
  await maybeHunt(false);
}

let broadcastTimer = null;

function scheduleBroadcast() {
  clearTimeout(broadcastTimer);

  broadcastTimer = setTimeout(() => {
    broadcastTimer = null;
    broadcastState().catch(error => {
      console.warn("Duck broadcast failed:", error);
    });
  }, BROADCAST_DELAY_MS);
}

// The active tab, if the user is at the keyboard, Chrome is focused and
// that tab is a regular website; otherwise null
async function getBrowsedTab() {
  const idleState = await chrome.idle.queryState(IDLE_THRESHOLD_SECONDS);

  if (idleState !== "active") {
    return null;
  }

  let focusedWindow;

  try {
    focusedWindow = await chrome.windows.getLastFocused({
      populate: true,
      windowTypes: ["normal"]
    });
  } catch {
    // No browser window is open
    return null;
  }

  if (!focusedWindow.focused || focusedWindow.state === "minimized") {
    return null;
  }

  const activeTab = focusedWindow.tabs?.find(tab => tab.active);

  return isWebUrl(activeTab?.url) ? activeTab : null;
}

// Accumulate active browsing time, one minute per alarm tick
async function trackBrowsingTime() {
  if (!(await isEnabled())) return;

  const settings = await getSettings();

  await finishExpiredFocus();
  await settleStreak(settings);

  const tab = await getBrowsedTab();
  if (tab) {
    const rot = isBrainRot(tab.url, settings.brainRot);
    await updateToday(day => {
      day.minutes++;
      if (rot) day.rotMinutes++;
    });
  }

  await broadcastState();
}

// Create the recurring alarm if it does not exist yet
async function initializeAlarm() {
  const existingAlarm = await chrome.alarms.get(ALARM_NAME);

  if (!existingAlarm) {
    await chrome.alarms.create(ALARM_NAME, {
      periodInMinutes: 1
    });
  }
}

// ---------------------------------------------------------------------------
// Closing tabs
// ---------------------------------------------------------------------------

async function closeTabNow(tabId) {
  try {
    const [tab, settings] = await Promise.all([
      chrome.tabs.get(tabId).catch(() => null),
      getSettings()
    ]);
    await chrome.tabs.remove(tabId);
    await recordKill(isBrainRot(tab?.url, settings.brainRot));
    return { success: true };
  } catch (error) {
    return { success: false, error: error.message };
  }
}

// Hunts and focus kills are the duck's own idea: without the duck on the
// tab strip they do not happen at all.
function isAutomatic(reason) {
  return reason === "hunt" || reason === "focus";
}

// Every close goes through here. With the helper running, the duck walks
// over and pecks the tab's close button first; otherwise a close the user
// asked for happens straight away.
async function requestClose(tabId, reason) {
  if (helper.status !== "connected" || Date.now() < helper.napUntil) {
    if (isAutomatic(reason)) return { success: false, error: "The duck is not on the tab strip" };
    return closeTabNow(tabId);
  }

  const alreadyQueued = helper.current?.tabId === tabId ||
    helper.queue.some(job => job.tabId === tabId);

  if (!alreadyQueued) {
    helper.queue.push({ tabId, reason });
    pumpQueue();
  }

  return { success: true, duck: true };
}

// Jobs go to the helper one at a time, so the tab index and window titles
// it receives are always current.
async function pumpQueue() {
  if (helper.pumping || helper.current || !helper.port || helper.status !== "connected") return;

  const next = helper.queue.shift();
  if (!next) return;

  helper.pumping = true;
  let tab;
  let windowTabs;
  try {
    tab = await chrome.tabs.get(next.tabId).catch(() => null);
    windowTabs = tab
      ? (await chrome.tabs.query({ windowId: tab.windowId })).sort((a, b) => a.index - b.index)
      : [];
  } finally {
    helper.pumping = false;
  }

  if (!tab || !helper.port) {
    pumpQueue();
    return;
  }

  const job = {
    ...next,
    jobId: `job-${Date.now()}-${++helper.jobCounter}`,
    timer: setTimeout(() => settleJob(job.jobId, "timeout"), next.reason === "hunt" ? 20000 : 12000)
  };
  helper.current = job;

  helper.port.postMessage({
    type: "kill",
    jobId: job.jobId,
    index: tab.index,
    title: tab.title || "",
    titles: windowTabs.map(t => t.title || ""),
    reason: job.reason
  });
}

// outcome: "pecked", "spared", or a reason the duck could not do it
async function settleJob(jobId, outcome) {
  const job = helper.current;
  if (!job || job.jobId !== jobId) return;

  clearTimeout(job.timer);
  helper.current = null;

  if (outcome === "pecked") {
    const result = await closeTabNow(job.tabId);
    if (!result.success) helper.port?.postMessage({ type: "close-failed", jobId });
  } else if (outcome === "spared") {
    helper.spared.set(job.tabId, Date.now());
  } else if (!isAutomatic(job.reason)) {
    // The user asked for this close, so do it even without the duck
    // (the window is minimised, the duck is napping, ...).
    await closeTabNow(job.tabId);
  }

  pumpQueue();
}

// ---------------------------------------------------------------------------
// Hunt mode
// ---------------------------------------------------------------------------

async function pickHuntTarget(ignoreIdleTime, brainRot) {
  let win;

  try {
    win = await chrome.windows.getLastFocused({
      populate: true,
      windowTypes: ["normal"]
    });
  } catch {
    return null;
  }

  const now = Date.now();
  const ownPages = chrome.runtime.getURL("");

  const candidates = (win.tabs || []).filter(tab => {
    if (tab.id === undefined || tab.active || tab.pinned || tab.audible) return false;
    if (tab.url?.startsWith(ownPages)) return false;

    const sparedAt = helper.spared.get(tab.id);
    if (sparedAt && now - sparedAt < SPARED_FOR_MS) return false;

    if (ignoreIdleTime) return true;
    const unseenFor = now - (tab.lastAccessed || now);
    return unseenFor >= (isBrainRot(tab.url, brainRot) ? HUNT_BRAIN_ROT_IDLE_MS : HUNT_IDLE_MS);
  });

  // Brain rot first, then whatever you have ignored the longest.
  candidates.sort((a, b) =>
    (isBrainRot(b.url, brainRot) - isBrainRot(a.url, brainRot)) ||
    ((a.lastAccessed || 0) - (b.lastAccessed || 0))
  );

  return candidates[0] || null;
}

async function maybeHunt(immediate) {
  if (helper.status !== "connected" || Date.now() < helper.napUntil) return false;
  if (helper.current || helper.queue.length) return false;

  const settings = await getSettings();

  if (!immediate) {
    const prefs = await getPrefs();
    if (!prefs.huntMode) return false;
    if (Date.now() - helper.lastHuntAt < HUNT_COOLDOWN_MS) return false;
    if (await getTabCount() < settings.warningTabs) return false;
  }

  const target = await pickHuntTarget(immediate, settings.brainRot);
  if (!target) return false;

  helper.lastHuntAt = Date.now();
  await requestClose(target.id, "hunt");
  return true;
}

// ---------------------------------------------------------------------------
// The tab strip helper (native messaging)
// ---------------------------------------------------------------------------

const helper = {
  port: null,
  // connecting | connected | missing | stopped | disconnected | off
  status: "connecting",
  retries: 0,
  queue: [],
  current: null,
  pumping: false,
  jobCounter: 0,
  spared: new Map(),
  lastHuntAt: 0,
  napUntil: 0
};

// Hand anything the duck had not finished back as a plain close; the
// duck's own hunts and focus kills are dropped.
function releaseJobs() {
  const pending = [helper.current, ...helper.queue].filter(Boolean);
  if (helper.current) clearTimeout(helper.current.timer);
  helper.current = null;
  helper.queue = [];
  pending
    .filter(job => !isAutomatic(job.reason))
    .forEach(job => closeTabNow(job.tabId));
}

// The master switch is off. Closing the port makes Chrome close the
// helper's input, and the helper quits.
function disconnectHelper() {
  helper.status = "off";
  const port = helper.port;
  helper.port = null;
  releaseJobs();
  port?.disconnect();
}

function connectHelper() {
  if (helper.port || helper.status === "off") return;

  helper.status = "connecting";

  let port;
  try {
    port = chrome.runtime.connectNative(HELPER_NAME);
  } catch {
    helper.status = "missing";
    return;
  }

  helper.port = port;
  port.onMessage.addListener(onHelperMessage);

  port.onDisconnect.addListener(() => {
    const error = chrome.runtime.lastError?.message || "";
    if (helper.port !== port) return;
    helper.port = null;

    releaseJobs();

    if (helper.status === "stopped" || helper.status === "off") return;

    if (/not found|forbidden/i.test(error)) {
      helper.status = "missing";
      return;
    }

    helper.status = "disconnected";
    if (helper.retries < 3) {
      helper.retries++;
      setTimeout(connectHelper, 3000 * helper.retries);
    }
  });
}

// Titles of the tabs in the last focused window. The helper uses them to
// find that window and to stay out of other Chrome profiles' windows.
async function getFocusedTitles() {
  try {
    const win = await chrome.windows.getLastFocused({
      populate: true,
      windowTypes: ["normal"]
    });
    return (win.tabs || [])
      .sort((a, b) => a.index - b.index)
      .map(tab => tab.title || "");
  } catch {
    return [];
  }
}

function helperSay(text) {
  if (helper.port && helper.status === "connected") {
    helper.port.postMessage({ type: "say", text });
  }
}

async function sendStateToHelper(state) {
  if (!helper.port || helper.status !== "connected") return;

  const dashboard = state || await getDashboard();

  helper.port.postMessage({
    type: "state",
    mood: dashboard.mood,
    tabCount: dashboard.tabCount,
    level: dashboard.level,
    sound: dashboard.prefs.sound,
    titles: await getFocusedTitles()
  });
}

let helperStateTimer = null;

// Lighter than a full broadcast: tab titles change often (unread counts,
// timers) and only the helper cares about them.
function scheduleHelperState() {
  if (helper.status !== "connected") return;

  clearTimeout(helperStateTimer);
  helperStateTimer = setTimeout(() => {
    sendStateToHelper().catch(() => {});
  }, 400);
}

// Find the tab the user dropped the duck on, from the helper's view of the
// tab strip (tab names in order plus the index of the drop target).
async function resolveDrop(message) {
  const names = message.names || [];
  const windows = await chrome.windows.getAll({
    populate: true,
    windowTypes: ["normal"]
  });

  const matches = (name, tab) => Boolean(tab.title) && name.startsWith(tab.title);

  let best = null;
  let bestScore = 0;

  for (const win of windows) {
    const tabs = (win.tabs || []).sort((a, b) => a.index - b.index);
    const score = tabs.filter((tab, i) => names[i] && matches(names[i], tab)).length;
    if (score > bestScore) {
      best = tabs;
      bestScore = score;
    }
  }

  if (!best) return null;

  const atIndex = best[message.index];
  if (atIndex && matches(message.name || "", atIndex)) return atIndex;

  return best.find(tab => matches(message.name || "", tab)) || atIndex || null;
}

async function onHelperMessage(message) {
  switch (message?.type) {
    case "ready":
      helper.status = "connected";
      helper.retries = 0;
      await sendStateToHelper();
      pumpQueue();
      break;

    case "pecked":
      await settleJob(message.jobId, "pecked");
      break;

    case "abort":
      await settleJob(message.jobId, message.reason);
      break;

    case "drop": {
      const tab = await resolveDrop(message);
      if (tab) {
        await requestClose(tab.id, "drop");
      } else {
        helper.port?.postMessage({ type: "say", text: "HUH?" });
      }
      break;
    }

    case "setting":
      if (typeof message.sound === "boolean") {
        await chrome.storage.local.set({ sound: message.sound });
      }
      break;

    case "nap":
      helper.napUntil = Date.now() + (message.minutes || 30) * 60 * 1000;
      break;

    case "bye":
      // The user sent the duck away from its right-click menu.
      helper.status = "stopped";
      break;
  }
}

// ---------------------------------------------------------------------------
// Start-up and events
// ---------------------------------------------------------------------------

// Every time the service worker starts, make sure the alarm exists.
// Alarms are not guaranteed to survive a browser restart.
initializeAlarm().catch(error => {
  console.warn("Could not create the duck alarm:", error);
});

getPrefs()
  .then(async prefs => {
    if (!prefs.enabled) {
      helper.status = "off";
      return;
    }

    connectHelper();
    await finishExpiredFocus();
    await settleStreak(await getSettings());
  })
  .catch(error => console.warn("Duck start-up check failed:", error));

// Register all event listeners at the top level so Chrome can wake the
// service worker for them.
chrome.runtime.onInstalled.addListener(async () => {
  scheduleBroadcast();
  if (!(await getPrefs()).consented) await openWelcome();
});
chrome.runtime.onStartup.addListener(scheduleBroadcast);

chrome.alarms.onAlarm.addListener(async (alarm) => {
  if (alarm.name === ALARM_NAME) {
    await trackBrowsingTime();
  } else if (alarm.name === FOCUS_ALARM) {
    await finishFocus(true);
  }
});

chrome.tabs.onCreated.addListener(scheduleBroadcast);
chrome.tabs.onRemoved.addListener((tabId) => {
  helper.spared.delete(tabId);
  scheduleBroadcast();
});
chrome.tabs.onActivated.addListener(scheduleBroadcast);
chrome.tabs.onAttached.addListener(scheduleBroadcast);
chrome.tabs.onDetached.addListener(scheduleBroadcast);
chrome.tabs.onMoved.addListener(scheduleHelperState);
chrome.tabs.onUpdated.addListener((tabId, changeInfo) => {
  // A changed URL without a reload is a single-page site moving on (from
  // YouTube's home page into Shorts, say), which can start or end a block.
  if (changeInfo.status === "complete" || changeInfo.url !== undefined) {
    scheduleBroadcast();
  } else if (changeInfo.title !== undefined) {
    scheduleHelperState();
  }
});

chrome.windows.onFocusChanged.addListener(scheduleBroadcast);

chrome.storage.onChanged.addListener((changes, area) => {
  if (area !== "local") return;

  // New thresholds or brain rot sites can change every page's overlay.
  if ("settings" in changes) {
    scheduleBroadcast();
  } else if ("sound" in changes || "xp" in changes) {
    scheduleHelperState();
  }
});

// Messages from the popup and content scripts
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {

  if (message.type === "GET_DASHBOARD") {
    getDashboard().then(state => {
      // A page asking for itself also learns whether focus mode blocks it.
      const url = sender.tab?.url;
      sendResponse({
        ...state,
        blocked: Boolean(state.focus && url) && isBrainRot(url, state.settings.brainRot)
      });
    });
    return true;
  }

  if (message.type === "GET_REPORT") {
    getReport().then(sendResponse);
    return true;
  }

  if (message.type === "SAVE_SETTINGS") {
    const settings = cleanSettings(message.settings);
    chrome.storage.local.set({ settings })
      .then(() => sendResponse({ success: true, settings }));
    return true;
  }

  if (message.type === "RESET_SETTINGS") {
    chrome.storage.local.remove("settings")
      .then(() => sendResponse({ success: true, settings: cleanSettings(null) }));
    return true;
  }

  if (message.type === "START_FOCUS") {
    isEnabled()
      .then(on => {
        if (!on) throw new Error("The duck is off");
        return getFocus();
      })
      .then(focus => focus || startFocus())
      .then(focus => sendResponse({ success: true, focus }))
      .catch(error => sendResponse({ success: false, error: error.message }));
    return true;
  }

  if (message.type === "STOP_FOCUS") {
    finishFocus(false).then(() => sendResponse({ success: true }));
    return true;
  }

  if (message.type === "GET_TABS") {
    chrome.tabs.query({ windowType: "normal" }).then(tabs => {
      sendResponse(tabs.map(tab => ({
        id: tab.id,
        title: tab.title || "Untitled tab",
        url: tab.url || ""
      })));
    });

    return true;
  }

  if (message.type === "CLOSE_TAB") {
    requestClose(message.tabId, "user").then(sendResponse);
    return true;
  }

  // Sent by the webpage overlay's "CLOSE THIS TAB" button. The user always
  // chooses to close; outside hunt mode and focus mode the duck never closes
  // anything on its own.
  if (message.type === "CLOSE_CURRENT_TAB" && sender.tab?.id !== undefined) {
    requestClose(sender.tab.id, "user").then(sendResponse);
    return true;
  }

  if (message.type === "GIVE_CONSENT") {
    giveConsent().then(sendResponse);
    return true;
  }

  if (message.type === "SET_ENABLED" ||
      (message.type === "SET_PREF" && message.key === "enabled")) {
    setEnabled(Boolean(message.value)).then(sendResponse);
    return true;
  }

  if (message.type === "SET_PREF" && message.key in DEFAULT_PREFS) {
    chrome.storage.local.set({ [message.key]: Boolean(message.value) })
      .then(() => sendResponse({ success: true }));
    return true;
  }

  if (message.type === "HUNT_NOW") {
    maybeHunt(true).then(found => sendResponse({ success: found }));
    return true;
  }

  if (message.type === "WAKE_HELPER") {
    if (helper.status === "off") {
      sendResponse({ helper: helper.status });
      return false;
    }
    helper.retries = 0;
    helper.napUntil = 0;
    if (helper.status === "stopped") helper.status = "disconnected";
    connectHelper();
    setTimeout(() => sendResponse({ helper: helper.status }), 800);
    return true;
  }

});
