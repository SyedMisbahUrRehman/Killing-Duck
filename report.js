// Killing Duck - weekly report and settings page (the extension's options page).

const $ = id => document.getElementById(id);

const PLOT_HEIGHT = 180;
const NUMBER_FIELDS = [
  "warningTabs", "stressedTabs", "criticalTabs",
  "warningMinutes", "criticalMinutes",
  "rotBudgetMinutes", "focusMinutes"
];

const form = $("settings");
const saved = $("saved");
const tooltip = $("tooltip");

function formatTime(minutes) {
  const hours = Math.floor(minutes / 60);
  const remaining = minutes % 60;

  if (hours === 0) return `${remaining}m`;
  if (remaining === 0) return `${hours}h`;
  return `${hours}h ${remaining}m`;
}

function plural(count, word) {
  return `${count} ${word}${count === 1 ? "" : "s"}`;
}

function dayName(dateKey, today, style) {
  if (dateKey === today) return "Today";

  const [year, month, day] = dateKey.split("-").map(Number);
  return new Date(year, month - 1, day).toLocaleDateString(undefined, style);
}

// A day nobody browsed on has no verdict; today's is only so far.
function budgetStatus(day, budget) {
  if (day.minutes === 0) return { icon: "–", text: "No browsing" };
  return day.rotMinutes <= budget
    ? { icon: "✓", text: "Within budget", good: true }
    : { icon: "✗", text: "Over budget", good: false };
}

// A clean axis step that gives three or four gridlines
function niceStep(max) {
  const steps = [5, 10, 15, 30, 60, 90, 120, 180, 240, 360, 480, 720];
  return steps.find(step => max / step <= 4) || Math.ceil(max / 4 / 60) * 60;
}

function renderTiles(report, week, lastWeek) {
  $("tile-level").textContent = report.level;
  $("tile-rank").textContent = `${report.rank} · ${report.xp} XP`;

  $("tile-streak").textContent = plural(report.streak, "day");
  $("tile-best").textContent = `best ${report.bestStreak}`;

  const rot = sum(week, "rotMinutes");
  const delta = rot - sum(lastWeek, "rotMinutes");
  $("tile-rot").textContent = formatTime(rot);
  $("tile-rot-delta").textContent = delta === 0
    ? "same as the 7 days before"
    : `${delta < 0 ? "↓" : "↑"} ${formatTime(Math.abs(delta))} ${delta < 0 ? "less" : "more"} than the 7 days before`;
  $("tile-rot-delta").className = delta < 0 ? "good" : delta > 0 ? "bad" : "";

  $("tile-kills").textContent = sum(week, "kills");
  $("tile-kills-total").textContent = `${report.killsTotal} all time`;
}

function sum(days, key) {
  return days.reduce((total, day) => total + day[key], 0);
}

function renderChart(week, report) {
  const budget = report.settings.rotBudgetMinutes;
  // At least half an hour of axis, so a quiet week still has a scale
  const most = Math.max(30, ...week.map(day => day.minutes));
  const step = niceStep(most);
  const top = Math.ceil(most / step) * step;
  const scale = PLOT_HEIGHT / top;

  const grid = $("grid");
  grid.replaceChildren();
  for (let value = 0; value <= top; value += step) {
    const line = document.createElement("div");
    line.className = "gridline";
    line.style.bottom = `${value * scale}px`;
    line.dataset.label = value === 0 ? "0" : formatTime(value);
    grid.appendChild(line);
  }

  const cols = $("cols");
  cols.replaceChildren();

  week.forEach(day => {
    const status = budgetStatus(day, budget);
    const other = day.minutes - day.rotMinutes;
    const label = dayName(day.date, report.today, { weekday: "short" });

    const col = document.createElement("div");
    col.className = "col";
    col.tabIndex = 0;
    col.setAttribute("aria-label",
      `${dayName(day.date, report.today, { weekday: "long", month: "long", day: "numeric" })}: ` +
      `${formatTime(day.minutes)} browsing, ${formatTime(day.rotMinutes)} brain rot. ${status.text}.`);

    const bar = document.createElement("div");
    bar.className = "bar";

    // Brain rot sits on the baseline, so days compare against the budget.
    const rotHeight = day.rotMinutes * scale;
    const otherHeight = other * scale;
    const both = rotHeight > 0 && otherHeight > 0;

    if (rotHeight > 0) {
      const seg = document.createElement("div");
      seg.className = "seg rot";
      seg.style.height = `${rotHeight}px`;
      bar.appendChild(seg);
    }

    if (otherHeight > 0) {
      const seg = document.createElement("div");
      seg.className = "seg other";
      seg.style.height = `${Math.max(1, otherHeight - (both ? 2 : 0))}px`;
      bar.appendChild(seg);
    }

    const name = document.createElement("span");
    name.className = "day-label";
    name.textContent = label;

    const mark = document.createElement("span");
    mark.className = `day-status ${status.good === true ? "good" : status.good === false ? "bad" : ""}`;
    mark.textContent = status.icon;
    mark.setAttribute("aria-hidden", "true");

    col.append(bar, name, mark);
    cols.appendChild(col);

    const show = () => showTooltip(col, day, report, status);
    col.addEventListener("mouseenter", show);
    col.addEventListener("focus", show);
    col.addEventListener("mouseleave", hideTooltip);
    col.addEventListener("blur", hideTooltip);
  });

  $("budget-note").textContent =
    `✓ marks a day with at most ${formatTime(budget)} of brain rot, your daily budget. ✗ marks a day over it.`;
}

