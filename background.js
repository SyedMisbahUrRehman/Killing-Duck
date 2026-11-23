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

const SETTINGS = {
  warningTabs: 6,
  stressedTabs: 11,
  criticalTabs: 16,
  warningMinutes: 25,
  criticalMinutes: 45
};

const ALARM_NAME = "duck-browsing-monitor";

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

const BRAIN_ROT = [
  "youtube.com/shorts",
  "tiktok.com",
  "instagram.com",
  "reddit.com",
  "x.com",
  "twitter.com",
  "facebook.com",
  "9gag.com",
  "twitch.tv"
];

const DEFAULT_PREFS = {
  huntMode: false,
  sound: true
};

function isWebUrl(url) {
  return typeof url === "string" &&
    (url.startsWith("http://") || url.startsWith("https://"));
}

function isBrainRot(url) {
  if (!isWebUrl(url)) return false;

  try {
    const { hostname, pathname } = new URL(url);
    const host = hostname.replace(/^www\.|^m\./, "");
    return BRAIN_ROT.some(entry => {
      const [entryHost, entryPath = ""] = entry.split(/\/(.*)/);
      return (host === entryHost || host.endsWith(`.${entryHost}`)) &&
        pathname.startsWith(`/${entryPath}`);
    });
  } catch {
    return false;
  }
}

// Today's date in local time, used to reset statistics daily
function getTodayKey() {
  const date = new Date();

  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");

  return `${year}-${month}-${day}`;
}

async function getPrefs() {
  const data = await chrome.storage.local.get(Object.keys(DEFAULT_PREFS));
  return { ...DEFAULT_PREFS, ...data };
}

// Count tabs in normal browser windows
async function getTabCount() {
  const tabs = await chrome.tabs.query({
    windowType: "normal"
  });

  return tabs.length;
}

// Today's accumulated active minutes
async function getActiveMinutes() {
  const data = await chrome.storage.local.get([
    "activeMinutes",
    "date"
  ]);

  return data.date === getTodayKey()
    ? (data.activeMinutes || 0)
    : 0;
}

async function getKillStats() {
  const data = await chrome.storage.local.get([
    "killsToday",
    "killsDate",
    "killsTotal"
  ]);

  const killsTotal = data.killsTotal || 0;

  return {
    killsToday: data.killsDate === getTodayKey() ? (data.killsToday || 0) : 0,
    killsTotal,
    level: getLevel(killsTotal)
  };
}

// Level 2 earns the duck a headband, level 3 adds sunglasses.
function getLevel(killsTotal) {
  if (killsTotal >= 30) return 3;
  if (killsTotal >= 10) return 2;
  return 1;
}

async function recordKill() {
  const stats = await getKillStats();

  await chrome.storage.local.set({
    killsToday: stats.killsToday + 1,
    killsDate: getTodayKey(),
    killsTotal: stats.killsTotal + 1
  });
}

// Determine the duck's mood
function getDuckMood(tabCount, minutes) {
  if (tabCount >= SETTINGS.criticalTabs ||
      minutes >= SETTINGS.criticalMinutes) {
    return "exhausted";
  }

  if (tabCount >= SETTINGS.stressedTabs) {
    return "stressed";
  }

  if (tabCount >= SETTINGS.warningTabs ||
      minutes >= SETTINGS.warningMinutes) {
    return "worried";
  }

  return "happy";
}

// Current dashboard state, shared by the popup and the webpage overlay
async function getDashboard() {
  const [tabCount, activeMinutes, prefs, stats] = await Promise.all([
    getTabCount(),
    getActiveMinutes(),
    getPrefs(),
    getKillStats()
  ]);

  return {
    tabCount,
    activeMinutes,
    mood: getDuckMood(tabCount, activeMinutes),
    settings: SETTINGS,
    prefs,
    ...stats,
    helper: helper.status,
    napping: Date.now() < helper.napUntil
  };
}

