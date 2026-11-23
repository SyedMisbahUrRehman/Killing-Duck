const tabCount = document.getElementById("tab-count");
const activeTime = document.getElementById("active-time");

const duckTitle = document.getElementById("duck-title");
const duckMessage = document.getElementById("duck-message");

const tabList = document.getElementById("tab-list");
const tabLabel = document.getElementById("tab-label");

const duck = document.getElementById("duck");

const levelLabel = document.getElementById("level-label");
const helperStatus = document.getElementById("helper-status");
const wakeHelper = document.getElementById("wake-helper");
const killsToday = document.getElementById("kills-today");
const killsTotal = document.getElementById("kills-total");
const huntMode = document.getElementById("hunt-mode");
const sound = document.getElementById("sound");
const huntNow = document.getElementById("hunt-now");

const HELPER_STATUS = {
  connected: "Living on your tab strip. Drag it onto a tab, or press KILL below, " +
    "and it pecks that tab's close button.",
  connecting: "Waking the duck up...",
  missing: "Not installed. Run <code>helper\\install.cmd</code> in the extension " +
    "folder, then reload the extension, to put the duck on your tab strip. " +
    "Until then, tabs close straight away.",
  disconnected: "The duck wandered off. Tabs close straight away for now.",
  stopped: "You sent the duck away. Tabs close straight away for now."
};

const MOODS = {
  happy: {
    title: "QUACK! I'M HAPPY",
    message: "Your browsing looks healthy."
  },

  worried: {
    title: "I'M GETTING WORRIED",
    message: "You have too many tabs or have been browsing too long."
  },

  stressed: {
    title: "TOO MANY TABS!",
    message: "Please close some unnecessary tabs."
  },

  exhausted: {
    title: "I NEED A BREAK!",
    message: "Your browsing session is getting excessive."
  }
};

// Convert minutes into a readable format
function formatTime(minutes) {
  const hours = Math.floor(minutes / 60);
  const remaining = minutes % 60;

  if (hours === 0) return `${remaining}m`;

  return `${hours}h ${remaining}m`;
}

function pluralTabs(count) {
  return `${count} ${count === 1 ? "tab" : "tabs"}`;
}

// Update the duck according to browsing behaviour
function updateUI(state) {
  tabCount.textContent = state.tabCount;
  activeTime.textContent = formatTime(state.activeMinutes);

  const mood = MOODS[state.mood] || MOODS.happy;

  duckTitle.textContent = mood.title;
  duckMessage.textContent = mood.message;

  duck.style.filter = state.mood === "exhausted"
    ? "grayscale(0.4)"
    : "none";

  const onStrip = state.helper === "connected" && !state.napping;

  helperStatus.innerHTML = state.napping
    ? "Napping. Tabs close straight away until it wakes up."
    : (HELPER_STATUS[state.helper] || HELPER_STATUS.connecting);
  wakeHelper.hidden = !(state.napping ||
    state.helper === "stopped" || state.helper === "disconnected");

  levelLabel.textContent = `LV ${state.level}`;
  killsToday.textContent = state.killsToday;
  killsTotal.textContent = state.killsTotal;

  huntMode.checked = state.prefs.huntMode;
  sound.checked = state.prefs.sound;
  huntMode.disabled = !onStrip;
  huntNow.disabled = !onStrip;
}

function showError() {
  duckTitle.textContent = "QUACK?";
  duckMessage.textContent =
    "The duck could not reach the extension. Try reopening this popup.";
}

// Fetch and display open tabs
async function loadTabs() {
  const tabs = await chrome.runtime.sendMessage({
    type: "GET_TABS"
  }) || [];

  tabLabel.textContent = pluralTabs(tabs.length);

  tabList.replaceChildren();

  if (!tabs.length) {
    const empty = document.createElement("p");
    empty.className = "empty";
    empty.textContent = "No open tabs found.";
    tabList.appendChild(empty);
    return;
  }

  tabs.forEach(tab => {
    const item = document.createElement("div");
    item.className = "tab-item";

    const title = document.createElement("span");
    title.className = "tab-title";
    title.textContent = tab.title;
    title.title = tab.url;

    const closeButton = document.createElement("button");
    closeButton.className = "tab-close";
    closeButton.textContent = "KILL";
    closeButton.setAttribute("aria-label", `Kill tab: ${tab.title}`);

    closeButton.addEventListener("click", async () => {
      if (tab.id === undefined) return;

      closeButton.disabled = true;

      const result = await chrome.runtime.sendMessage({
        type: "CLOSE_TAB",
        tabId: tab.id
      });

      // With the duck on the tab strip the tab closes a moment later, when
      // the duck pecks it; the onRemoved listener below refreshes the list.
      if (result?.success && !result.duck) {
        await refreshDashboard();
      } else if (!result?.success) {
        closeButton.disabled = false;
      }
    });

    item.appendChild(title);
    item.appendChild(closeButton);

    tabList.appendChild(item);
  });
}

// Refresh everything
async function refreshDashboard() {
  try {
    const state = await chrome.runtime.sendMessage({
      type: "GET_DASHBOARD"
    });

    if (!state) {
      showError();
      return;
    }

    updateUI(state);
    await loadTabs();
  } catch (error) {
    console.warn("Duck popup failed to load:", error);
    showError();
  }
}

function setPref(key, value) {
  return chrome.runtime.sendMessage({ type: "SET_PREF", key, value });
}

huntMode.addEventListener("change", () => setPref("huntMode", huntMode.checked));
sound.addEventListener("change", () => setPref("sound", sound.checked));

huntNow.addEventListener("click", async () => {
  huntNow.disabled = true;
  const result = await chrome.runtime.sendMessage({ type: "HUNT_NOW" });

  if (!result?.success) {
    huntNow.textContent = "NOTHING TO KILL (ACTIVE, PINNED AND NOISY TABS ARE SAFE)";
    setTimeout(() => {
      huntNow.textContent = "SIC 'EM! KILL MY STALEST TAB";
      huntNow.disabled = false;
    }, 2500);
    return;
  }

  setTimeout(() => {
    huntNow.disabled = false;
  }, 1500);
});

wakeHelper.addEventListener("click", async () => {
  wakeHelper.disabled = true;
  await chrome.runtime.sendMessage({ type: "WAKE_HELPER" });
  wakeHelper.disabled = false;
  await refreshDashboard();
});

let refreshTimer = null;

// Tabs closed by the duck (or anything else) disappear from the list.
chrome.tabs.onRemoved.addListener(() => {
  clearTimeout(refreshTimer);
  refreshTimer = setTimeout(refreshDashboard, 150);
});

// Initial load
refreshDashboard();