function showTooltip(col, day, report, status) {
  const title = dayName(day.date, report.today, { weekday: "long", month: "short", day: "numeric" });

  tooltip.replaceChildren();

  const heading = document.createElement("b");
  heading.textContent = title;
  tooltip.appendChild(heading);

  [
    ["rot", "Brain rot", formatTime(day.rotMinutes)],
    ["other", "Everything else", formatTime(day.minutes - day.rotMinutes)],
    [null, "Tabs killed", String(day.kills)],
    [null, "Focus", day.focusSessions ? formatTime(day.focusMinutes) : "none"],
    [null, "Budget", status.text]
  ].forEach(([swatch, name, value]) => {
    const row = document.createElement("span");
    row.className = "row";
    if (swatch) {
      const key = document.createElement("i");
      key.className = `swatch ${swatch}`;
      row.appendChild(key);
    }
    row.append(`${name}: `);
    const strong = document.createElement("strong");
    strong.textContent = value;
    row.appendChild(strong);
    tooltip.appendChild(row);
  });

  tooltip.hidden = false;

  // Beside the column, so the bar it describes stays in view
  const chart = $("chart").getBoundingClientRect();
  const box = col.getBoundingClientRect();
  const width = tooltip.offsetWidth;
  const right = box.right - chart.left + 4;
  const left = right + width <= chart.width ? right : box.left - chart.left - width - 4;

  tooltip.style.left = `${Math.max(0, left)}px`;
}

function hideTooltip() {
  tooltip.hidden = true;
}

function renderTable(week, report) {
  const budget = report.settings.rotBudgetMinutes;
  const rows = $("day-rows");
  rows.replaceChildren();

  [...week].reverse().forEach(day => {
    const status = budgetStatus(day, budget);
    const row = document.createElement("tr");

    const cells = [
      dayName(day.date, report.today, { weekday: "short", month: "short", day: "numeric" }),
      formatTime(day.minutes),
      formatTime(day.rotMinutes),
      day.rotKills ? `${day.kills} (${day.rotKills} brain rot)` : String(day.kills),
      day.focusSessions ? `${plural(day.focusSessions, "session")}, ${formatTime(day.focusMinutes)}` : "–",
      `${status.icon} ${status.text}${day.date === report.today && day.minutes ? " so far" : ""}`
    ];

    cells.forEach((text, i) => {
      const cell = document.createElement(i === 0 ? "th" : "td");
      if (i === 0) cell.scope = "row";
      cell.textContent = text;
      if (i === 5 && status.good !== undefined) cell.className = status.good ? "good" : "bad";
      row.appendChild(cell);
    });

    rows.appendChild(row);
  });
}

let formFilled = false;

async function renderReport() {
  const report = await chrome.runtime.sendMessage({ type: "GET_REPORT" });
  if (!report) return;

  // Switched off: nothing but a way to switch it back on
  $("off-card").hidden = report.enabled;
  $("on-content").hidden = !report.enabled;
  if (!report.enabled) return report;

  // Filled once, so a later refresh never overwrites an edit in progress
  if (!formFilled) {
    fillForm(report.settings);
    formFilled = true;
  }

  const week = report.days.slice(-7);
  const lastWeek = report.days.slice(0, 7);

  renderTiles(report, week, lastWeek);
  renderChart(week, report);
  renderTable(week, report);

  return report;
}

function fillForm(settings) {
  for (const key of NUMBER_FIELDS) form.elements[key].value = settings[key];
  form.elements.brainRot.value = settings.brainRot.join("\n");
}

function readForm() {
  const settings = {};
  for (const key of NUMBER_FIELDS) settings[key] = form.elements[key].value;
  settings.brainRot = form.elements.brainRot.value.split(/[\n,]+/).map(s => s.trim()).filter(Boolean);
  return settings;
}

// What the worker changed while cleaning the values, in words
function describeAdjustments(sent, kept) {
  const changed = NUMBER_FIELDS.filter(key => Number(sent[key]) !== kept[key]);
  const dropped = sent.brainRot.length - kept.brainRot.length;
  const notes = [];

  if (changed.length) notes.push("some numbers were moved into range or into order");
  if (dropped > 0) {
    notes.push(`${dropped === 1 ? "1 site was" : `${dropped} sites were`} left out as duplicates or not web addresses`);
  }

  return notes.length ? `Saved, but ${notes.join(", and ")}.` : "Saved.";
}

form.addEventListener("submit", async event => {
  event.preventDefault();

  const sent = readForm();
  const result = await chrome.runtime.sendMessage({ type: "SAVE_SETTINGS", settings: sent });
  if (!result?.success) {
    saved.textContent = "Could not save. Reload the page and try again.";
    return;
  }

  fillForm(result.settings);
  saved.textContent = describeAdjustments(sent, result.settings);
});

$("reset").addEventListener("click", async () => {
  const result = await chrome.runtime.sendMessage({ type: "RESET_SETTINGS" });
  if (result?.success) {
    fillForm(result.settings);
    saved.textContent = "Back to the defaults.";
  }
});

$("turn-on").addEventListener("click", async () => {
  $("turn-on").disabled = true;
  await chrome.runtime.sendMessage({ type: "SET_ENABLED", value: true });
  $("turn-on").disabled = false;
  await renderReport();
});

// Kills, minutes, focus sessions and the switch show up while the page is
// open.
let refreshTimer = null;
chrome.storage.onChanged.addListener((changes, area) => {
  if (area !== "local") return;
  if (!["history", "xp", "streak", "settings", "enabled"].some(key => key in changes)) return;

  clearTimeout(refreshTimer);
  refreshTimer = setTimeout(renderReport, 300);
});

renderReport();