// Send the current state to every open webpage and refresh the badge
async function broadcastState() {
  const state = await getDashboard();

  const tabs = await chrome.tabs.query({});

  await Promise.all(
    tabs
      .filter(tab => tab.id !== undefined && isWebUrl(tab.url))
      .map(tab =>
        chrome.tabs.sendMessage(tab.id, {
          type: "DUCK_UPDATE",
          state
        }).catch(() => {
          // Page has no content script yet (still loading, or opened
          // before the extension was installed).
        })
      )
  );

  // Show the current tab count on the extension badge
  await chrome.action.setBadgeText({
    text: String(state.tabCount)
  });

  await chrome.action.setBadgeBackgroundColor({
    color: state.tabCount >= SETTINGS.criticalTabs
      ? "#dc2626"
      : "#64748b"
  });

  await sendStateToHelper(state);
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

// True when the user is at the keyboard, Chrome is focused and the active
// tab is a regular website
async function isActivelyBrowsing() {
  const idleState = await chrome.idle.queryState(IDLE_THRESHOLD_SECONDS);

  if (idleState !== "active") {
    return false;
  }

  let focusedWindow;

  try {
    focusedWindow = await chrome.windows.getLastFocused({
      populate: true,
      windowTypes: ["normal"]
    });
  } catch {
    // No browser window is open
    return false;
  }

  if (!focusedWindow.focused || focusedWindow.state === "minimized") {
    return false;
  }

  const activeTab = focusedWindow.tabs?.find(tab => tab.active);

  return isWebUrl(activeTab?.url);
}

// Accumulate active browsing time, one minute per alarm tick
async function trackBrowsingTime() {
  if (await isActivelyBrowsing()) {
    const previousMinutes = await getActiveMinutes();

    await chrome.storage.local.set({
      activeMinutes: previousMinutes + 1,
      date: getTodayKey()
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
    await chrome.tabs.remove(tabId);
    await recordKill();
    return { success: true };
  } catch (error) {
    return { success: false, error: error.message };
  }
}

// Every close the user asks for goes through here. With the helper running,
// the duck walks over and pecks the tab's close button first; otherwise the
// tab closes straight away.
async function requestClose(tabId, reason) {
  if (helper.status !== "connected" || Date.now() < helper.napUntil) {
    if (reason === "hunt") return { success: false, error: "The duck is not on the tab strip" };
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
  } else if (outcome !== "cancel" && job.reason !== "hunt") {
    // The user asked for this close, so do it even without the duck
    // (the window is minimised, the duck is napping, ...).
    await closeTabNow(job.tabId);
  }

  pumpQueue();
}

// ---------------------------------------------------------------------------
// Hunt mode
// ---------------------------------------------------------------------------

async function pickHuntTarget(ignoreIdleTime) {
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
    return unseenFor >= (isBrainRot(tab.url) ? HUNT_BRAIN_ROT_IDLE_MS : HUNT_IDLE_MS);
  });

  // Brain rot first, then whatever you have ignored the longest.
  candidates.sort((a, b) =>
    (isBrainRot(b.url) - isBrainRot(a.url)) ||
    ((a.lastAccessed || 0) - (b.lastAccessed || 0))
  );

  return candidates[0] || null;
}

async function maybeHunt(immediate) {
  if (helper.status !== "connected" || Date.now() < helper.napUntil) return false;
  if (helper.current || helper.queue.length) return false;

  if (!immediate) {
    const prefs = await getPrefs();
    if (!prefs.huntMode) return false;
    if (Date.now() - helper.lastHuntAt < HUNT_COOLDOWN_MS) return false;
    if (await getTabCount() < SETTINGS.warningTabs) return false;
  }

  const target = await pickHuntTarget(immediate);
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
  // connecting | connected | missing | stopped | disconnected
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

function connectHelper() {
  if (helper.port) return;

  helper.status = "connecting";

  let port;
  try {
    port = chrome.runtime.connectNative(HELPER_NAME);
  } catch (error) {
    helper.status = "missing";
    return;
  }

  helper.port = port;
  port.onMessage.addListener(onHelperMessage);

  port.onDisconnect.addListener(() => {
    const error = chrome.runtime.lastError?.message || "";
    helper.port = null;

    // Hand anything the duck had not finished back as a plain close.
    const pending = [helper.current, ...helper.queue].filter(Boolean);
    if (helper.current) clearTimeout(helper.current.timer);
    helper.current = null;
    helper.queue = [];
    pending
      .filter(job => job.reason !== "hunt")
      .forEach(job => closeTabNow(job.tabId));

    if (helper.status === "stopped") return;

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

connectHelper();

// Register all event listeners at the top level so Chrome can wake the
// service worker for them.
chrome.runtime.onInstalled.addListener(scheduleBroadcast);
chrome.runtime.onStartup.addListener(scheduleBroadcast);

chrome.alarms.onAlarm.addListener(async (alarm) => {
  if (alarm.name === ALARM_NAME) {
    await trackBrowsingTime();
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
  if (changeInfo.status === "complete") {
    scheduleBroadcast();
  } else if (changeInfo.title !== undefined) {
    scheduleHelperState();
  }
});

chrome.windows.onFocusChanged.addListener(scheduleBroadcast);

chrome.storage.onChanged.addListener((changes, area) => {
  if (area === "local" && ("sound" in changes || "killsTotal" in changes)) {
    scheduleHelperState();
  }
});

// Messages from the popup and content scripts
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {

  if (message.type === "GET_DASHBOARD") {
    getDashboard().then(sendResponse);
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
  // chooses to close; outside hunt mode the duck never closes anything on
  // its own.
  if (message.type === "CLOSE_CURRENT_TAB" && sender.tab?.id !== undefined) {
    requestClose(sender.tab.id, "user").then(sendResponse);
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
    helper.retries = 0;
    helper.napUntil = 0;
    if (helper.status === "stopped") helper.status = "disconnected";
    connectHelper();
    setTimeout(() => sendResponse({ helper: helper.status }), 800);
    return true;
  }

});
