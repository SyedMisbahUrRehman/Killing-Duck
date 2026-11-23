// Killing Duck - background service worker.
//
// Manifest V3 service workers are stopped when idle and restarted on the next
// event, so nothing important lives in global variables. Browsing time is
// persisted in chrome.storage.local and periodic work runs on chrome.alarms.

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

function isWebUrl(url) {
  return typeof url === "string" &&
    (url.startsWith("http://") || url.startsWith("https://"));
}

// Today's date in local time, used to reset statistics daily
function getTodayKey() {
  const date = new Date();

  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");

  return `${year}-${month}-${day}`;
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
  const [tabCount, activeMinutes] = await Promise.all([
    getTabCount(),
    getActiveMinutes()
  ]);

  return {
    tabCount,
    activeMinutes,
    mood: getDuckMood(tabCount, activeMinutes),
    settings: SETTINGS
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

// Every time the service worker starts, make sure the alarm exists.
// Alarms are not guaranteed to survive a browser restart.
initializeAlarm().catch(error => {
  console.warn("Could not create the duck alarm:", error);
});

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
chrome.tabs.onRemoved.addListener(scheduleBroadcast);
chrome.tabs.onActivated.addListener(scheduleBroadcast);
chrome.tabs.onAttached.addListener(scheduleBroadcast);
chrome.tabs.onDetached.addListener(scheduleBroadcast);
chrome.tabs.onUpdated.addListener((tabId, changeInfo) => {
  if (changeInfo.status === "complete") {
    scheduleBroadcast();
  }
});

chrome.windows.onFocusChanged.addListener(scheduleBroadcast);

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
    chrome.tabs.remove(message.tabId)
      .then(() => sendResponse({ success: true }))
      .catch(error => sendResponse({
        success: false,
        error: error.message
      }));

    return true;
  }

  // Sent by the webpage overlay's "CLOSE THIS TAB" button. The user always
  // chooses to close; the duck never closes anything on its own.
  if (message.type === "CLOSE_CURRENT_TAB" && sender.tab?.id !== undefined) {
    chrome.tabs.remove(sender.tab.id)
      .then(() => sendResponse({ success: true }))
      .catch(error => sendResponse({
        success: false,
        error: error.message
      }));

    return true;
  }

});
