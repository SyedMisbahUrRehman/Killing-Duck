// Killing Duck - webpage intervention overlay.
//
// Runs on regular http/https pages. The duck stays hidden while browsing is
// healthy and appears in the bottom-right corner when the background worker
// reports a worried, stressed or exhausted mood.

(() => {
  // Prevent duplicate overlays
  if (document.getElementById("tab-duck-widget")) return;

  const SEVERITY = {
    happy: 0,
    worried: 1,
    stressed: 2,
    exhausted: 3
  };

  const widget = document.createElement("div");
  widget.id = "tab-duck-widget";

  // Hidden until the first state arrives, so healthy pages never flash it
  widget.style.display = "none";

  const shadow = widget.attachShadow({ mode: "open" });

  shadow.innerHTML = `
    <style>
      :host {
        all: initial;
        position: fixed;
        right: 22px;
        bottom: 22px;
        z-index: 2147483647;
        font-family: monospace;
      }

      .card {
        width: 235px;
        padding: 15px;
        background: #fff3ce;
        color: #352d24;
        border: 4px solid #352d24;
        box-shadow: 6px 6px 0 #352d24;
        image-rendering: pixelated;
      }

      .duck {
        display: block;
        width: 80px;
        height: 80px;
        margin: 0 auto;
        image-rendering: pixelated;
        animation: bounce 1.5s steps(2) infinite;
      }

      h3 {
        text-align: center;
        font-size: 15px;
        margin: 8px 0;
      }

      p {
        font-size: 12px;
        line-height: 1.6;
        text-align: center;
        margin: 0 0 6px;
      }

      button {
        display: block;
        width: 100%;
        padding: 9px;
        margin-top: 7px;
        border: 3px solid #352d24;
        background: #f5cf45;
        color: #352d24;
        font-family: monospace;
        font-size: 12px;
        font-weight: bold;
        cursor: pointer;
      }

      button:hover {
        background: #e9b932;
      }

      .close {
        background: #fff;
      }

      @keyframes bounce {
        50% { transform: translateY(-4px); }
      }

      @media (prefers-reduced-motion: reduce) {
        .duck { animation: none; }
      }
    </style>

    <div class="card" role="alert">
      <img class="duck" alt="Killing duck"
           src="${chrome.runtime.getURL("assets/duck.svg")}">

      <h3>QUACK! 🦆</h3>

      <p id="message">
        You have too many tabs open!
      </p>

      <button id="close-tab">
        CLOSE THIS TAB
      </button>

      <button id="dismiss" class="close">
        I'LL BE BACK
      </button>
    </div>
  `;

  document.documentElement.appendChild(widget);

  const messageText = shadow.querySelector("#message");

  // Severity at which the user dismissed the duck. It stays hidden until the
  // mood gets worse than this, or recovers to happy and then worsens again.
  let dismissedSeverity = 0;
  let currentSeverity = 0;

  function hide() {
    widget.style.display = "none";
  }

  function show() {
    widget.style.display = "block";
  }

  // After the extension is reloaded or removed, this script is orphaned and
  // chrome.runtime calls throw. Remove the overlay quietly in that case.
  function sendToBackground(payload) {
    if (!chrome.runtime?.id) {
      widget.remove();
      return Promise.resolve(undefined);
    }

    return chrome.runtime.sendMessage(payload).catch(() => undefined);
  }

  function getMessage(state) {
    const { tabCount, activeMinutes, settings } = state;

    if (activeMinutes >= settings.criticalMinutes) {
      return `You've been browsing for ${settings.criticalMinutes}+ minutes. ` +
        "Please take a break!";
    }

    if (tabCount >= settings.criticalTabs) {
      return `QUACK! ${tabCount} tabs! Help me clean this mess!`;
    }

    if (tabCount >= settings.stressedTabs) {
      return `${tabCount} tabs open. Are you really using all of them?`;
    }

    if (activeMinutes >= settings.warningMinutes) {
      return `${settings.warningMinutes} minutes of browsing! ` +
        "Time to stretch and rest.";
    }

    return `${tabCount} tabs open. Maybe close a few?`;
  }

  function updateDuck(state) {
    if (!state?.settings) return;

    currentSeverity = SEVERITY[state.mood] ?? 0;

    if (currentSeverity === 0) {
      dismissedSeverity = 0;
      hide();
      return;
    }

    if (currentSeverity <= dismissedSeverity) {
      hide();
      return;
    }

    messageText.textContent = getMessage(state);
    show();
  }

  shadow.querySelector("#close-tab").addEventListener("click", () => {
    sendToBackground({ type: "CLOSE_CURRENT_TAB" });
  });

  shadow.querySelector("#dismiss").addEventListener("click", () => {
    dismissedSeverity = currentSeverity;
    hide();
  });

  chrome.runtime.onMessage.addListener((message) => {
    if (message?.type === "DUCK_UPDATE") {
      updateDuck(message.state);
    }
  });

  // Request the current state when the page loads
  sendToBackground({ type: "GET_DASHBOARD" }).then(updateDuck);

})();
