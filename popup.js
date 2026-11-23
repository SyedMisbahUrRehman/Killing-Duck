const tabCount = document.getElementById("tab-count");
const activeTime = document.getElementById("active-time");

const duckTitle = document.getElementById("duck-title");
const duckMessage = document.getElementById("duck-message");

const tabList = document.getElementById("tab-list");
const tabLabel = document.getElementById("tab-label");

const duck = document.getElementById("duck");

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
    closeButton.textContent = "CLOSE";
    closeButton.setAttribute("aria-label", `Close tab: ${tab.title}`);

    closeButton.addEventListener("click", async () => {
      if (tab.id === undefined) return;

      closeButton.disabled = true;

      const result = await chrome.runtime.sendMessage({
        type: "CLOSE_TAB",
        tabId: tab.id
      });

      if (result?.success) {
        await refreshDashboard();
      } else {
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

// Initial load
refreshDashboard();
