// Killing Duck - webpage intervention overlay.
//
// Runs on regular http/https pages. The duck stays hidden while browsing is
// healthy and appears in the bottom-right corner when the background worker
// reports a worried, stressed or exhausted mood. During a focus session it
// covers brain rot pages completely.

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

      /* Focus mode: the whole page is covered */
      :host(.blocking) {
        inset: 0;
        right: 0;
        bottom: 0;
      }

      .cover {
        display: none;
        position: fixed;
        inset: 0;
        align-items: center;
        justify-content: center;
        background: rgba(53, 45, 36, 0.94);
      }

      :host(.blocking) .cover {
        display: flex;
      }

      :host(.blocking) .card:not(.focus-card) {
        display: none;
      }

      .focus-card {
        width: 300px;
      }

      .label {
        font-size: 11px;
        font-weight: bold;
        letter-spacing: 2px;
        text-align: center;
        margin: 10px 0 0;
      }

      .countdown {
        font-size: 40px;
        font-weight: bold;
        text-align: center;
        margin: 2px 0 8px;
        font-variant-numeric: tabular-nums;
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

    <div class="cover">
      <div class="card focus-card" role="alertdialog" aria-labelledby="focus-title">
        <img class="duck" alt=""
             src="${chrome.runtime.getURL("assets/duck.svg")}">

        <p class="label">FOCUS MODE</p>
        <p class="countdown" id="countdown">25:00</p>

        <h3 id="focus-title">THIS IS BRAIN ROT</h3>
        <p>The duck is guarding your focus. Get back to work and earn the XP.</p>

        <button id="focus-kill">KILL THIS TAB</button>
        <button id="focus-quit" class="close">END FOCUS EARLY (NO XP)</button>
      </div>
    </div>
  `;

  document.documentElement.appendChild(widget);

  const messageText = shadow.querySelector("#message");

  // Severity at which the user dismissed the duck. It stays hidden until the
  // mood gets worse than this, or recovers to happy and then worsens again.
  let dismissedSeverity = 0;
  let currentSeverity = 0;

  const countdown = shadow.querySelector("#countdown");
  let focusUntil = 0;
  let focusTimer = null;

  function hide() {
    widget.style.display = "none";
  }

  function show() {
    widget.style.display = "block";
  }

  function formatLeft(ms) {
    const seconds = Math.max(0, Math.ceil(ms / 1000));
    return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`;
  }

  // Once a second: count down, and keep the page's videos from playing
  // behind the cover (Shorts and reels start the next one on their own).
  function tickFocus() {
    const left = focusUntil - Date.now();
    if (left <= 0) {
      unblock();
      return;
    }

    countdown.textContent = formatLeft(left);
    document.querySelectorAll("video, audio").forEach(media => {
      if (!media.paused) media.pause();
    });
  }

  function block(until) {
    focusUntil = until;
    widget.classList.add("blocking");
    show();
    tickFocus();
    if (!focusTimer) focusTimer = setInterval(tickFocus, 1000);
  }

  function unblock() {
    clearInterval(focusTimer);
    focusTimer = null;
    focusUntil = 0;
    widget.classList.remove("blocking");
    hide();
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

    // The master switch is off: no overlay, no block.
    if (state.enabled === false) {
      if (focusUntil) unblock();
      dismissedSeverity = 0;
      hide();
      return;
    }

    if (state.focus && state.blocked) {
      block(state.focus.until);
      return;
    }

    if (focusUntil) unblock();

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

  shadow.querySelector("#focus-kill").addEventListener("click", () => {
    sendToBackground({ type: "CLOSE_CURRENT_TAB" });
  });

  shadow.querySelector("#focus-quit").addEventListener("click", () => {
    sendToBackground({ type: "STOP_FOCUS" });
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
