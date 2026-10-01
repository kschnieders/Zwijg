"use strict";

// Vorlagen für neue Verbindungen. Alles außer Claude und Echo spricht die OpenAI kompatible API.
const PRESETS = {
  claude: {
    label: "Claude", sub: "Anthropic", type: "Anthropic", color: "#c96442", letter: "C",
    model: "claude-opus-5-5", cloud: true,
    note: "Braucht einen API Schlüssel von platform.claude.com. Die API wird getrennt vom Claude Abo abgerechnet."
  },
  openai: {
    label: "ChatGPT", sub: "OpenAI", url: "https://api.openai.com/v1", color: "#10a37f", letter: "G", cloud: true,
    note: "API Schlüssel von platform.openai.com. Das Modell am besten über \"Modelle laden\" auswählen."
  },
  mistral: {
    label: "Mistral", sub: "Europäischer Anbieter", url: "https://api.mistral.ai/v1", color: "#fa520f", letter: "M",
    model: "mistral-small-latest", cloud: true,
    note: "Anbieter aus Frankreich. Im kostenlosen Tarif können Eingaben zum Training genutzt werden."
  },
  gemini: {
    label: "Gemini", sub: "Google", url: "https://generativelanguage.googleapis.com/v1beta/openai", color: "#4285f4",
    letter: "G", cloud: true, warn: true,
    note: "Es gibt einen kostenlosen Tarif, dort darf Google die Eingaben aber zum Training nutzen. Nur mit Testdaten verwenden."
  },
  groq: {
    label: "Groq", sub: "Sehr schnelle offene Modelle", url: "https://api.groq.com/openai/v1", color: "#f55036",
    letter: "Q", cloud: true, warn: true,
    note: "Kostenloser Tarif verfügbar. Anbieter sitzt in den USA, daher nur für unkritische Anfragen."
  },
  ollama: {
    label: "Ollama", sub: "Lokal oder auf eigenem Server", url: "http://localhost:11434/v1", color: "#3b3b38",
    letter: "O", model: "qwen2.5:7b", local: true,
    note: "Kostenlos und lokal. Für einen Server im Praxisnetz die Adresse anpassen, z.B. http://192.168.1.50:11434/v1"
  },
  lmstudio: {
    label: "LM Studio", sub: "Lokale Modelle mit Oberfläche", url: "http://localhost:1234/v1", color: "#6b4de6",
    letter: "L", local: true,
    note: "In LM Studio unter Developer den lokalen Server starten."
  },
  custom: {
    label: "Eigener Server", sub: "vLLM, llama.cpp, LocalAI ...", url: "", color: "#5a6b66", letter: "S", local: true,
    note: "Jeder Server mit OpenAI kompatibler API. Die Adresse endet meist auf /v1."
  },
  echo: {
    label: "Echo", sub: "Test ohne KI", type: "Echo", color: "#9a9890", letter: "E", model: "echo", local: true,
    note: "Gibt nur die Eingabe zurück. Gut zum Ausprobieren der Pseudonymisierung."
  },
};

const ACTIONS = { chat: "Chat", document: "Dokument", protect: "Text schützen", admin: "Verwaltung", login: "Anmeldung" };
const SENSITIVITY = { None: "keine", Low: "niedrig", Medium: "mittel", High: "hoch" };
const MODES = { Auto: "Automatisch", LocalOnly: "Nur lokal", CloudOnly: "Nur Cloud" };
const LEVELS = { Info: "Info", Warning: "Hinweis", Critical: "Wichtig" };
const AUDIENCES = { All: "Alle", Staff: "Nur Mitarbeiter", Admins: "Nur Admins", Selected: "Bestimmte Personen" };
const NL = String.fromCharCode(10);

const state = {
  key: "", me: null, meJson: "", settings: null, history: [], editing: null, preset: null,
  previewTimer: null, userFilter: "all", userStats: {}, editingUser: null, editingNotice: null, noticeLevel: "Info",
  conversationId: null, convs: [], convTotal: 0, convShowAll: false, secrets: []
};

// Hilfsfunktionen

const $ = id => document.getElementById(id);

function esc(s) {
  return String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

function icon(name, cls = "ico") {
  return `<svg class="${cls}"><use href="#i-${name}"/></svg>`;
}

function chip(text, kind = "", ico = "") {
  return `<span class="chip ${kind}">${ico ? icon(ico) : ""}${esc(text)}</span>`;
}

function toast(text, error = false) {
  const t = document.createElement("div");
  t.className = "toast" + (error ? " error" : "");
  t.innerHTML = icon(error ? "alert" : "check") + `<span>${esc(text)}</span>`;
  $("toasts").appendChild(t);
  setTimeout(() => t.remove(), error ? 6000 : 3000);
}

function confirmDialog(title, text, yes = "Ja, weiter") {
  $("confirmTitle").textContent = title;
  $("confirmText").textContent = text;
  $("confirmYes").textContent = yes;
  const d = $("confirmDialog");
  d.returnValue = "";
  d.showModal();
  return new Promise(resolve => d.addEventListener("close", () => resolve(d.returnValue === "yes"), { once: true }));
}

function storage(action, value) {
  try {
    if (action === "get") return localStorage.getItem("zwijg.key") || "";
    if (action === "set") localStorage.setItem("zwijg.key", value);
    if (action === "del") localStorage.removeItem("zwijg.key");
  } catch { /* privater Modus, dann eben ohne merken */ }
  return "";
}

function relTime(iso) {
  if (!iso) return "noch nie";
  const d = new Date(iso);
  const min = Math.round((Date.now() - d) / 60000);
  if (min < 1) return "gerade eben";
  if (min < 60) return `vor ${min} Min.`;
  const h = Math.round(min / 60);
  if (h < 24) return `vor ${h} Std.`;
  const days = Math.round(h / 24);
  if (days === 1) return "gestern";
  if (days < 7) return `vor ${days} Tagen`;
  return d.toLocaleDateString("de-DE");
}

function fmtDateTime(iso) {
  return iso ? new Date(iso).toLocaleString("de-DE", { dateStyle: "short", timeStyle: "short" }) : "";
}

function initials(name) {
  return name.split(/[\s.]+/).filter(Boolean).slice(0, 2).map(w => w[0].toUpperCase()).join("") || "?";
}

function lines(id) {
  return $(id).value.split(NL).map(s => s.trim()).filter(Boolean);
}

// datetime-local arbeitet ohne Zeitzone, der Server mit UTC
function toLocalInput(iso) {
  if (!iso) return "";
  const d = new Date(iso);
  return new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
}

function fromLocalInput(value) {
  return value ? new Date(value).toISOString() : null;
}

// API

// Jede Anfrage trägt unsere Kennung. Mit Sitzung verlangt der Server sie bei ändernden Anfragen,
// fremde Webseiten können sie nicht setzen. Der Schlüssel wird nur mitgeschickt, wenn damit angemeldet wurde.
function authHeaders(extra = {}) {
  const h = { "X-Requested-With": "zwijg", ...extra };
  if (state.key) h["Authorization"] = "Bearer " + state.key;
  return h;
}

async function api(method, path, body, { raw = false, headers = {} } = {}) {
  const init = { method, headers: authHeaders(headers) };
  if (body instanceof FormData) {
    init.body = body;
  } else if (body !== undefined) {
    init.headers["Content-Type"] = "application/json";
    init.body = JSON.stringify(body);
  }

  const res = await fetch(path, init);
  if (res.status === 401 && path !== "/v1/me") {
    logout("Sitzung abgelaufen, bitte neu anmelden.");
    throw new Error("Nicht angemeldet");
  }
  if (raw) return res;

  const data = await res.json().catch(() => ({}));
  if (res.status === 403 && data.locked) {
    logout(data.error);
    throw new Error(data.error);
  }
  if (res.status === 403 && data.mustChangePassword) {
    openPasswordDialog(true);
    throw new Error(data.error);
  }
  if (!res.ok) {
    const err = new Error(data.error || `Fehler ${res.status}`);
    err.data = data;
    throw err;
  }
  return data;
}

// Anmeldung: Benutzername und Passwort, oder als Notzugang der Zugangsschlüssel

let loginMode = "password";

function setLoginMode(mode) {
  loginMode = mode;
  $("loginPasswordFields").hidden = mode !== "password";
  $("loginKeyFields").hidden = mode !== "key";
  $("loginModeToggle").textContent = mode === "password"
    ? "Stattdessen mit Zugangsschlüssel anmelden"
    : "Mit Benutzername und Passwort anmelden";
  $("loginError").hidden = true;
  (mode === "password" ? $("loginUser") : $("loginKey")).focus();
}

$("loginModeToggle").addEventListener("click", () => setLoginMode(loginMode === "password" ? "key" : "password"));

async function enterApp(me) {
  state.me = me;
  state.meJson = JSON.stringify(me);

  $("login").hidden = true;
  $("app").hidden = false;
  applyMe();
  await refreshStatus().catch(() => {});
  show(location.hash.slice(1) || (me.admin ? "dashboard" : "chat"));
  emptyChat();
  preview();

  if (me.mustChangePassword) openPasswordDialog(true);
}

async function loginWithKey(key) {
  state.key = key.trim();
  const res = await fetch("/v1/me", { headers: authHeaders() });
  if (!res.ok) {
    const data = await res.json().catch(() => ({}));
    state.key = "";
    return data.error || "Dieser Schlüssel ist nicht gültig.";
  }
  storage("set", state.key);
  await enterApp(await res.json());
  return null;
}

async function loginWithPassword(username, password, remember) {
  const res = await fetch("/auth/login", {
    method: "POST",
    headers: { "Content-Type": "application/json", "X-Requested-With": "zwijg" },
    body: JSON.stringify({ username, password, remember }),
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) return data.error || "Anmeldung fehlgeschlagen";

  state.key = "";
  storage("del");
  const me = await fetch("/v1/me", { headers: authHeaders() });
  if (!me.ok) return "Anmeldung fehlgeschlagen";
  await enterApp(await me.json());
  return null;
}

// Beim Öffnen: gespeicherter Schlüssel oder noch laufende Sitzung
async function resume() {
  const saved = storage("get");
  if (saved && !await loginWithKey(saved)) return;

  const res = await fetch("/v1/me", { headers: authHeaders() }).catch(() => null);
  if (res?.ok) {
    await enterApp(await res.json());
    return;
  }
  showLogin();
}

function showLogin(message) {
  $("app").hidden = true;
  $("login").hidden = false;
  $("loginError").hidden = !message;
  $("loginError").textContent = message || "";
  $("loginPassword").value = "";
  $("loginKey").value = "";
  setLoginMode(loginMode);
}

function logout(message) {
  // Sitzung beim Server beenden, Fehler dabei sind egal
  fetch("/auth/logout", { method: "POST", headers: { "X-Requested-With": "zwijg" } }).catch(() => {});
  storage("del");
  state.key = "";
  state.me = null;
  state.meJson = "";
  state.history = [];
  state.conversationId = null;
  state.secrets = [];
  renderSecrets();
  resetProtect();
  state.convs = [];
  state.convLoaded = false;
  $("convList").innerHTML = "";
  emptyChat();
  showLogin(message);
}

$("loginForm").addEventListener("submit", async e => {
  e.preventDefault();
  const button = $("loginForm").querySelector("button[type=submit]");
  button.disabled = true;
  try {
    const error = loginMode === "password"
      ? await loginWithPassword($("loginUser").value.trim(), $("loginPassword").value, $("loginRemember").checked)
      : await loginWithKey($("loginKey").value);
    if (error) {
      $("loginError").textContent = error;
      $("loginError").hidden = false;
      if (loginMode === "password") $("loginPassword").select();
    }
  } finally {
    button.disabled = false;
  }
});

$("logout").addEventListener("click", e => {
  e.stopPropagation();
  logout();
});

// Konto Menü unten links
$("whoBox").addEventListener("click", e => {
  if (e.target.closest("#logout")) return;
  e.stopPropagation();
  openMenu($("whoBox"), [
    { icon: "key", label: state.me?.hasPassword ? "Passwort ändern" : "Passwort festlegen", run: () => openPasswordDialog(false) },
    { icon: "info", label: "Über Zwijg", run: openAbout },
    "-",
    { icon: "sun", label: "Hell", active: themeChoice() === "light", run: () => setTheme("light") },
    { icon: "moon", label: "Dunkel", active: themeChoice() === "dark", run: () => setTheme("dark") },
    { icon: "monitor", label: "Wie das System", active: themeChoice() === "system", run: () => setTheme("system") },
    "-",
    { icon: "logout", label: "Abmelden", run: () => logout() },
  ]);
});

// Darstellung: hell, dunkel oder wie das System. Gilt pro Browser.
function themeChoice() {
  try { return localStorage.getItem("zwijg.theme") || "system"; } catch { return "system"; }
}

const systemDark = matchMedia("(prefers-color-scheme: dark)");

function applyTheme() {
  const choice = themeChoice();
  const dark = choice === "dark" || (choice === "system" && systemDark.matches);
  document.documentElement.dataset.theme = dark ? "dark" : "light";
}

function setTheme(choice) {
  try {
    if (choice === "system") localStorage.removeItem("zwijg.theme");
    else localStorage.setItem("zwijg.theme", choice);
  } catch { }
  applyTheme();
}

systemDark.addEventListener("change", applyTheme);

// Version und Updates, nur für Admins. Der Server fragt GitHub, der Browser nie direkt.
let versionInfo = null;

const UPDATE_STEPS = {
  windows: {
    title: "Windows Paket",
    steps: [
      ["Im Ordner von Zwijg PowerShell öffnen und ausführen. Das Skript beendet Zwijg, sichert den data Ordner und spielt die neue Version ein:", "powershell -ExecutionPolicy Bypass -File update.ps1"],
      ["Danach Zwijg wieder starten:", ".\\Zwijg.Gateway.exe"],
    ],
  },
  linux: {
    title: "Linux Paket",
    steps: [
      ["Zwijg beenden, zum Beispiel wenn es als Dienst läuft:", "sudo systemctl stop zwijg"],
      ["Im Ordner von Zwijg ausführen. Das Skript sichert den data Ordner und spielt die neue Version ein:", "./update.sh"],
      ["Danach wieder starten:", "sudo systemctl start zwijg"],
    ],
  },
  docker: {
    title: "Docker",
    steps: [
      ["Im Ordner mit der docker-compose.yml ausführen. Die Daten liegen im Volume und bleiben erhalten:", "docker compose pull && docker compose up -d"],
    ],
  },
  source: {
    title: "Quellcode",
    steps: [
      ["Im Ordner des Repositorys den neuesten Stand holen:", "git pull"],
      ["Zwijg neu starten:", "dotnet run --project src/Zwijg.Gateway"],
    ],
  },
};

async function loadVersion(refresh = false) {
  if (!state.me?.admin) return;
  try {
    versionInfo = await api("GET", "/admin/version" + (refresh ? "?refresh=true" : ""));
  } catch {
    versionInfo = null;
  }
  renderVersionRow();
  if ($("updateDialog").open) renderUpdateDialog();
}

function renderVersionRow() {
  const el = $("versionStatus");
  if (!el) return;
  const v = versionInfo;
  if (!v) { el.innerHTML = ""; return; }
  const badge = v.updateAvailable ? chip(`Update auf ${v.latest}`, "info", "download")
    : v.error ? chip("nicht geprüft", "", "alert")
    : !v.enabled && !v.checkedAt ? chip("Prüfung aus")
    : chip("aktuell", "ok", "check");
  el.innerHTML = `<strong>${esc(v.current)}</strong>${badge}`;
}

function renderUpdateDialog() {
  const v = versionInfo;
  if (!v) return;

  $("updVerdict").innerHTML = v.updateAvailable
    ? `<div class="doc-verdict warn">${icon("download")}<div><strong>Version ${esc(v.latest)} ist verfügbar</strong><span>Installiert ist ${esc(v.current)}. Einstellungen, Protokoll und Verlauf bleiben beim Update erhalten.</span></div></div>`
    : v.error
      ? `<div class="doc-verdict">${icon("alert")}<div><strong>Konnte nicht prüfen</strong><span>${esc(v.error)}</span></div></div>`
      : v.checkedAt
        ? `<div class="doc-verdict ok">${icon("check")}<div><strong>Zwijg ist aktuell</strong><span>${v.latest ? `Neueste Version ist ${esc(v.latest)}.` : "Es gibt noch keine veröffentlichte Version."}</span></div></div>`
        : `<div class="doc-verdict">${icon("info")}<div><strong>Noch nicht geprüft</strong><span>Die automatische Prüfung ist aus. Mit "Jetzt prüfen" fragt Zwijg einmal bei GitHub nach.</span></div></div>`;

  const fact = (k, val) => val ? `<dt>${k}</dt><dd>${val}</dd>` : "";
  $("updFacts").innerHTML =
    fact("Installiert", esc(v.current)) +
    fact("Neueste", v.latest ? esc(v.latest) + (v.publishedAt ? ` <span class="muted small">vom ${fmtDateTime(v.publishedAt)}</span>` : "") : "") +
    fact("Geprüft", v.checkedAt ? fmtDateTime(v.checkedAt) : "") +
    fact("Quelle", `<a href="https://github.com/${esc(v.repository)}/releases" target="_blank" rel="noopener">github.com/${esc(v.repository)}</a>`);

  $("updNotesBox").hidden = !(v.updateAvailable && v.notes);
  $("updNotes").textContent = v.notes || "";

  // Befehle passend zur Installation zuerst, die anderen aufklappbar
  const block = key => {
    const info = UPDATE_STEPS[key];
    return info.steps.map(([text, cmd]) => `<p class="small">${esc(text)}</p>
      <div class="key-box"><code>${esc(cmd)}</code><button class="icon-btn" type="button" data-copy-cmd="${esc(cmd)}" title="Kopieren" aria-label="Kopieren">${icon("copy")}</button></div>`).join("");
  };
  const own = UPDATE_STEPS[v.installType] ? v.installType : "source";
  const others = Object.keys(UPDATE_STEPS).filter(k => k !== own);
  $("updHowto").innerHTML = v.updateAvailable || v.installType
    ? `<h3 class="about-sub">So aktualisierst du (${esc(UPDATE_STEPS[own].title)})</h3>${block(own)}
       <details class="upd-other"><summary>Andere Installationsarten</summary>${others.map(k => `<h4>${esc(UPDATE_STEPS[k].title)}</h4>${block(k)}`).join("")}</details>`
    : "";

  $("updCheck").checked = v.enabled;
}

async function openUpdateDialog() {
  if (!versionInfo) await loadVersion();
  renderUpdateDialog();
  $("updateDialog").showModal();
}

$("updRefresh").addEventListener("click", async () => {
  $("updRefresh").disabled = true;
  await loadVersion(true);
  $("updRefresh").disabled = false;
  toast("Version geprüft");
});

$("updCheck").addEventListener("change", async e => {
  try {
    await api("PUT", "/admin/version/check", { enabled: e.target.checked });
    await loadVersion();
  } catch (err) {
    e.target.checked = !e.target.checked;
    toast(err.message, true);
  }
});

$("dashStatus").addEventListener("click", e => { if (e.target.closest("#versionRow")) openUpdateDialog(); });
$("dashStatus").addEventListener("keydown", e => { if (e.key === "Enter" && e.target.closest("#versionRow")) openUpdateDialog(); });

$("updHowto").addEventListener("click", async e => {
  const b = e.target.closest("[data-copy-cmd]");
  if (!b) return;
  await navigator.clipboard.writeText(b.dataset.copyCmd);
  toast("Befehl kopiert");
});

// Text schützen: die Zuordnung Platzhalter zu echtem Wert lebt nur hier im Speicher, nie im Browserspeicher
const protect = { mapping: [], protectedText: "", restoredText: "" };

const PLACEHOLDER = /\[\s*([A-Z]+)_(\d+)\s*\]/g;

async function runProtect() {
  const text = $("protectInput").value;
  if (!text.trim()) return;
  $("protectRun").disabled = true;
  $("protectStatus").textContent = "wird geschützt ...";
  try {
    const r = await api("POST", "/v1/protect", { text, known: protect.mapping });
    protect.mapping = r.mapping;
    protect.protectedText = r.protected;
    $("protectOutput").innerHTML = esc(r.protected).replace(/\[[A-Z]+_\d+\]/g, m => `<mark>${m}</mark>`);
    $("protectCopy").disabled = false;

    const chips = Object.entries(r.entities).map(([k, v]) => chip(`${k} × ${v}`));
    if (!chips.length) chips.push(chip("keine Personendaten erkannt", "ok", "check"));
    if (r.healthTerms?.length) chips.push(chip("Gesundheitsdaten", "amber", "steth"));
    $("protectChips").innerHTML = chips.join("");
    $("protectStatus").textContent = "";
    renderProtectMap();
    restoreAnswer();
  } catch (err) {
    $("protectStatus").textContent = "";
    $("protectOutput").innerHTML = `<div class="doc-verdict bad">${icon("alert")}<div><strong>Nicht geschützt</strong><span>${esc(err.message)}</span></div></div>`;
    $("protectCopy").disabled = true;
    $("protectChips").innerHTML = "";
    protect.protectedText = "";
  } finally {
    $("protectRun").disabled = false;
  }
}

// Platzhalter in der Antwort durch die echten Werte ersetzen, tolerant bei Leerzeichen wie "[ NAME_1 ]"
function restoreAnswer() {
  const text = $("restoreInput").value;
  if (!text.trim()) {
    $("restoreOutput").innerHTML = '<span class="muted">Hier erscheint die Antwort mit den echten Daten.</span>';
    $("restoreCopy").disabled = true;
    $("restoreStatus").textContent = "";
    protect.restoredText = "";
    return;
  }

  const byPlaceholder = new Map(protect.mapping.map(e => [e.placeholder, e.value]));
  let unknown = 0;
  protect.restoredText = text.replace(PLACEHOLDER, (m, label, n) => byPlaceholder.get(`[${label}_${n}]`) ?? m);
  $("restoreOutput").innerHTML = esc(text).replace(PLACEHOLDER, (m, label, n) => {
    const value = byPlaceholder.get(`[${label}_${n}]`);
    if (value === undefined) { unknown++; return `<mark class="unknown" title="Unbekannter Platzhalter">${m}</mark>`; }
    return `<span class="restored" title="${esc(m)}">${esc(value)}</span>`;
  });
  $("restoreStatus").textContent = unknown ? `${unknown} Platzhalter unbekannt. Stammt die Antwort aus dieser Sitzung?` : "";
  $("restoreCopy").disabled = false;
}

function renderProtectMap() {
  $("protectMapBox").hidden = protect.mapping.length === 0;
  $("protectMapCount").textContent = protect.mapping.length;
  $("protectMap").innerHTML = protect.mapping.map(e =>
    `<div class="protect-map-row"><code>${esc(e.placeholder)}</code><span>${esc(e.value)}</span></div>`).join("");
}

function resetProtect() {
  protect.mapping = [];
  protect.protectedText = "";
  protect.restoredText = "";
  $("protectInput").value = "";
  $("restoreInput").value = "";
  $("protectOutput").innerHTML = '<span class="muted">Hier erscheint der Text mit Platzhaltern.</span>';
  $("protectChips").innerHTML = "";
  $("protectCopy").disabled = true;
  renderProtectMap();
  restoreAnswer();
}

async function copyText(text, label) {
  try {
    await navigator.clipboard.writeText(text);
    toast(label);
  } catch {
    toast("Kopieren ging nicht, bitte von Hand markieren", true);
  }
}

$("protectRun").addEventListener("click", runProtect);
$("protectInput").addEventListener("keydown", e => { if (e.ctrlKey && e.key === "Enter") runProtect(); });
$("restoreInput").addEventListener("input", restoreAnswer);
$("protectCopy").addEventListener("click", () => copyText(protect.protectedText, "Geschützte Fassung kopiert"));
$("restoreCopy").addEventListener("click", () => copyText(protect.restoredText, "Antwort kopiert"));
$("protectReset").addEventListener("click", resetProtect);

// Über Zwijg: Ersteller, Quellcode und Lizenzen. Geht auch ohne Anmeldung.
async function openAbout() {
  $("aboutDialog").showModal();
  const h = await fetch("/health").then(r => r.json()).catch(() => null);
  $("aboutVersion").textContent = h?.version ? "Version " + h.version : "";
}

$("loginAbout").addEventListener("click", openAbout);

// Passwort ändern. Mit Startpasswort ist der Dialog Pflicht und lässt sich nicht schließen.
let passwordForced = false;

function openPasswordDialog(forced) {
  if ($("passwordDialog").open) return;
  passwordForced = forced;
  const needsCurrent = state.me?.hasPassword && !state.me?.mustChangePassword;

  $("pwTitle").textContent = forced ? "Eigenes Passwort festlegen" : (state.me?.hasPassword ? "Passwort ändern" : "Passwort festlegen");
  $("pwIntro").textContent = forced
    ? "Du hast dich mit einem Startpasswort angemeldet. Bitte leg jetzt ein eigenes fest, das nur du kennst. Mindestens 10 Zeichen, ein Satz oder mehrere Wörter sind gut zu merken."
    : "Mindestens 10 Zeichen. Ein Satz oder mehrere Wörter sind leichter zu merken und trotzdem sicher.";
  $("pwCurrentField").hidden = !needsCurrent;
  $("pwClose").hidden = forced;
  $("pwCancel").hidden = forced;
  $("pwUser").value = state.me?.username || "";
  ["pwCurrent", "pwNew", "pwRepeat"].forEach(id => $(id).value = "");
  $("pwError").hidden = true;
  updateMeter();
  $("passwordDialog").showModal();
  (needsCurrent ? $("pwCurrent") : $("pwNew")).focus();
}

// Esc darf den Pflicht Dialog nicht schließen
$("passwordDialog").addEventListener("cancel", e => { if (passwordForced) e.preventDefault(); });

function updateMeter() {
  const p = $("pwNew").value;
  const variety = [/[a-z]/, /[A-Z]/, /\d/, /[^A-Za-z0-9]/].filter(r => r.test(p)).length;
  const score = Math.min(100, p.length * 5 + variety * 8);
  const bar = $("pwMeter").querySelector("i");
  bar.style.width = (p ? Math.max(8, score) : 0) + "%";
  bar.style.background = p.length < 10 ? "var(--crit)" : score < 75 ? "var(--warn)" : "var(--ok)";
}

$("pwNew").addEventListener("input", updateMeter);

$("passwordForm").addEventListener("submit", async e => {
  if (e.submitter?.value === "cancel") return;
  e.preventDefault();

  const fail = msg => { $("pwError").textContent = msg; $("pwError").hidden = false; };
  if ($("pwNew").value !== $("pwRepeat").value) return fail("Die beiden neuen Passwörter stimmen nicht überein.");
  if ($("pwNew").value.length < 10) return fail("Das Passwort braucht mindestens 10 Zeichen.");

  try {
    await api("POST", "/v1/account/password", { current: $("pwCurrent").value || null, new: $("pwNew").value });
    $("passwordDialog").close();
    toast("Passwort gespeichert");
    const wasForced = passwordForced;
    passwordForced = false;
    await reloadMe();
    if (wasForced) show(location.hash.slice(1) || (state.me.admin ? "dashboard" : "chat"));
  } catch (err) {
    fail(err.message);
  }
});

// Was der Benutzer darf und sieht. Wird regelmäßig neu geladen, damit neue Hinweise ankommen.
function applyMe() {
  const me = state.me;
  $("userName").textContent = me.name;
  $("userRole").textContent = me.admin ? "Admin" : "Mitarbeiter";
  $("avatar").textContent = initials(me.name);
  document.querySelectorAll(".admin-only").forEach(el => el.hidden = !me.admin);

  $("navDoc").hidden = !me.canUseDocuments;
  // Ein kopierter Text verlässt die Praxis, das gibt es nur für Leute, die in die Cloud dürfen
  $("navProtect").hidden = !me.cloudAllowed;
  $("previewCard").hidden = !me.showPreview;
  $("chatLayout").classList.toggle("solo", !me.showPreview);
  $("routeCloud").hidden = !me.cloudAllowed;
  if (!me.cloudAllowed && $("route").value === "cloud") $("route").value = "";

  renderUsage();
  renderTemplateBar();
  applyHistory();
  renderBanners();
}

function renderTemplateBar() {
  const list = state.me.templates || [];
  $("templateBar").hidden = !list.length;
  $("templateBar").innerHTML = list.map(t =>
    `<button type="button" class="${t.mode === "Run" ? "run" : ""}" data-template="${t.id}" title="${t.mode === "Run" ? "Wird direkt ausgeführt" : "Wird ins Eingabefeld eingefügt"}">${icon(t.mode === "Run" ? "send" : "doc")}${esc(t.title)}</button>`).join("");
}

function renderUsage() {
  const me = state.me;
  $("usage").hidden = !me.dailyLimit;
  if (me.dailyLimit) {
    $("usage").textContent = `${me.usedToday} von ${me.dailyLimit} Anfragen heute`;
    $("usage").classList.toggle("full", me.usedToday >= me.dailyLimit);
  }
}

async function reloadMe() {
  if (!state.key) return;
  try {
    const me = await api("GET", "/v1/me");
    const json = JSON.stringify(me);
    if (json !== state.meJson) {
      state.me = me;
      state.meJson = json;
      applyMe();
    }
  } catch { /* offline, beim nächsten Mal */ }
}

setInterval(reloadMe, 60000);

function bannerHtml(a, dismiss = true) {
  const ico = a.level === "Info" ? "info" : "alert";
  return `<div class="banner ${a.level}" data-id="${a.id || ""}">
    ${icon(ico)}
    <div class="banner-body">
      ${a.title ? `<div class="banner-title">${esc(a.title)}</div>` : ""}
      ${a.message ? `<div class="banner-text">${esc(a.message)}</div>` : ""}
    </div>
    ${a.dismissible && dismiss ? `<button type="button" class="icon-btn" data-dismiss="${a.id}" title="Ausblenden" aria-label="Ausblenden">${icon("x")}</button>` : ""}
  </div>`;
}

function renderBanners() {
  $("banners").innerHTML = (state.me.announcements || []).map(a => bannerHtml(a)).join("");
}

$("banners").addEventListener("click", async e => {
  const btn = e.target.closest("[data-dismiss]");
  if (!btn) return;
  btn.closest(".banner").remove();
  try {
    await api("POST", `/v1/announcements/${btn.dataset.dismiss}/dismiss`);
    state.me.announcements = state.me.announcements.filter(a => a.id !== btn.dataset.dismiss);
  } catch (err) { toast(err.message, true); }
});

// Navigation

function show(view) {
  if (view === "doc" && !state.me.canUseDocuments) view = "chat";
  if (view === "protect" && !state.me.cloudAllowed) view = "chat";
  if (!document.getElementById("view-" + view) || (!state.me.admin && document.querySelector(`.nav button[data-view="${view}"]`)?.classList.contains("admin-only"))) view = "chat";
  history.replaceState(null, "", "#" + view);
  document.querySelectorAll(".nav button").forEach(b => b.classList.toggle("active", b.dataset.view === view));
  document.querySelectorAll(".view").forEach(v => v.hidden = v.id !== "view-" + view);

  const loaders = { dashboard: loadDashboard, connections: loadConnections, rules: loadRules, users: loadUsers, notices: loadNotices, audit: loadAudit };
  // Nur eigene Einträge aufrufen, nie etwas wie "constructor" aus der Adresszeile
  if (Object.hasOwn(loaders, view))
    loaders[view]().catch(err => toast(err.message, true));
}

document.querySelectorAll(".nav button").forEach(b => b.addEventListener("click", () => show(b.dataset.view)));

async function refreshStatus() {
  const h = await fetch("/health").then(r => r.json()).catch(() => null);
  let local = "", cloud = "";

  if (state.me?.admin) {
    state.settings = await api("GET", "/admin/settings");
    const byId = id => state.settings.connections.find(c => c.id === id)?.name;
    local = byId(state.settings.localConnectionId) || "";
    cloud = byId(state.settings.cloudConnectionId) || "";
    updateNoticeCount();
  }

  const line = (ico, label, on, name) =>
    `<div class="route-line">${icon(ico)}<span class="name">${esc(name || label)}</span><span class="dot ${on ? "on" : ""}" title="${on ? "bereit" : "nicht verfügbar"}"></span></div>`;

  $("routeStatus").innerHTML = h
    ? line("lock", "Lokal", h.local, local) + line("cloud", "Cloud", h.cloud, cloud)
    : line("alert", "Gateway nicht erreichbar", false, "");
}

function updateNoticeCount() {
  const now = Date.now();
  const active = (state.settings?.announcements || []).filter(a =>
    a.enabled && (!a.startsAt || new Date(a.startsAt) <= now) && (!a.endsAt || new Date(a.endsAt) > now)).length;
  $("noticeCount").hidden = !active;
  $("noticeCount").textContent = active;
}

// Chat

function emptyChat(text) {
  $("chat").innerHTML = `<div class="chat-empty">${icon("steth")}<p>${esc(text || "Stell eine Frage wie im normalen Chat. Namen, Geburtsdaten und Versichertennummern verlassen den Rechner nie im Klartext.")}</p></div>`;
}

function routeHeaders() {
  const r = $("route").value;
  return r ? { "X-Zwijg-Route": r } : {};
}

$("prompt").addEventListener("input", () => {
  clearTimeout(state.previewTimer);
  state.previewTimer = setTimeout(preview, 300);
});

$("prompt").addEventListener("keydown", e => {
  if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) { e.preventDefault(); send(); }
});

async function preview() {
  if (!state.me?.showPreview) return;
  const text = $("prompt").value;
  if (!text.trim()) {
    $("preview").innerHTML = '<span class="muted">Beim Tippen erscheint hier die geschützte Fassung.</span>';
    $("chips").innerHTML = "";
    return;
  }

  try {
    const r = await api("POST", "/v1/check", { text, secrets: state.secrets });
    $("preview").innerHTML = esc(r.pseudonymized).replace(/\[[A-Z]+_\d+\]/g, m => `<mark>${m}</mark>`);

    const chips = Object.entries(r.entities).map(([k, v]) => chip(`${k} × ${v}`));
    if (!chips.length) chips.push(chip("keine Personendaten erkannt", "ok", "check"));
    if (r.healthTerms?.length) {
      const shown = r.healthTerms.slice(0, 4).join(", ") + (r.healthTerms.length > 4 ? " ..." : "");
      chips.push(chip(`Gesundheitsdaten: ${shown}`, "amber", "steth"));
    }
    chips.push(chip(`Sensibilität ${SENSITIVITY[r.sensitivity] ?? r.sensitivity}`, r.sensitivity === "High" ? "amber" : ""));
    const local = r.route === "Local" || !state.me.cloudAllowed;
    chips.push(chip(local ? "bleibt lokal" : "darf in die Cloud", "ok", local ? "lock" : "cloud"));
    for (const rule of r.rules || []) {
      const a = { LocalOnly: ["bleibt lokal", "ok", "lock"], Block: ["wird blockiert", "warn", "x"], Warn: ["wird protokolliert", "amber", "list"] }[rule.action];
      if (a) chips.push(chip(`Regel "${rule.name}": ${a[0]}`, a[1], a[2]));
    }
    if (r.injection.score > 0)
      chips.push(chip(`Manipulationsverdacht (${r.injection.score}): ${r.injection.findings.map(f => f.rule).join(", ")}`, "warn", "alert"));
    $("chips").innerHTML = chips.join("");
  } catch (e) {
    $("preview").innerHTML = `<span class="muted">${esc(e.message)}</span>`;
  }
}

function addMessage(role, text, meta) {
  const chat = $("chat");
  chat.querySelector(".chat-empty")?.remove();
  const div = document.createElement("div");
  div.className = "msg " + role;
  div.textContent = text;
  if (role === "user") {
    div.dataset.text = text;
    markSecrets(div);
  }
  if (meta) {
    const m = document.createElement("div");
    m.className = "msg-meta";
    m.innerHTML = meta;
    div.appendChild(m);
  }
  chat.appendChild(div);
  chat.scrollTop = chat.scrollHeight;
  return div;
}

// Selbst markierte Geheimnisse: gelten für die ganze Unterhaltung, gehen nie an die KI.
// Der Server ersetzt sie überall durch Platzhalter und setzt sie in der Antwort wieder ein.
const SECRET_KINDS = { GEHEIM: "Geheim", NAME: "Person", FIRMA: "Firma", NUMMER: "Nummer", ORT: "Ort", DATEN: "Sonstiges" };
let secretLabel = "GEHEIM";

function selectedPromptText() {
  const p = $("prompt");
  return p.value.slice(p.selectionStart, p.selectionEnd).trim();
}

function isSecret(text) {
  return state.secrets.some(s => s.value.toLowerCase() === text.toLowerCase());
}

// Knopf "Verstecken" nur zeigen, wenn im Eingabefeld etwas Sinnvolles markiert ist
function updateHideButton() {
  const sel = document.activeElement === $("prompt") ? selectedPromptText() : "";
  $("hideSelection").hidden = !(sel.length >= 2 && sel.length <= 500 && !isSecret(sel));
}

for (const ev of ["select", "keyup", "mouseup", "input", "blur"])
  $("prompt").addEventListener(ev, () => setTimeout(updateHideButton, 0));

// Mit der Maus auf den Knopf geht die Markierung im Feld nicht verloren
$("hideSelection").addEventListener("mousedown", e => e.preventDefault());
$("hideSelection").addEventListener("click", () => openSecretDialog(selectedPromptText()));

$("prompt").addEventListener("keydown", e => {
  if (e.ctrlKey && e.shiftKey && e.key.toLowerCase() === "h") {
    e.preventDefault();
    const sel = selectedPromptText();
    if (sel.length >= 2) openSecretDialog(sel);
  }
});

function openSecretDialog(text) {
  if (text.length < 2) return;
  $("secretForm").dataset.value = text;
  $("secretValue").textContent = text;
  setSecretLabel("GEHEIM");
  $("secretDialog").showModal();
}

function setSecretLabel(label) {
  secretLabel = label;
  for (const b of $("secretKinds").querySelectorAll("button"))
    b.classList.toggle("active", b.dataset.label === label);
  const n = state.secrets.filter(s => s.label === label).length + 1;
  $("secretExample").textContent = `[${label}_${n}]`;
}

$("secretKinds").addEventListener("click", e => {
  const b = e.target.closest("button[data-label]");
  if (b) setSecretLabel(b.dataset.label);
});

$("secretForm").addEventListener("submit", e => {
  if (e.submitter?.value !== "save") return;
  const value = $("secretForm").dataset.value;
  if (!isSecret(value)) state.secrets.push({ value, label: secretLabel });
  renderSecrets();
  preview();
  toast("Wird in dieser Unterhaltung vor der KI versteckt");
  const p = $("prompt");
  p.focus();
  p.setSelectionRange(p.selectionEnd, p.selectionEnd);
  updateHideButton();
});

// Liste rechts in der Vorschau, und Markierungen in den eigenen Nachrichten auffrischen
function renderSecrets() {
  const list = $("secretList");
  list.hidden = state.secrets.length === 0;
  list.innerHTML = state.secrets.length === 0 ? "" :
    `<div class="secret-list-title">${icon("lock")}Versteckt in dieser Unterhaltung</div>` +
    state.secrets.map((s, i) => `<div class="secret-item">
        <span class="secret-item-value" title="${esc(s.value)}">${esc(s.value)}</span>
        <span class="secret-item-kind">${esc(SECRET_KINDS[s.label] || s.label)}</span>
        <button class="icon-btn" type="button" data-remove-secret="${i}" title="Nicht mehr verstecken" aria-label="Nicht mehr verstecken">${icon("x")}</button>
      </div>`).join("");

  for (const div of $("chat").querySelectorAll(".msg.user[data-text]"))
    markSecrets(div);
}

$("secretList").addEventListener("click", e => {
  const b = e.target.closest("[data-remove-secret]");
  if (!b) return;
  state.secrets.splice(Number(b.dataset.removeSecret), 1);
  renderSecrets();
  preview();
});

// Versteckte Stellen in einer eigenen Nachricht hervorheben, damit man sieht, was die KI nicht bekam
function markSecrets(div) {
  const text = div.dataset.text;
  if (!state.secrets.length) {
    div.textContent = text;
    return;
  }

  // Mit Klammer im Muster liefert split die Treffer an den ungeraden Stellen
  const pattern = new RegExp("(" + state.secrets
    .map(s => s.value).sort((a, b) => b.length - a.length)
    .map(v => v.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")).join("|") + ")", "gi");
  div.innerHTML = text.split(pattern)
    .map((part, i) => i % 2 ? `<span class="secret-mark" title="Vor der KI versteckt">${esc(part)}</span>` : esc(part))
    .join("");
}

async function send() {
  const text = $("prompt").value.trim();
  if (!text || $("send").disabled) return;

  $("prompt").value = "";
  autoGrow();
  preview();
  await sendContent(text, null);
}

// Schickt eine Nachricht. Bei direkt ausgeführten Vorlagen steht im Chat nur die Karte (display),
// die eigentliche Anweisung geht im Hintergrund an die KI.
async function sendContent(text, display) {
  if ($("send").disabled) return;
  $("send").disabled = true;

  const message = { role: "user", content: text };
  if (display) message.zwijg_display = display;
  state.history.push(message);
  if (display) addTemplateCard(display);
  else addMessage("user", text);

  const typing = addMessage("assistant", "");
  typing.innerHTML = '<span class="typing"><i></i><i></i><i></i></span>';

  try {
    const body = { model: "auto", messages: state.history };
    body.zwijg = { secrets: state.secrets };
    if (historyEnabled()) body.zwijg.conversation = state.conversationId || "new";
    const res = await api("POST", "/v1/chat/completions", body, { raw: true, headers: routeHeaders() });
    const data = await res.json().catch(() => ({}));
    typing.remove();

    if (!res.ok) {
      state.history.pop();
      if (res.status === 403 && data.locked) { logout(data.error); return; }
      const findings = (data.details?.findings || []).map(f => chip(`${f.rule}: ${f.snippet}`, "warn")).join(" ");
      addMessage("error", data.error || `Fehler ${res.status}`, findings);
      return;
    }

    const answer = data.choices?.[0]?.message?.content ?? "";
    state.history.push({ role: "assistant", content: answer });

    const saved = res.headers.get("X-Zwijg-Conversation");
    if (saved) {
      state.conversationId = saved;
      loadConversations();
    }

    const route = res.headers.get("X-Zwijg-Route");
    const conn = decodeURIComponent(res.headers.get("X-Zwijg-Connection") || "");
    const count = Number(res.headers.get("X-Zwijg-Pseudonyms") || 0);
    addMessage("assistant", answer,
      chip(route === "Local" ? "lokal" : "Cloud", "ok", route === "Local" ? "lock" : "cloud") +
      chip(conn || data.model || "") +
      (count > 0 ? chip(`${count} Werte geschützt`, "", "shield") : ""));
  } catch (e) {
    typing.remove();
    state.history.pop();
    addMessage("error", e.message);
  } finally {
    $("send").disabled = false;
    $("prompt").focus();
    reloadMe();
  }
}

$("send").addEventListener("click", send);

function newConversation() {
  state.history = [];
  state.conversationId = null;
  state.secrets = [];
  renderSecrets();
  emptyChat();
  renderConversations();
  $("prompt").value = "";
  autoGrow();
  preview();
  $("prompt").focus();
}

$("reset").addEventListener("click", newConversation);

// Eingabefeld wächst mit dem Text, erst ab der Maximalhöhe gibt es eine Scrollleiste
function autoGrow() {
  const t = $("prompt");
  t.style.height = "auto";
  const max = 260;
  t.style.height = Math.min(t.scrollHeight + 2, max) + "px";
  t.classList.toggle("scroll", t.scrollHeight > max);
}

$("prompt").addEventListener("input", autoGrow);

// Dokument: erst prüfen, dann fragen

const doc = { file: null, check: null };

const DOC_QUESTIONS = [
  "Fasse das Dokument in drei Sätzen zusammen.",
  "Welche Diagnosen und Befunde werden genannt?",
  "Welche Medikamente mit Dosierung werden genannt?",
  "Was sind die empfohlenen nächsten Schritte?",
  "Erkläre das Dokument in einfacher Sprache für den Patienten.",
];

$("docQuickQuestions").innerHTML = DOC_QUESTIONS.map((q, i) => `<button type="button" data-q="${i}">${esc(q)}</button>`).join("");
$("docQuickQuestions").addEventListener("click", e => {
  const b = e.target.closest("[data-q]");
  if (!b) return;
  $("docQuestion").value = DOC_QUESTIONS[Number(b.dataset.q)];
  askDocument();
});

function setFile(file) {
  doc.file = file;
  $("dropText").textContent = `${file.name} (${Math.max(1, Math.round(file.size / 1024))} KB)`;
  checkDocument();
}

$("docFile").addEventListener("change", () => { if ($("docFile").files[0]) setFile($("docFile").files[0]); });
["dragover", "dragenter"].forEach(ev => $("drop").addEventListener(ev, e => { e.preventDefault(); $("drop").classList.add("over"); }));
["dragleave", "drop"].forEach(ev => $("drop").addEventListener(ev, () => $("drop").classList.remove("over")));
$("drop").addEventListener("drop", e => { e.preventDefault(); if (e.dataTransfer.files[0]) setFile(e.dataTransfer.files[0]); });

function resetDocument() {
  doc.file = null;
  doc.check = null;
  $("docFile").value = "";
  $("dropText").textContent = "Datei hierher ziehen oder klicken";
  ["docReport", "docAsk", "docPreviewCard", "docReset"].forEach(id => $(id).hidden = true);
  $("docLayout").classList.remove("checked");
  $("docStatus").innerHTML = "";
  $("docResult").innerHTML = "";
}

$("docReset").addEventListener("click", resetDocument);

async function checkDocument() {
  const form = new FormData();
  form.append("file", doc.file);
  ["docReport", "docAsk", "docPreviewCard"].forEach(id => $(id).hidden = true);
  $("docResult").innerHTML = "";
  $("docStatus").innerHTML = `<div class="doc-busy"><span class="typing"><i></i><i></i><i></i></span><span id="docBusyText">Dokument wird geprüft...</span></div>`;
  const slow = setTimeout(() => {
    if ($("docBusyText")) $("docBusyText").textContent = "Wird geprüft. Bei Scans läuft die Texterkennung, das dauert pro Seite ein paar Sekunden.";
  }, 2500);

  let r;
  try {
    r = await api("POST", "/v1/documents/check", form);
  } catch (e) {
    clearTimeout(slow);
    const scanned = e.data?.scanned;
    $("docStatus").innerHTML = `<div class="doc-verdict ${scanned ? "warn" : "bad"}">${icon(scanned ? "alert" : "x")}
      <div><strong>${scanned ? "Eingescanntes Dokument" : "Konnte nicht geprüft werden"}</strong><div class="muted small">${esc(e.message)}</div></div></div>`;
    $("docReset").hidden = false;
    return;
  }

  clearTimeout(slow);
  doc.check = r;
  $("docStatus").innerHTML = "";
  $("docReset").hidden = false;
  $("docLayout").classList.add("checked");
  renderDocumentCheck(r);
}

function renderDocumentCheck(r) {
  // Urteil oben: blockiert, auffällig oder in Ordnung
  let verdict;
  if (r.blocked) {
    verdict = `<div class="doc-verdict bad">${icon("alert")}<div><strong>Wird blockiert</strong>
      <div class="muted small">${esc(r.blockedReason)}. Das Dokument geht nicht an die KI.</div></div></div>`;
  } else if (r.hiddenText || r.invisibleChars) {
    verdict = `<div class="doc-verdict warn">${icon("alert")}<div><strong>Auffällig, aber nutzbar</strong>
      <div class="muted small">Versteckte Inhalte gefunden. Sie werden entfernt, die KI sieht nur den sichtbaren Text.</div></div></div>`;
  } else if (r.ocr) {
    verdict = `<div class="doc-verdict ok">${icon("check")}<div><strong>Per Texterkennung gelesen</strong>
      <div class="muted small">Keine eingeschleusten Anweisungen. Bitte rechts kurz prüfen, ob alle Namen ersetzt wurden, bei Scans kommen Lesefehler vor.</div></div></div>`;
  } else {
    verdict = `<div class="doc-verdict ok">${icon("check")}<div><strong>Keine Auffälligkeiten</strong>
      <div class="muted small">Kein versteckter Text, keine eingeschleusten Anweisungen.</div></div></div>`;
  }
  $("docVerdict").innerHTML = verdict;

  const found = Object.values(r.entities).reduce((a, b) => a + b, 0);
  const fact = (label, value) => `<div class="fact"><dt>${label}</dt><dd>${value}</dd></div>`;
  $("docFacts").innerHTML =
    fact("Datei", esc(r.fileName)) +
    fact("Umfang", `${r.pages} ${r.pages === 1 ? "Seite" : "Seiten"}, ${r.characters.toLocaleString("de-DE")} Zeichen${r.ocr ? ", per Texterkennung" : ""}`) +
    fact("Geschützte Werte", found ? `${found} ersetzt` : "keine gefunden") +
    fact("Geht an", r.route === "Local" ? `${icon("lock", "ico inline")}lokales Modell` : `${icon("cloud", "ico inline")}Cloud`);

  const chips = Object.entries(r.entities).map(([k, v]) => chip(`${ENTITY_LABELS[k] || k} × ${v}`));
  if (r.healthTerms?.length) chips.push(chip(`Gesundheitsdaten: ${r.healthTerms.slice(0, 4).join(", ")}${r.healthTerms.length > 4 ? " ..." : ""}`, "amber", "steth"));
  chips.push(chip(`Sensibilität ${SENSITIVITY[r.sensitivity] ?? r.sensitivity}`, r.sensitivity === "High" ? "amber" : ""));
  for (const rule of r.rules || []) chips.push(chip(`Regel "${rule.name}"`, rule.action === "Block" ? "warn" : "amber", "shield"));
  if (r.injection.score > 0)
    chips.push(chip(`Manipulationsverdacht ${r.injection.score} von ${r.injection.threshold}: ${[...new Set(r.injection.findings.map(f => f.rule))].join(", ")}`,
      r.injection.score >= r.injection.threshold ? "warn" : "amber", "alert"));
  if (r.invisibleChars) chips.push(chip(`${r.invisibleChars} unsichtbare Zeichen entfernt`, "amber"));
  $("docChips").innerHTML = chips.join("");

  $("docHidden").hidden = !r.hiddenText;
  $("docHiddenText").textContent = r.hiddenText || "";

  $("docText").innerHTML = esc(r.pseudonymized).replace(/\[[A-Z]+_\d+\]/g, m => `<mark>${m}</mark>`)
    + (r.truncated ? '<p class="muted small">Vorschau gekürzt, die KI bekommt den ganzen Text.</p>' : "");

  $("docReport").hidden = false;
  $("docPreviewCard").hidden = false;
  $("docAsk").hidden = r.blocked;
}

$("docCopy").addEventListener("click", async () => {
  if (!doc.check) return;
  try {
    await navigator.clipboard.writeText(doc.check.pseudonymized);
    toast("Geschützte Fassung kopiert");
  } catch { toast("Kopieren nicht möglich", true); }
});

async function askDocument() {
  if (!doc.file || !doc.check || doc.check.blocked) return;
  const form = new FormData();
  form.append("file", doc.file);
  form.append("question", $("docQuestion").value);

  $("docSend").disabled = true;
  $("docResult").innerHTML = '<div class="msg assistant"><span class="typing"><i></i><i></i><i></i></span></div>';

  try {
    const j = await api("POST", "/v1/documents/ask", form, { headers: routeHeaders() });
    const chips = [
      chip(j.route === "Local" ? "lokal" : "Cloud", "ok", j.route === "Local" ? "lock" : "cloud"),
      j.connection ? chip(j.connection) : "",
      chip(`${j.pseudonyms} Werte geschützt`, "", "shield")
    ];
    $("docResult").innerHTML = `<div class="doc-question">${icon("chat")}${esc($("docQuestion").value)}</div>`
      + `<div class="msg assistant">${esc(j.answer)}</div><div class="chips">${chips.join("")}</div>`;
  } catch (e) {
    const findings = (e.data?.details?.findings || []).map(f => chip(`${f.rule}: ${f.snippet}`, "warn")).join("");
    $("docResult").innerHTML = `<div class="msg error">${esc(e.message)}</div><div class="chips">${findings}</div>`;
  } finally {
    $("docSend").disabled = false;
    reloadMe();
  }
}

$("docSend").addEventListener("click", askDocument);
$("docQuestion").addEventListener("keydown", e => { if (e.key === "Enter") { e.preventDefault(); askDocument(); } });

// Übersicht

async function loadDashboard() {
  const s = await api("GET", "/admin/stats");
  const w = s.week;
  const handled = w.local + w.cloud;
  const localShare = handled ? Math.round(w.local * 100 / handled) : null;

  const tile = (ico, label, value, sub, cls = "") =>
    `<div class="tile ${cls}"><div class="tile-label">${icon(ico)}${label}</div><div class="tile-value">${value}</div><div class="tile-sub">${sub}</div></div>`;

  $("tiles").innerHTML =
    tile("chat", "Anfragen heute", s.today.requests, `${w.requests} in den letzten 7 Tagen`) +
    tile("shield", "Geschützte Werte", w.protectedValues, `in 7 Tagen ersetzt, ${w.withHealthData} Anfragen mit Gesundheitsdaten`) +
    tile("alert", "Blockiert", w.blocked, w.blocked ? "Manipulationsversuche in 7 Tagen, zum Anzeigen klicken" : "keine Manipulationsversuche in 7 Tagen", w.blocked ? "alert clickable" : "") +
    tile("lock", "Lokal verarbeitet", localShare === null ? "keine" : `${localShare} %`, handled ? `${w.local} lokal, ${w.cloud} in der Cloud` : "noch keine Anfragen");

  renderChart(s.days);

  const conn = (ico, name, ready) =>
    `<strong>${esc(name || "keine")}</strong><span class="dot ${ready ? "on" : ""}" title="${ready ? "bereit" : "nicht verfügbar"}"></span>`;
  $("dashStatus").innerHTML = `
    <div class="status-row"><span class="label">Modus</span><strong>${esc(MODES[s.routes.mode] || s.routes.mode)}</strong></div>
    <div class="status-row"><span class="label">Lokal</span>${conn("lock", s.routes.local, s.routes.localReady)}</div>
    <div class="status-row"><span class="label">Cloud</span>${conn("cloud", s.routes.cloud, s.routes.cloudReady)}</div>
    <div class="status-row"><span class="label">Benutzer</span><strong>${s.users.active} aktiv</strong><span class="muted small">von ${s.users.total}</span></div>
    <div class="status-row"><span class="label">Aktive Hinweise</span><strong>${s.announcements}</strong></div>
    <div class="status-row clickable" id="versionRow" tabindex="0" title="Version und Updates"><span class="label">Version</span><span class="version-status" id="versionStatus"></span></div>`;
  renderVersionRow();
  loadVersion();

  const eventIcon = { blocked: "alert", error: "plug", admin: "edit" };
  $("dashEvents").innerHTML = s.recent.length
    ? s.recent.map(e => `<div class="event ${e.kind} clickable" data-audit-id="${e.id}" tabindex="0">${icon(eventIcon[e.kind])}<div class="event-text">
        <div>${esc(e.reason || ACTIONS[e.action] || e.action)}</div>
        <div class="muted small">${esc(e.user)} · ${relTime(e.timestamp)}</div></div></div>`).join("")
    : '<p class="empty-note">Keine Auffälligkeiten. So soll es sein.</p>';

  const max = Math.max(1, ...s.topUsers.map(u => u.requests));
  $("dashUsers").innerHTML = s.topUsers.length
    ? s.topUsers.map(u => `<div class="bar-row"><span>${esc(u.user)}</span><span class="muted">${u.requests}${u.blocked ? `, ${u.blocked} blockiert` : ""}</span>
        <div class="bar"><i style="width:${Math.round(u.requests * 100 / max)}%"></i></div></div>`).join("")
    : '<p class="empty-note">Noch keine Anfragen in den letzten 7 Tagen.</p>';
}

$("dashRefresh").addEventListener("click", () => loadDashboard().catch(e => toast(e.message, true)));

// Gestapelte Säulen: Cloud unten, Lokal, Blockiert oben (geprüfte Farbreihenfolge)
const SERIES = [
  { key: "cloud", label: "Cloud", color: "var(--series-cloud)" },
  { key: "local", label: "Lokal", color: "var(--series-local)" },
  { key: "blocked", label: "Blockiert", color: "var(--status-critical)" },
];

function niceStep(max) {
  const raw = max / 4;
  const pow = Math.pow(10, Math.floor(Math.log10(raw)));
  const n = raw / pow;
  return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * pow;
}

function renderChart(days) {
  $("legend").innerHTML = SERIES.map(s => s.key === "blocked"
    ? `<span>${icon("alert")}<i style="background:${s.color}"></i>${s.label}</span>`
    : `<span><i style="background:${s.color}"></i>${s.label}</span>`).join("");

  const W = 720, H = 250, L = 34, R = 8, T = 12, B = 28;
  const pw = W - L - R, ph = H - T - B;
  const totals = days.map(d => d.cloud + d.local + d.blocked);
  const step = niceStep(Math.max(4, ...totals));
  const top = Math.ceil(Math.max(4, ...totals) / step) * step;
  const y = v => T + ph - (v / top) * ph;
  const band = pw / days.length;
  const bw = Math.min(26, band * 0.56);

  let svg = `<svg viewBox="0 0 ${W} ${H}" role="img" aria-label="Anfragen pro Tag, gestapelt nach Cloud, Lokal und Blockiert">`;
  for (let v = 0; v <= top; v += step) {
    svg += `<line class="${v === 0 ? "baseline" : "gridline"}" x1="${L}" x2="${W - R}" y1="${y(v)}" y2="${y(v)}"/>`;
    svg += `<text class="tick" x="${L - 8}" y="${y(v) + 4}" text-anchor="end">${v}</text>`;
  }

  days.forEach((d, i) => {
    const x = L + band * i + (band - bw) / 2;
    const cx = L + band * i + band / 2;
    svg += `<rect class="hit" data-i="${i}" x="${L + band * i}" y="${T}" width="${band}" height="${ph}"/>`;

    let base = 0;
    const parts = SERIES.map(s => ({ ...s, v: d[s.key] })).filter(p => p.v > 0);
    parts.forEach((p, j) => {
      const y0 = y(base), y1 = y(base + p.v);
      base += p.v;
      const h = y0 - y1;
      const last = j === parts.length - 1;
      // 2px Lücke zur Fläche zwischen den Segmenten, oben 4px Rundung
      const gap = j > 0 ? 2 : 0;
      const hh = Math.max(0, h - gap);
      if (last) {
        const r = Math.min(4, hh, bw / 2);
        svg += `<path pointer-events="none" fill="${p.color}" d="M${x},${y0 - gap} V${y1 + r} Q${x},${y1} ${x + r},${y1} H${x + bw - r} Q${x + bw},${y1} ${x + bw},${y1 + r} V${y0 - gap} Z"/>`;
      } else {
        svg += `<rect pointer-events="none" fill="${p.color}" x="${x}" y="${y1}" width="${bw}" height="${hh}"/>`;
      }
    });

    const date = new Date(d.date + "T12:00:00");
    const isToday = i === days.length - 1;
    if (i % 2 === 1 || isToday)
      svg += `<text class="tick" x="${cx}" y="${H - 8}" text-anchor="middle">${isToday ? "Heute" : date.toLocaleDateString("de-DE", { day: "2-digit", month: "2-digit" })}</text>`;
  });

  svg += "</svg>";
  if (!totals.some(t => t > 0))
    svg += '<p class="empty-note" style="text-align:center;margin-top:-140px;position:relative">Noch keine Anfragen in den letzten 14 Tagen.</p>';
  $("chart").innerHTML = svg;

  $("chart").querySelectorAll(".hit").forEach(hit => {
    hit.addEventListener("mousemove", e => {
      const d = days[Number(hit.dataset.i)];
      const date = new Date(d.date + "T12:00:00").toLocaleDateString("de-DE", { weekday: "long", day: "numeric", month: "long" });
      showTooltip(e, `<div class="tt-title">${esc(date)}</div>` +
        [...SERIES].reverse().map(s => `<div class="tt-row"><i style="background:${s.color}"></i><span>${s.label}</span><strong>${d[s.key]}</strong></div>`).join("") +
        `<div class="tt-row" style="margin-top:4px"><span>Gesamt</span><strong>${d.cloud + d.local + d.blocked}</strong></div>`);
    });
    hit.addEventListener("mouseleave", hideTooltip);
  });

  $("chartTable").innerHTML = `<table><thead><tr><th>Tag</th>${SERIES.map(s => `<th class="num">${s.label}</th>`).join("")}</tr></thead><tbody>
    ${days.map(d => `<tr><td>${new Date(d.date + "T12:00:00").toLocaleDateString("de-DE")}</td>${SERIES.map(s => `<td class="num">${d[s.key]}</td>`).join("")}</tr>`).join("")}
  </tbody></table>`;
}

function showTooltip(e, html) {
  const t = $("tooltip");
  t.innerHTML = html;
  t.hidden = false;
  const r = t.getBoundingClientRect();
  let x = e.clientX + 14, yPos = e.clientY + 14;
  if (x + r.width > innerWidth - 8) x = e.clientX - r.width - 14;
  if (yPos + r.height > innerHeight - 8) yPos = e.clientY - r.height - 14;
  t.style.left = x + "px";
  t.style.top = yPos + "px";
}

function hideTooltip() { $("tooltip").hidden = true; }

// Verbindungen

async function loadConnections() {
  state.settings = await api("GET", "/admin/settings");
  const s = state.settings;

  const option = (c, selected) => `<option value="${c.id}" ${selected ? "selected" : ""}>${esc(c.name)}</option>`;
  $("assignLocal").innerHTML = '<option value="">Keine (sensible Anfragen werden abgelehnt)</option>' +
    s.connections.filter(c => c.onPremise).map(c => option(c, c.id === s.localConnectionId)).join("");
  $("assignCloud").innerHTML = '<option value="">Keine (alles bleibt lokal)</option>' +
    s.connections.map(c => option(c, c.id === s.cloudConnectionId)).join("");

  if (!s.connections.length) {
    $("connList").innerHTML = '<div class="card empty-card">Noch keine Verbindung. Leg oben rechts die erste an.</div>';
    return;
  }

  $("connList").innerHTML = s.connections.map(c => {
    const p = PRESETS[c.preset] || PRESETS.custom;
    const roles = [];
    if (c.id === s.localConnectionId) roles.push(chip("für sensible Daten", "ok", "lock"));
    if (c.id === s.cloudConnectionId) roles.push(chip("für unkritische Daten", "amber", "cloud"));

    const key = c.type === "Echo" ? "nicht nötig"
      : c.hasApiKey ? `gespeichert ${esc(c.apiKeyHint || "")}`
      : c.usesEnvironmentKey ? "aus Umgebungsvariable"
      : c.type === "Anthropic" ? "<span style='color:var(--crit)'>fehlt</span>" : "keiner";

    return `<div class="card conn" data-id="${c.id}">
      <div class="conn-head">
        <span class="conn-logo" style="background:${p.color}">${p.letter}</span>
        <div>
          <div class="conn-title">${esc(c.name)}</div>
          <div class="conn-sub">${esc(p.label)} · ${c.onPremise ? "lokal" : "Cloud"} · <span class="dot ${c.usable ? "on" : ""}"></span>${c.usable ? "bereit" : "unvollständig"}</div>
        </div>
      </div>
      ${roles.length ? `<div class="chips" style="margin:0">${roles.join("")}</div>` : ""}
      <dl>
        ${c.baseUrl ? `<dt>Adresse</dt><dd title="${esc(c.baseUrl)}">${esc(c.baseUrl)}</dd>` : ""}
        <dt>Modell</dt><dd>${esc(c.model || "Standard")}</dd>
        <dt>Schlüssel</dt><dd>${key}</dd>
      </dl>
      <div class="conn-test" id="test-${c.id}"></div>
      <div class="conn-actions">
        <button class="btn small" data-act="test">${icon("check")}Testen</button>
        <button class="btn small ghost" data-act="edit">${icon("edit")}Bearbeiten</button>
        <button class="btn small ghost" data-act="delete">${icon("trash")}Löschen</button>
      </div>
    </div>`;
  }).join("");
}

$("connList").addEventListener("click", async e => {
  const btn = e.target.closest("button[data-act]");
  if (!btn) return;
  const id = btn.closest(".conn").dataset.id;
  const c = state.settings.connections.find(x => x.id === id);

  if (btn.dataset.act === "edit") openConnection(c);

  if (btn.dataset.act === "delete") {
    if (!await confirmDialog("Verbindung löschen?", `${c.name} wird entfernt. Das lässt sich nicht rückgängig machen.`, "Löschen")) return;
    try {
      await api("DELETE", `/admin/connections/${id}`);
      toast("Verbindung gelöscht");
      await loadConnections();
      refreshStatus();
    } catch (err) { toast(err.message, true); }
  }

  if (btn.dataset.act === "test") {
    const out = $("test-" + id);
    btn.disabled = true;
    out.innerHTML = '<span class="muted small">Teste...</span>';
    try {
      out.innerHTML = testResult(await api("POST", `/admin/connections/${id}/test`));
    } catch (err) {
      out.innerHTML = chip(err.message, "warn");
    } finally {
      btn.disabled = false;
    }
  }
});

function testResult(r) {
  return r.ok
    ? chip(`antwortet in ${(r.ms / 1000).toFixed(1)} s: "${r.reply.trim().slice(0, 40)}"`, "ok", "check")
    : chip(r.error, "warn", "x");
}

$("saveRoutes").addEventListener("click", async () => {
  try {
    await api("PUT", "/admin/routes", { localConnectionId: $("assignLocal").value || null, cloudConnectionId: $("assignCloud").value || null });
    toast("Zuordnung gespeichert");
    await loadConnections();
    refreshStatus();
  } catch (e) { toast(e.message, true); }
});

// Dialog für Verbindungen

$("presetGrid").innerHTML = Object.entries(PRESETS).map(([id, p]) => `
  <button type="button" class="preset" data-preset="${id}">
    <span class="conn-logo" style="background:${p.color};width:36px;height:36px;font-size:14px">${p.letter}</span>
    <strong>${esc(p.label)}</strong>
    <small>${esc(p.sub)}</small>
    <span class="tag ${p.local ? "local" : "cloud"}">${p.local ? "lokal" : "Cloud"}</span>
  </button>`).join("");

$("presetGrid").addEventListener("click", e => {
  const b = e.target.closest("[data-preset]");
  if (b) fillConnectionForm(b.dataset.preset, null);
});

$("addConnection").addEventListener("click", () => openConnection(null));

function openConnection(existing) {
  state.editing = existing;
  $("connDialogTitle").textContent = existing ? `${existing.name} bearbeiten` : "Neue Verbindung";
  $("presetStep").hidden = !!existing;
  $("formStep").hidden = !existing;
  $("cBack").hidden = !!existing;
  if (existing) fillConnectionForm(existing.preset, existing);
  $("connDialog").showModal();
}

function fillConnectionForm(presetId, c) {
  const p = PRESETS[presetId] || PRESETS.custom;
  state.preset = presetId;
  const type = c?.type || p.type || "OpenAI";

  $("presetStep").hidden = true;
  $("formStep").hidden = false;
  $("presetNote").textContent = p.note;
  $("presetNote").className = "preset-note" + (p.warn ? " warn" : "");

  $("cName").value = c?.name ?? p.label;
  $("cUrl").value = c?.baseUrl ?? p.url ?? "";
  $("cKey").value = "";
  $("cModel").value = c?.model ?? p.model ?? "";
  $("cEffort").value = c?.effort || "medium";
  $("cTimeout").value = c?.timeoutSeconds ?? (type === "Anthropic" ? 300 : 120);
  $("cTemp").value = c ? (c.temperature ?? "") : (p.local && type !== "Echo" ? "0.3" : "");
  $("cOnPrem").checked = c ? c.onPremise : !!p.local;
  $("cModelList").innerHTML = "";
  $("cModelsInfo").textContent = "";
  $("cTestResult").innerHTML = "";

  $("cUrlField").hidden = type !== "OpenAI";
  $("cKeyField").hidden = type === "Echo";
  $("cEffortField").hidden = type !== "Anthropic";
  $("cTempField").hidden = type !== "OpenAI";
  $("cOnPrem").disabled = type === "Anthropic";

  $("cKeyHint").textContent = c?.hasApiKey
    ? `Gespeichert (${c.apiKeyHint || "verborgen"}). Leer lassen, um ihn zu behalten.`
    : p.local ? "Bei lokalen Servern meist nicht nötig." : "";
}

function connectionInput() {
  const p = PRESETS[state.preset] || PRESETS.custom;
  const type = state.editing?.type || p.type || "OpenAI";
  return {
    name: $("cName").value.trim(),
    preset: state.preset,
    type,
    baseUrl: type === "OpenAI" ? $("cUrl").value.trim() : null,
    apiKey: $("cKey").value.trim() || null,
    clearApiKey: false,
    model: $("cModel").value.trim(),
    effort: type === "Anthropic" ? $("cEffort").value : null,
    timeoutSeconds: Number($("cTimeout").value) || 120,
    temperature: type === "OpenAI" && $("cTemp").value !== "" ? Number($("cTemp").value) : null,
    onPremise: type === "Anthropic" ? false : $("cOnPrem").checked,
  };
}

$("cBack").addEventListener("click", () => {
  $("presetStep").hidden = false;
  $("formStep").hidden = true;
});

$("cLoadModels").addEventListener("click", async () => {
  const btn = $("cLoadModels");
  btn.disabled = true;
  $("cModelsInfo").textContent = "Lade Modelle...";
  try {
    const models = await api("POST", "/admin/connections/preview/models", { connection: connectionInput(), id: state.editing?.id ?? null });
    $("cModelList").innerHTML = models.map(m => `<option value="${esc(m)}">`).join("");
    $("cModelsInfo").textContent = models.length
      ? `${models.length} Modelle gefunden. Ins Feld klicken zum Auswählen.`
      : "Keine Modelle gefunden.";
    if (!$("cModel").value && models.length) $("cModel").value = models[0];
  } catch (e) {
    $("cModelsInfo").textContent = e.message;
  } finally {
    btn.disabled = false;
  }
});

$("cTest").addEventListener("click", async () => {
  const btn = $("cTest");
  btn.disabled = true;
  $("cTestResult").innerHTML = '<span class="muted small">Teste Verbindung...</span>';
  try {
    $("cTestResult").innerHTML = testResult(await api("POST", "/admin/connections/preview/test", { connection: connectionInput(), id: state.editing?.id ?? null }));
  } catch (e) {
    $("cTestResult").innerHTML = chip(e.message, "warn");
  } finally {
    btn.disabled = false;
  }
});

$("cSave").addEventListener("click", async () => {
  const input = connectionInput();
  if (!input.name) { toast("Bitte einen Namen eingeben.", true); return; }

  try {
    if (state.editing) await api("PUT", `/admin/connections/${state.editing.id}`, input);
    else await api("POST", "/admin/connections", input);
    $("connDialog").close();
    toast("Verbindung gespeichert");
    await loadConnections();
    refreshStatus();
  } catch (e) {
    $("cTestResult").innerHTML = chip(e.message, "warn");
  }
});

// Enter in einem Feld soll Dialoge nicht schließen
["connForm", "userForm", "noticeForm"].forEach(id =>
  $(id).addEventListener("submit", e => { if (e.submitter?.value !== "cancel") e.preventDefault(); }));

// Regeln

const RULE_ACTIONS = {
  Replace: { label: "Ersetzen", icon: "shield" },
  LocalOnly: { label: "Nur lokal", icon: "lock" },
  Block: { label: "Blockieren", icon: "x" },
  Warn: { label: "Protokollieren", icon: "list" },
};

const INSTRUCTION_SNIPPETS = [
  ["Keine Diagnosen", "Stelle keine Diagnosen. Nenne mögliche Ursachen und was ärztlich abgeklärt werden sollte."],
  ["Notfall 112", "Weise bei Anzeichen eines Notfalls (z.B. Brustschmerz, Atemnot, Lähmungen) sofort auf den Notruf 112 hin."],
  ["Dosierungen prüfen", "Nenne Dosierungen nur mit dem Hinweis, dass sie ärztlich geprüft werden müssen."],
  ["Fachbegriffe erklären", "Erkläre medizinische Fachbegriffe beim ersten Vorkommen kurz in Klammern."],
  ["Unsicherheit sagen", "Wenn du dir nicht sicher bist, sag das deutlich und erfinde keine Fakten."],
  ["Leitlinien", "Orientiere dich an aktuellen deutschen Leitlinien und nenne sie, wenn möglich."],
  ["Platzhalter behalten", "Übernimm Platzhalter wie [NAME_1] unverändert."],
];

const RULE_EXAMPLES = [
  { name: "Interne Patientennummer", patterns: "P-\\d{4,6}", isRegex: true, action: "Replace", label: "PATIENTENNR" },
  { name: "Zugangsdaten", patterns: "Passwort\nKennwort\nPIN\nZugangsdaten", action: "Block", message: "Bitte keine Passwörter oder Zugangsdaten eingeben." },
  { name: "Besonders sensible Diagnosen", patterns: "HIV\nAIDS\nSchwangerschaftsabbruch\nSuizid\nPsychiatrie", action: "LocalOnly" },
  { name: "Minderjährige", patterns: "Kind\nKinder\nMinderjährig\nSäugling", action: "Warn" },
  { name: "Fallnummer Klinik", patterns: "\\bFall\\s?Nr\\.?\\s?\\d{6,10}\\b", isRegex: true, action: "Replace", label: "FALLNR" },
];

const TEMPLATE_EXAMPLES = [
  {
    title: "Patientenabsage", mode: "Run",
    text: "Schreibe eine kurze, freundliche Nachricht an {{Patient}}. Der Termin am {{Absagetermin:termin}} muss leider abgesagt werden. " +
      "Grund: {{Grund:auswahl=Erkrankung in der Praxis|Terminüberschneidung|Urlaub|Fortbildung}}. " +
      "Biete als neuen Termin {{Neuer Termin:termin}} an und bitte um eine kurze Bestätigung. Entschuldige dich einmal, nicht mehrfach.\n" +
      "{{Zusätzlicher Hinweis?:langtext}}",
  },
  {
    title: "Terminerinnerung", mode: "Run",
    text: "Schreibe eine kurze Terminerinnerung an {{Patient}} für den Termin am {{Termin:termin}}. " +
      "Bitte mitbringen: {{Mitbringen:auswahl=Versichertenkarte|Überweisung|Medikamentenplan|Befunde vom Facharzt}}. " +
      "Freundlich, höchstens vier Sätze.",
  },
  {
    title: "Rezept abholbereit", mode: "Run",
    text: "Schreibe eine kurze Nachricht an {{Patient}}, dass das Rezept für {{Medikament}} ab {{Abholbar ab:datum}} in der Praxis abgeholt werden kann. " +
      "Freundlich, höchstens drei Sätze.",
  },
  {
    title: "Anamnesefragen", mode: "Run",
    text: "Welche Fragen sollte ich in der Anamnese noch stellen? Beschwerden: {{Beschwerden:langtext}}. Alter: {{Alter:zahl}} Jahre. " +
      "Gib die Fragen als kurze Liste, sortiert nach Wichtigkeit.",
  },
  { title: "Arztbrief entwerfen", mode: "Insert", text: "Entwirf einen kurzen, sachlichen Arztbrief an den weiterbehandelnden Kollegen. Stichpunkte:" },
  { title: "Einfache Sprache", mode: "Insert", text: "Erkläre den folgenden Befund in einfacher Sprache für den Patienten, ohne Fachbegriffe:" },
  { title: "Zusammenfassen", mode: "Insert", text: "Fasse den folgenden Text in fünf Stichpunkten zusammen:" },
];

const ruleState = { tab: "instructions", editingRule: null, ruleAction: "Replace", ruleMode: "words", editingTemplate: null, templateMode: "Run", previewTimer: null, testTimer: null };

// Reiter

function showRuleTab(tab) {
  ruleState.tab = tab;
  $("ruleTabs").querySelectorAll("button").forEach(b => b.classList.toggle("on", b.dataset.tab === tab));
  document.querySelectorAll(".rule-pane").forEach(p => p.hidden = p.dataset.pane !== tab);
}

$("ruleTabs").addEventListener("click", e => {
  const b = e.target.closest("button[data-tab]");
  if (b) showRuleTab(b.dataset.tab);
});

// Texterkennung: Einstellungen und Status, ob Tesseract gefunden wurde
async function loadOcr() {
  $("ocrStatus").innerHTML = `<div class="doc-busy"><span class="typing"><i></i><i></i><i></i></span>Tesseract wird gesucht ...</div>`;
  const r = await api("GET", "/admin/ocr");
  const o = r.settings, s = r.status;
  $("ocrEnabled").checked = o.enabled;
  $("ocrPath").value = o.tesseractPath || "";
  $("ocrTessdata").value = o.tessdataDir || "";
  $("ocrLangs").value = o.languages;
  $("ocrPages").value = o.maxPages;

  const guide = `<a href="https://github.com/kschnieders/zwijg/blob/main/docs/texterkennung.md" target="_blank" rel="noopener">Anleitung</a>`;
  $("ocrStatus").innerHTML = s.available
    ? `<div class="doc-verdict ok">${icon("check")}<div><strong>Bereit</strong>
        <div class="muted small">${esc(s.version || "Tesseract")}, Sprachen: ${esc(s.languages.join(", "))}</div></div></div>`
    : `<div class="doc-verdict warn">${icon("alert")}<div><strong>${esc(s.problem || "Nicht bereit")}</strong>
        <div class="muted small">Ohne Texterkennung werden Scans abgelehnt. Wie man Tesseract installiert, steht in der ${guide}.${
          s.languages?.length ? ` Gefundene Sprachen: ${esc(s.languages.join(", "))}.` : ""}</div></div></div>`;
}

$("ocrRecheck").addEventListener("click", () => loadOcr().catch(err => toast(err.message, true)));

$("ocrSave").addEventListener("click", async () => {
  try {
    await api("PUT", "/admin/ocr", {
      enabled: $("ocrEnabled").checked,
      tesseractPath: $("ocrPath").value,
      tessdataDir: $("ocrTessdata").value,
      languages: $("ocrLangs").value || "deu+eng",
      maxPages: Number($("ocrPages").value) || 20,
    });
    toast("Texterkennung gespeichert");
    await loadOcr();
  } catch (err) { toast(err.message, true); }
});

async function loadRules() {
  state.settings = await api("GET", "/admin/settings");
  loadOcr().catch(err => toast(err.message, true));
  const s = state.settings;
  const p = s.policy;

  // Weiterleitung und Erkennung
  document.querySelector(`input[name=mode][value=${p.routing.mode}]`).checked = true;
  document.querySelector(`input[name=injAction][value=${p.injection.action}]`).checked = true;
  $("cloudMax").value = p.routing.cloudMaxSensitivity;
  $("docsLocal").checked = p.routing.documentsLocalOnly;
  $("promptThreshold").value = p.injection.promptThreshold;
  $("docThreshold").value = p.injection.documentThreshold;
  $("storePrompts").checked = p.storePrompts;
  $("llmNames").checked = p.useLocalLlmForNames;
  $("extraNames").value = (p.extraNames || []).join(NL);
  $("extraPlaces").value = (p.extraPlaces || []).join(NL);
  $("ignoredWords").value = (p.ignoredWords || []).join(NL);

  // Anweisungen
  const i = s.instructions;
  $("iEnabled").checked = i.enabled;
  $("iLanguage").value = i.language;
  $("iAddressing").value = i.addressing;
  $("iAudience").value = i.audience;
  $("iTone").value = i.tone;
  $("iLength").value = i.length;
  $("iFormat").value = i.format;
  $("iCustom").value = i.customText || "";
  $("iFooter").value = i.responseFooter || "";
  syncInstructions(true);

  renderRules();
  renderTemplates();
  loadHistorySettings().catch(() => {});
  showRuleTab(ruleState.tab);
}

async function savePolicy() {
  try {
    await api("PUT", "/admin/policy", {
      routing: {
        mode: document.querySelector("input[name=mode]:checked").value,
        cloudMaxSensitivity: $("cloudMax").value,
        documentsLocalOnly: $("docsLocal").checked,
      },
      injection: {
        action: document.querySelector("input[name=injAction]:checked").value,
        promptThreshold: Number($("promptThreshold").value),
        documentThreshold: Number($("docThreshold").value),
      },
      storePrompts: $("storePrompts").checked,
      useLocalLlmForNames: $("llmNames").checked,
      extraNames: lines("extraNames"),
      extraPlaces: lines("extraPlaces"),
      ignoredWords: lines("ignoredWords"),
    });
    toast("Gespeichert");
    refreshStatus();
    preview();
  } catch (err) { toast(err.message, true); }
}

document.querySelectorAll("[data-save-policy]").forEach(b => b.addEventListener("click", savePolicy));

// Anweisungen

function instructionInput() {
  return {
    enabled: $("iEnabled").checked,
    language: $("iLanguage").value,
    addressing: $("iAddressing").value,
    audience: $("iAudience").value,
    tone: $("iTone").value,
    length: $("iLength").value,
    format: $("iFormat").value,
    customText: $("iCustom").value,
    responseFooter: $("iFooter").value.trim(),
  };
}

function syncInstructions(immediate = false) {
  $("styleGrid").classList.toggle("off", !$("iEnabled").checked);
  $("iCustomCount").textContent = `${$("iCustom").value.length} / 4000`;
  clearTimeout(ruleState.previewTimer);
  ruleState.previewTimer = setTimeout(async () => {
    try {
      const r = await api("POST", "/admin/instructions/preview", instructionInput());
      const footer = $("iFooter").value.trim();
      const text = r.text + (footer ? `${NL}${NL}Unter jeder Antwort: ${footer}` : "");
      $("iPreview").textContent = text.trim() || "Keine Anweisungen. Die KI bekommt nur die Frage.";
      $("iPreview").classList.toggle("empty", !text.trim());
    } catch { /* Vorschau ist nicht wichtig genug für eine Fehlermeldung */ }
  }, immediate ? 0 : 250);
}

["iEnabled", "iLanguage", "iAddressing", "iAudience", "iTone", "iLength", "iFormat", "iCustom", "iFooter"].forEach(id => {
  $(id).addEventListener("input", () => syncInstructions());
  $(id).addEventListener("change", () => syncInstructions());
});

$("iSnippets").innerHTML = INSTRUCTION_SNIPPETS.map(([label], i) =>
  `<button type="button" data-snippet="${i}">${icon("plus")}${esc(label)}</button>`).join("");

$("iSnippets").addEventListener("click", e => {
  const b = e.target.closest("[data-snippet]");
  if (!b) return;
  const text = INSTRUCTION_SNIPPETS[Number(b.dataset.snippet)][1];
  const current = $("iCustom").value.trimEnd();
  if (current.includes(text)) { toast("Steht schon drin"); return; }
  $("iCustom").value = current ? current + NL + text : text;
  syncInstructions();
});

$("iSave").addEventListener("click", async () => {
  try {
    await api("PUT", "/admin/instructions", instructionInput());
    toast("Anweisungen gespeichert");
  } catch (err) { toast(err.message, true); }
});

// Schutzregeln

function renderRules() {
  const rules = state.settings.protectionRules;
  $("ruleCount").textContent = rules.filter(r => r.enabled).length || "";

  $("ruleList").innerHTML = rules.length ? rules.map((r, i) => {
    const a = RULE_ACTIONS[r.action];
    const patterns = r.patterns.split(NL).filter(Boolean);
    const shown = patterns.slice(0, 4).map(p => `<code>${esc(p)}</code>`).join(", ") + (patterns.length > 4 ? ` und ${patterns.length - 4} weitere` : "");
    return `<div class="rule-item ${r.enabled ? "" : "off"}" data-i="${i}">
      <label class="switch" title="${r.enabled ? "aktiv" : "aus"}"><input type="checkbox" data-act="toggle" ${r.enabled ? "checked" : ""}><span class="track"></span></label>
      <div style="min-width:0">
        <div class="rule-name">${esc(r.name)}<span class="badge action-${r.action}">${icon(a.icon)}${a.label}</span>
          ${r.action === "Replace" ? `<span class="muted small">[${esc(r.label || "EIGENE")}_1]</span>` : ""}</div>
        <div class="rule-sub">${r.isRegex ? "Muster: " : "Wörter: "}${shown}</div>
      </div>
      <div class="rule-actions">
        <button class="icon-btn" data-act="edit" title="Bearbeiten">${icon("edit")}</button>
        <button class="icon-btn" data-act="delete" title="Löschen">${icon("trash")}</button>
      </div>
    </div>`;
  }).join("") : '<div class="rule-empty">Noch keine eigenen Regeln. Leg eine an oder übernimm ein Beispiel.</div>';

  const names = new Set(rules.map(r => r.name));
  $("ruleExamples").innerHTML = RULE_EXAMPLES.filter(x => !names.has(x.name)).map(x =>
    `<button type="button" data-example="${esc(x.name)}">${icon("plus")}${esc(x.name)} <span class="muted">(${RULE_ACTIONS[x.action].label})</span></button>`).join("")
    || '<span class="muted small">Alle Beispiele sind übernommen.</span>';
}

async function saveRules(rules, message) {
  try {
    await api("PUT", "/admin/rules", rules);
    state.settings.protectionRules = rules;
    renderRules();
    if (message) toast(message);
    return true;
  } catch (err) {
    toast(err.message, true);
    return false;
  }
}

$("ruleList").addEventListener("click", async e => {
  const el = e.target.closest("[data-act]");
  if (!el) return;
  const i = Number(el.closest(".rule-item").dataset.i);
  const rules = structuredClone(state.settings.protectionRules);

  if (el.dataset.act === "toggle") {
    rules[i].enabled = el.checked;
    await saveRules(rules, el.checked ? "Regel aktiviert" : "Regel ausgeschaltet");
  }
  if (el.dataset.act === "edit") openRule(i);
  if (el.dataset.act === "delete") {
    if (!await confirmDialog("Regel löschen?", `"${rules[i].name}" wird entfernt.`, "Löschen")) return;
    rules.splice(i, 1);
    await saveRules(rules, "Regel gelöscht");
  }
});

$("ruleExamples").addEventListener("click", e => {
  const b = e.target.closest("[data-example]");
  if (!b) return;
  const x = RULE_EXAMPLES.find(r => r.name === b.dataset.example);
  openRule(null, x);
});

$("addRule").addEventListener("click", () => openRule(null));

function openRule(index, preset) {
  const r = index !== null ? state.settings.protectionRules[index] : preset;
  ruleState.editingRule = index;
  $("ruleDialogTitle").textContent = index !== null ? `${r.name} bearbeiten` : "Neue Schutzregel";
  $("rName").value = r?.name ?? "";
  $("rPatterns").value = r?.patterns ?? "";
  $("rCase").checked = r?.caseSensitive ?? false;
  $("rLabel").value = r?.label ?? "";
  $("rMessage").value = r?.message ?? "";
  $("rEnabled").checked = r?.enabled ?? true;
  $("rTest").value = "";
  $("rTestResult").innerHTML = "";
  $("rError").hidden = true;
  setRuleAction(r?.action ?? "Replace");
  setRuleMode(r?.isRegex ? "regex" : "words");
  $("ruleDialog").showModal();
  $("rName").focus();
}

function setRuleAction(a) {
  ruleState.ruleAction = a;
  $("rAction").querySelectorAll("button").forEach(b => b.classList.toggle("on", b.dataset.a === a));
  $("rLabelField").hidden = a !== "Replace";
  $("rMessageField").hidden = a !== "Block";
  syncRuleForm();
}

function setRuleMode(m) {
  ruleState.ruleMode = m;
  $("rMode").querySelectorAll("button").forEach(b => b.classList.toggle("on", b.dataset.m === m));
  $("rPatternHint").textContent = m === "words"
    ? "Ein Begriff pro Zeile. Es werden nur ganze Wörter gefunden, \"HIV\" trifft also nicht \"Archiv\"."
    : "Ein regulärer Ausdruck pro Zeile, z.B. P-\\d{5} für P-12345. Zu langsame Muster werden automatisch abgebrochen.";
  syncRuleForm();
}

$("rAction").addEventListener("click", e => { const b = e.target.closest("button[data-a]"); if (b) setRuleAction(b.dataset.a); });
$("rMode").addEventListener("click", e => { const b = e.target.closest("button[data-m]"); if (b) setRuleMode(b.dataset.m); });

function ruleInput() {
  const old = ruleState.editingRule !== null ? state.settings.protectionRules[ruleState.editingRule] : null;
  return {
    id: old?.id,
    name: $("rName").value.trim(),
    patterns: $("rPatterns").value,
    isRegex: ruleState.ruleMode === "regex",
    caseSensitive: $("rCase").checked,
    action: ruleState.ruleAction,
    label: $("rLabel").value.trim().toUpperCase() || null,
    message: $("rMessage").value.trim() || null,
    enabled: $("rEnabled").checked,
  };
}

function syncRuleForm() {
  const label = $("rLabel").value.trim().toUpperCase() || "EIGENE";
  $("rLabelPreview").textContent = `Wird zu [${label}_1]. Nur Großbuchstaben, 2 bis 20 Zeichen.`;

  clearTimeout(ruleState.testTimer);
  ruleState.testTimer = setTimeout(async () => {
    const text = $("rTest").value;
    if (!text.trim() || !$("rPatterns").value.trim()) { $("rTestResult").innerHTML = ""; return; }
    try {
      const r = await api("POST", "/admin/rules/test", { rule: ruleInput(), text });
      if (!r.hits.length) { $("rTestResult").innerHTML = chip("kein Treffer", "", "x"); return; }
      let html = "", pos = 0;
      for (const h of r.hits.sort((a, b) => a.start - b.start)) {
        if (h.start < pos) continue;
        html += esc(text.slice(pos, h.start)) + `<mark>${esc(text.slice(h.start, h.start + h.length))}</mark>`;
        pos = h.start + h.length;
      }
      $("rTestResult").innerHTML = chip(`${r.hits.length} ${r.hits.length === 1 ? "Treffer" : "Treffer"}`, "ok", "check") + "<div>" + html + esc(text.slice(pos)) + "</div>";
    } catch (err) {
      $("rTestResult").innerHTML = chip(err.message, "warn", "alert");
    }
  }, 300);
}

["rPatterns", "rTest", "rLabel", "rCase"].forEach(id => $(id).addEventListener("input", syncRuleForm));
$("rCase").addEventListener("change", syncRuleForm);

$("rSave").addEventListener("click", async () => {
  const input = ruleInput();
  if (!input.name || !input.patterns.trim()) {
    $("rError").textContent = "Bitte Name und mindestens einen Begriff angeben.";
    $("rError").hidden = false;
    return;
  }

  const rules = structuredClone(state.settings.protectionRules);
  if (ruleState.editingRule !== null) rules[ruleState.editingRule] = input;
  else rules.push(input);

  try {
    await api("PUT", "/admin/rules", rules);
    $("ruleDialog").close();
    toast("Regel gespeichert");
    await loadRules();
  } catch (err) {
    $("rError").textContent = err.message;
    $("rError").hidden = false;
  }
});

// Vorlagen

const VAR_TYPES = {
  text: "Text",
  langtext: "Langer Text",
  datum: "Datum",
  uhrzeit: "Uhrzeit",
  termin: "Datum und Uhrzeit",
  zahl: "Zahl",
  auswahl: "Auswahl",
};

// Gleiche Schreibweise wie auf dem Server: {{Name}}, {{Name:typ}}, {{Name:auswahl=A|B}}, {{Name?}} ist optional
function parseVars(text) {
  const re = /\{\{\s*([^{}:]+?)\s*(?::\s*([^{}=]+?)\s*(?:=\s*([^{}]*?))?)?\s*\}\}/g;
  const vars = [];
  for (const m of text.matchAll(re)) {
    let name = m[1].trim();
    const optional = name.endsWith("?");
    name = name.replace(/\?+$/, "").trim();
    const type = (m[2] || "text").trim().toLowerCase();
    const options = m[3] ? m[3].split("|").map(s => s.trim()).filter(Boolean) : [];
    if (!vars.some(v => v.name.toLowerCase() === name.toLowerCase())) vars.push({ name, type, optional, options });
  }
  return vars;
}

function renderTemplates() {
  const list = state.settings.templates;
  $("templateCount").textContent = list.filter(t => t.enabled).length || "";

  $("templateList").innerHTML = list.length ? list.map((t, i) => {
    const vars = parseVars(t.text);
    const run = t.mode === "Run";
    return `<div class="rule-item ${t.enabled ? "" : "off"}" data-i="${i}">
      <label class="switch" title="${t.enabled ? "sichtbar" : "versteckt"}"><input type="checkbox" data-act="toggle" ${t.enabled ? "checked" : ""}><span class="track"></span></label>
      <div style="min-width:0">
        <div class="rule-name">${esc(t.title)}
          <span class="badge ${run ? "action-LocalOnly" : "neutral"}">${icon(run ? "send" : "edit")}${run ? "Direkt ausführen" : "Einfügen"}</span>
          ${vars.length ? `<span class="badge action-Replace">${vars.length} ${vars.length === 1 ? "Feld" : "Felder"}</span>` : ""}
        </div>
        <div class="rule-sub">${esc(t.text)}</div>
      </div>
      <div class="rule-actions">
        <button class="icon-btn" data-act="up" title="Nach vorn" ${i === 0 ? "disabled" : ""}>${icon("up")}</button>
        <button class="icon-btn" data-act="edit" title="Bearbeiten">${icon("edit")}</button>
        <button class="icon-btn" data-act="delete" title="Löschen">${icon("trash")}</button>
      </div>
    </div>`;
  }).join("") : '<div class="rule-empty">Noch keine Vorlagen. Leg eine an oder übernimm ein Beispiel.</div>';

  const titles = new Set(list.map(t => t.title));
  $("templateExamples").innerHTML = TEMPLATE_EXAMPLES.filter(x => !titles.has(x.title)).map(x =>
    `<button type="button" data-example="${esc(x.title)}">${icon("plus")}${esc(x.title)}</button>`).join("")
    || '<span class="muted small">Alle Beispiele sind übernommen.</span>';
}

async function saveTemplates(list, message) {
  try {
    await api("PUT", "/admin/templates", list);
    state.settings.templates = list;
    renderTemplates();
    if (message) toast(message);
    reloadMe();
  } catch (err) { toast(err.message, true); }
}

$("templateList").addEventListener("click", async e => {
  const el = e.target.closest("[data-act]");
  if (!el) return;
  const i = Number(el.closest(".rule-item").dataset.i);
  const list = structuredClone(state.settings.templates);

  if (el.dataset.act === "toggle") { list[i].enabled = el.checked; await saveTemplates(list); }
  if (el.dataset.act === "up" && i > 0) { [list[i - 1], list[i]] = [list[i], list[i - 1]]; await saveTemplates(list); }
  if (el.dataset.act === "edit") openTemplate(i);
  if (el.dataset.act === "delete") {
    if (!await confirmDialog("Vorlage löschen?", `"${list[i].title}" verschwindet aus dem Chat.`, "Löschen")) return;
    list.splice(i, 1);
    await saveTemplates(list, "Vorlage gelöscht");
  }
});

$("templateExamples").addEventListener("click", async e => {
  const b = e.target.closest("[data-example]");
  if (!b) return;
  const x = TEMPLATE_EXAMPLES.find(t => t.title === b.dataset.example);
  await saveTemplates([...structuredClone(state.settings.templates), { title: x.title, text: x.text, mode: x.mode, enabled: true }],
    `Vorlage "${x.title}" übernommen`);
});

$("addTemplate").addEventListener("click", () => openTemplate(null));

function setTemplateMode(mode) {
  ruleState.templateMode = mode;
  $("tMode").querySelectorAll("button").forEach(b => b.classList.toggle("on", b.dataset.m === mode));
}

$("tMode").addEventListener("click", e => {
  const b = e.target.closest("button[data-m]");
  if (b) setTemplateMode(b.dataset.m);
});

function openTemplate(index) {
  const t = index !== null ? state.settings.templates[index] : null;
  ruleState.editingTemplate = index;
  $("templateDialogTitle").textContent = t ? `${t.title} bearbeiten` : "Neue Vorlage";
  $("tTitle").value = t?.title ?? "";
  $("tText").value = t?.text ?? "";
  $("tEnabled").checked = t?.enabled ?? true;
  $("tError").hidden = true;
  setTemplateMode(t?.mode ?? "Run");
  syncTemplateVars();
  $("templateDialog").showModal();
  $("tTitle").focus();
}

// Knöpfe zum Einfügen von Feldern, damit niemand die Schreibweise auswendig kennen muss
$("tVarButtons").innerHTML = Object.entries(VAR_TYPES).map(([type, label]) =>
  `<button type="button" data-var="${type}">${icon("plus")}${label}</button>`).join("")
  + `<button type="button" data-var="optional" title="Feld, das leer bleiben darf">${icon("plus")}Optional</button>`;

$("tVarButtons").addEventListener("click", async e => {
  const b = e.target.closest("[data-var]");
  if (!b) return;
  const type = b.dataset.var;
  const name = await promptDialog(type === "optional" ? "Name des optionalen Feldes" : `Name des Feldes (${VAR_TYPES[type]})`, "");
  if (!name) return;

  let token;
  if (type === "auswahl") {
    const options = await promptDialog("Möglichkeiten, getrennt mit |", "Ja|Nein");
    if (!options) return;
    token = `{{${name}:auswahl=${options}}}`;
  } else if (type === "optional") {
    token = `{{${name}?}}`;
  } else {
    token = type === "text" ? `{{${name}}}` : `{{${name}:${type}}}`;
  }

  // An der Cursorposition einsetzen
  const area = $("tText");
  const start = area.selectionStart ?? area.value.length;
  const end = area.selectionEnd ?? start;
  area.value = area.value.slice(0, start) + token + area.value.slice(end);
  area.focus();
  area.setSelectionRange(start + token.length, start + token.length);
  syncTemplateVars();
});

function syncTemplateVars() {
  const vars = parseVars($("tText").value);
  $("tVars").innerHTML = vars.length
    ? '<span class="muted small">Wird beim Klick abgefragt:</span>' + vars.map(v =>
      `<span class="var-chip">${esc(v.name)}<small>${esc(VAR_TYPES[v.type] || v.type)}${v.optional ? ", optional" : ""}</small></span>`).join("")
    : '<span class="muted small">Keine Felder. Die Vorlage wird ohne Rückfrage verwendet.</span>';
}

$("tText").addEventListener("input", syncTemplateVars);

$("tSave").addEventListener("click", async () => {
  const t = { title: $("tTitle").value.trim(), text: $("tText").value.trim(), mode: ruleState.templateMode, enabled: $("tEnabled").checked };
  if (!t.title || !t.text) { $("tError").textContent = "Bitte Titel und Text angeben."; $("tError").hidden = false; return; }

  const list = structuredClone(state.settings.templates);
  if (ruleState.editingTemplate !== null) list[ruleState.editingTemplate] = { ...list[ruleState.editingTemplate], ...t };
  else list.push(t);

  try {
    await api("PUT", "/admin/templates", list);
    $("templateDialog").close();
    toast("Vorlage gespeichert");
    await loadRules();
    reloadMe();
  } catch (err) {
    $("tError").textContent = err.message;
    $("tError").hidden = false;
  }
});

// Vorlage im Chat ausfüllen

const run = { template: null, vars: [] };

function openRunDialog(t, vars) {
  run.template = t;
  run.vars = vars;
  $("runTitle").textContent = t.title;
  $("runKind").textContent = t.mode === "Run" ? "Vorlage, wird direkt ausgeführt" : "Vorlage, wird ins Eingabefeld eingefügt";
  $("runGo").innerHTML = t.mode === "Run" ? icon("send") + "Ausführen" : icon("edit") + "Einfügen";
  $("runNote").textContent = "Auch diese Angaben werden vor dem Versand wie jede Nachricht geschützt.";
  $("runError").hidden = true;

  $("runFields").innerHTML = vars.map((v, i) => {
    const id = `run_${i}`;
    const label = `<span>${esc(v.name)}${v.optional ? "<small>optional</small>" : ""}</span>`;
    const req = v.optional ? "" : "required";
    switch (v.type) {
      case "langtext":
        return `<label class="field wide">${label}<textarea id="${id}" rows="3" ${req}></textarea></label>`;
      case "datum":
        return `<label class="field">${label}<input type="date" id="${id}" ${req}></label>`;
      case "uhrzeit":
        return `<label class="field">${label}<input type="time" id="${id}" step="300" ${req}></label>`;
      case "termin":
        return `<label class="field wide">${label}<div class="run-termin"><input type="date" id="${id}" ${req}><input type="time" id="${id}_t" step="300"></div></label>`;
      case "zahl":
        return `<label class="field">${label}<input type="number" id="${id}" ${req}></label>`;
      case "auswahl":
        return `<label class="field">${label}<select id="${id}" ${req}><option value="">Bitte wählen</option>${v.options.map(o => `<option>${esc(o)}</option>`).join("")}</select></label>`;
      default:
        return `<label class="field">${label}<input type="text" id="${id}" ${req} autocomplete="off"></label>`;
    }
  }).join("");

  $("runDialog").showModal();
  $("run_0")?.focus();
}

// Werte so formatieren, wie die KI und die Karte sie lesen sollen
function formatValue(v, i) {
  const raw = $(`run_${i}`).value.trim();
  if (!raw) return "";
  const longDate = s => new Date(s + "T12:00:00").toLocaleDateString("de-DE", { weekday: "long", day: "2-digit", month: "2-digit", year: "numeric" });
  switch (v.type) {
    case "datum": return longDate(raw);
    case "uhrzeit": return `${raw} Uhr`;
    case "termin": {
      const time = $(`run_${i}_t`).value;
      return time ? `${longDate(raw)} um ${time} Uhr` : longDate(raw);
    }
    default: return raw;
  }
}

function fillTemplate(text, values) {
  const re = /\{\{\s*([^{}:]+?)\s*(?::\s*([^{}=]+?)\s*(?:=\s*([^{}]*?))?)?\s*\}\}/g;
  return text.replace(re, (_, name) => values[name.replace(/\?+$/, "").trim().toLowerCase()] ?? "")
    .replace(/[ \t]+\n/g, "\n")
    .replace(/\n{3,}/g, "\n\n")
    .trim();
}

$("runForm").addEventListener("submit", e => {
  if (e.submitter?.value === "cancel") return;
  e.preventDefault();

  const missing = run.vars.filter((v, i) => !v.optional && !$(`run_${i}`).value.trim());
  if (missing.length) {
    $("runError").textContent = `Bitte noch ausfüllen: ${missing.map(v => v.name).join(", ")}`;
    $("runError").hidden = false;
    return;
  }

  const values = {};
  const fields = [];
  run.vars.forEach((v, i) => {
    const value = formatValue(v, i);
    values[v.name.toLowerCase()] = value;
    if (value) fields.push({ label: v.name, value });
  });

  const text = fillTemplate(run.template.text, values);
  $("runDialog").close();

  if (run.template.mode === "Run") {
    show("chat");
    sendContent(text, { title: run.template.title, fields });
  } else {
    insertIntoPrompt(text);
  }
});

// Die Karte im Chat: Titel der Vorlage und die eingegebenen Werte, nicht die lange Anweisung
function addTemplateCard(display) {
  const div = addMessage("template", "");
  div.innerHTML = `<div class="tpl-head">${icon("doc")}${esc(display.title)}</div>`
    + (display.fields?.length
      ? `<dl class="tpl-fields">${display.fields.map(f => `<dt>${esc(f.label)}</dt><dd>${esc(f.value)}</dd>`).join("")}</dl>`
      : "");
  return div;
}

["ruleForm", "templateForm"].forEach(id =>
  $(id).addEventListener("submit", e => { if (e.submitter?.value !== "cancel") e.preventDefault(); }));

// Benutzer

async function loadUsers() {
  const [settings, stats] = await Promise.all([api("GET", "/admin/settings"), api("GET", "/admin/users/stats")]);
  state.settings = settings;
  state.userStats = stats;
  renderUsers();
}

function renderUsers() {
  const q = $("userSearch").value.trim().toLowerCase();
  const f = state.userFilter;
  const all = state.settings.users;
  const users = all
    .filter(u => f === "all" || (f === "active" && u.active) || (f === "locked" && !u.active) || (f === "admin" && u.admin))
    .filter(u => !q || u.name.toLowerCase().includes(q) || (u.note || "").toLowerCase().includes(q))
    .sort((a, b) => (b.active - a.active) || a.name.localeCompare(b.name, "de"));

  $("userCount").textContent = `${users.length} von ${all.length}`;

  if (!users.length) {
    $("userTable").innerHTML = '<p class="empty-note">Keine Benutzer gefunden.</p>';
    return;
  }

  const perm = (on, ico, label) => `<span class="perm ${on ? "" : "off"}" title="${label}: ${on ? "ja" : "nein"}">${icon(ico)}</span>`;

  $("userTable").innerHTML = `<table>
    <thead><tr><th>Benutzer</th><th>Rolle</th><th>Rechte</th><th class="nowrap">Limit</th><th class="nowrap">Zuletzt aktiv</th><th class="num nowrap">30 Tage</th><th>Status</th><th></th></tr></thead>
    <tbody>
    ${users.map(u => {
      const st = state.userStats[u.name] || {};
      return `<tr data-id="${u.id}" class="${u.active ? "" : "locked"}">
        <td><div class="user-cell"><span class="avatar small ${u.active ? "" : "off"}">${esc(initials(u.name))}</span>
          <div><strong>${esc(u.name)}</strong>${u.id === state.me.id ? ' <span class="muted small">(du)</span>' : ""}
          <div class="sub">${esc(u.username)}${u.hasPassword ? "" : " · nur Schlüssel"}${u.keyHint ? "" : " · ohne Schlüssel"}${u.mustChangePassword ? " · Startpasswort" : ""}${u.note ? " · " + esc(u.note) : ""}</div></div></div></td>
        <td>${u.admin ? '<span class="badge admin">Admin</span>' : '<span class="badge neutral">Mitarbeiter</span>'}</td>
        <td><div class="perm-icons">${perm(u.showPreview, "eye", "Das sieht die KI")}${perm(u.canUseDocuments, "doc", "Dokumente")}${perm(u.cloudAllowed, "cloud", "Cloud erlaubt")}</div></td>
        <td class="nowrap">${u.dailyLimit ? `${st.today || 0} / ${u.dailyLimit}` : '<span class="muted">keins</span>'}</td>
        <td class="nowrap">${relTime(st.lastSeen)}</td>
        <td class="num">${st.month || 0}${st.blocked ? ` <span class="badge off" title="blockiert">${st.blocked}</span>` : ""}</td>
        <td>${u.active ? '<span class="badge ok">aktiv</span>' : '<span class="badge off">gesperrt</span>'}</td>
        <td><button class="icon-btn" data-menu="${u.id}" aria-label="Aktionen">${icon("dots")}</button></td>
      </tr>`;
    }).join("")}
    </tbody></table>`;
}

$("userSearch").addEventListener("input", renderUsers);
$("userFilter").addEventListener("click", e => {
  const b = e.target.closest("button[data-f]");
  if (!b) return;
  state.userFilter = b.dataset.f;
  $("userFilter").querySelectorAll("button").forEach(x => x.classList.toggle("on", x === b));
  renderUsers();
});

function userPayload(u, changes = {}) {
  return {
    name: u.name, admin: u.admin, active: u.active, showPreview: u.showPreview,
    canUseDocuments: u.canUseDocuments, cloudAllowed: u.cloudAllowed, dailyLimit: u.dailyLimit, note: u.note,
    instructions: u.instructions, username: u.username, ...changes
  };
}

// Kleines Menü an einer Tabellenzeile
function openMenu(anchor, items) {
  const m = $("menu");
  m.innerHTML = items.map((it, i) => it === "-" ? "<hr>" :
    `<button data-i="${i}" class="${it.danger ? "danger" : ""}">${icon(it.icon)}${esc(it.label)}${it.active ? `<svg class="ico menu-check"><use href="#i-check"/></svg>` : ""}</button>`).join("");
  m.hidden = false;
  const r = anchor.getBoundingClientRect();
  const mr = m.getBoundingClientRect();
  m.style.left = Math.max(8, Math.min(r.right - mr.width, innerWidth - mr.width - 8)) + "px";
  m.style.top = (r.bottom + mr.height + 6 > innerHeight ? r.top - mr.height - 6 : r.bottom + 6) + "px";
  m.onclick = e => {
    const b = e.target.closest("button[data-i]");
    if (!b) return;
    m.hidden = true;
    items[Number(b.dataset.i)].run();
  };
}

document.addEventListener("click", e => {
  if (!e.target.closest("#menu") && !e.target.closest("[data-menu]")) $("menu").hidden = true;
});
document.addEventListener("keydown", e => { if (e.key === "Escape") $("menu").hidden = true; });

$("userTable").addEventListener("click", e => {
  const btn = e.target.closest("[data-menu]");
  if (!btn) return;
  e.stopPropagation();
  const u = state.settings.users.find(x => x.id === btn.dataset.menu);
  const self = u.id === state.me.id;

  openMenu(btn, [
    { icon: "edit", label: "Bearbeiten", run: () => openUser(u) },
    { icon: "lock", label: "Passwort zurücksetzen", run: () => resetPassword(u) },
    { icon: "key", label: u.keyHint ? "Neuen Schlüssel erzeugen" : "Schlüssel erzeugen", run: () => rotateKey(u) },
    ...(u.keyHint ? [{ icon: "x", label: "Schlüssel entfernen", run: () => removeKey(u) }] : []),
    { icon: "bell", label: "Benachrichtigung senden", run: () => openNotice(null, u.id) },
    "-",
    ...(self ? [] : [
      { icon: "lock", label: u.active ? "Zugang sperren" : "Zugang entsperren", run: () => toggleLock(u) },
      { icon: "trash", label: "Löschen", danger: true, run: () => deleteUser(u) },
    ]),
  ]);
});

async function rotateKey(u) {
  if (!await confirmDialog("Neuen Schlüssel erzeugen?", `Der alte Schlüssel von ${u.name} funktioniert danach nicht mehr.`, "Neu erzeugen")) return;
  try {
    const r = await api("POST", `/admin/users/${u.id}/rotate`);
    showKey(u.name, r.key);
    if (u.id === state.me.id && state.key) {
      state.key = r.key;
      storage("set", r.key);
    }
    loadUsers();
  } catch (err) { toast(err.message, true); }
}

// Ohne Schlüssel geht die Anmeldung nur noch mit Passwort, auch über die API nicht mehr
async function removeKey(u) {
  if (!u.hasPassword) {
    toast(`${u.name} hat noch kein Passwort. Bitte zuerst ein Passwort vergeben, sonst käme ${u.name} nicht mehr rein.`, true);
    return;
  }
  const self = u.id === state.me.id;
  const text = `${u.name} kann sich danach nur noch mit Benutzername und Passwort anmelden. Programme, die den Schlüssel für die API nutzen, funktionieren nicht mehr.`
    + (self && state.key ? " Du bist gerade mit diesem Schlüssel angemeldet und wirst danach abgemeldet." : "");
  if (!await confirmDialog("Schlüssel entfernen?", text, "Entfernen")) return;
  try {
    await api("DELETE", `/admin/users/${u.id}/key`);
    toast("Schlüssel entfernt");
    if (self && state.key) { logout("Schlüssel entfernt, bitte mit Passwort anmelden."); return; }
    loadUsers();
  } catch (err) { toast(err.message, true); }
}

async function toggleLock(u) {
  if (u.active && !await confirmDialog("Zugang sperren?", `${u.name} kann Zwijg danach nicht mehr nutzen. Das Protokoll bleibt erhalten und du kannst jederzeit entsperren.`, "Sperren")) return;
  try {
    await api("PUT", `/admin/users/${u.id}`, userPayload(u, { active: !u.active }));
    toast(u.active ? `${u.name} gesperrt` : `${u.name} entsperrt`);
    loadUsers();
  } catch (err) { toast(err.message, true); }
}

async function deleteUser(u) {
  if (!await confirmDialog("Benutzer löschen?", `${u.name} wird endgültig entfernt. Einträge im Protokoll bleiben erhalten. Tipp: Sperren lässt sich rückgängig machen, Löschen nicht.`, "Löschen")) return;
  try {
    await api("DELETE", `/admin/users/${u.id}`);
    toast("Benutzer gelöscht");
    loadUsers();
  } catch (err) { toast(err.message, true); }
}

$("addUser").addEventListener("click", () => openUser(null));

function openUser(u) {
  state.editingUser = u;
  $("userDialogTitle").textContent = u ? `${u.name} bearbeiten` : "Benutzer anlegen";
  $("uName").value = u?.name ?? "";
  $("uUsername").value = u?.username ?? "";
  $("uPassword").value = u ? "" : generatePassword();
  $("uMustChange").checked = u ? u.mustChangePassword || !u.hasPassword : true;
  $("uPwState").textContent = !u ? "neu" : !u.hasPassword ? "noch keins, nur Schlüssel" : u.mustChangePassword ? "Startpasswort, noch nicht geändert" : "gesetzt";
  $("uPwState").className = "badge " + (u?.hasPassword && !u.mustChangePassword ? "ok" : "neutral");
  $("uLimit").value = u?.dailyLimit ?? "";
  $("uNote").value = u?.note ?? "";
  $("uInstructions").value = u?.instructions ?? "";
  $("uAdmin").checked = u?.admin ?? false;
  $("uPreview").checked = u?.showPreview ?? true;
  $("uDocs").checked = u?.canUseDocuments ?? true;
  $("uCloud").checked = u?.cloudAllowed ?? true;
  $("uActive").checked = u?.active ?? true;
  $("uActiveRow").hidden = !u || u.id === state.me.id;
  $("uError").hidden = true;
  $("userDialog").showModal();
  $("uName").focus();
}

$("uSave").addEventListener("click", async () => {
  const payload = {
    name: $("uName").value.trim(),
    admin: $("uAdmin").checked,
    active: $("uActive").checked,
    showPreview: $("uPreview").checked,
    canUseDocuments: $("uDocs").checked,
    cloudAllowed: $("uCloud").checked,
    dailyLimit: $("uLimit").value ? Number($("uLimit").value) : null,
    note: $("uNote").value.trim() || null,
    instructions: $("uInstructions").value.trim() || null,
    username: $("uUsername").value.trim().toLowerCase() || null,
    password: $("uPassword").value.trim() || null,
    mustChangePassword: $("uMustChange").checked,
  };
  if (!payload.name) { $("uError").textContent = "Bitte einen Namen eingeben."; $("uError").hidden = false; return; }

  try {
    const u = state.editingUser;
    if (u) {
      await api("PUT", `/admin/users/${u.id}`, payload);
      toast("Benutzer gespeichert");
      if (payload.password)
        showCredentials("Neues Passwort gesetzt", "Das Passwort wird nur jetzt angezeigt.",
          [{ label: "Benutzername", value: payload.username || u.username }, { label: "Passwort", value: payload.password }]);
      if (u.id === state.me.id) reloadMe();
    } else {
      const r = await api("POST", "/admin/users", payload);
      $("userDialog").close();
      const created = (await api("GET", "/admin/settings")).users.find(x => x.name === payload.name);
      const items = [];
      if (created) items.push({ label: "Benutzername", value: created.username });
      if (payload.password) items.push({ label: payload.mustChangePassword ? "Startpasswort (muss beim ersten Anmelden geändert werden)" : "Passwort", value: payload.password });
      items.push({ label: "Zugangsschlüssel (für Programme oder als Notzugang)", value: r.key });
      showCredentials(`${payload.name} angelegt`, "Diese Angaben werden nur jetzt angezeigt. Bitte sicher weitergeben, am besten persönlich.", items);
    }
    $("userDialog").close();
    loadUsers();
  } catch (err) {
    $("uError").textContent = err.message;
    $("uError").hidden = false;
  }
});

// Zeigt neue Zugangsdaten genau einmal an, jeder Wert mit eigenem Kopieren Knopf
function showCredentials(title, intro, items) {
  $("credTitle").textContent = title;
  $("credIntro").textContent = intro;
  $("credBoxes").innerHTML = items.map((it, i) => `
    <div class="cred-label">${esc(it.label)}</div>
    <div class="key-box"><code>${esc(it.value)}</code><button class="btn small" type="button" data-copy="${i}">${icon("copy")}Kopieren</button></div>`).join("");
  $("credBoxes").onclick = async e => {
    const b = e.target.closest("[data-copy]");
    if (!b) return;
    try {
      await navigator.clipboard.writeText(items[Number(b.dataset.copy)].value);
      b.innerHTML = icon("check") + "Kopiert";
    } catch { toast("Kopieren nicht möglich, bitte markieren und Strg+C drücken.", true); }
  };
  $("keyDialog").showModal();
}

function showKey(name, key) {
  showCredentials("Neuer Zugangsschlüssel", `Für ${name}. Der Schlüssel wird nur jetzt angezeigt. Er ist für Programme gedacht, die Zwijg direkt nutzen, oder als Notzugang.`,
    [{ label: "Zugangsschlüssel", value: key }]);
}

// Benachrichtigungen

function noticeStatus(a) {
  const now = Date.now();
  if (!a.enabled) return ["aus", "neutral"];
  if (a.startsAt && new Date(a.startsAt) > now) return [`geplant ab ${fmtDateTime(a.startsAt)}`, "admin"];
  if (a.endsAt && new Date(a.endsAt) < now) return ["abgelaufen", "neutral"];
  return ["aktiv", "ok"];
}

async function loadNotices() {
  state.settings = await api("GET", "/admin/settings");
  updateNoticeCount();
  const list = state.settings.announcements;

  if (!list.length) {
    $("noticeList").innerHTML = '<div class="card empty-card">Noch keine Benachrichtigungen. Damit kannst du dem Team Hinweise geben, die oben in der App erscheinen.</div>';
    return;
  }

  const userName = id => state.settings.users.find(u => u.id === id)?.name || "?";
  $("noticeList").innerHTML = list.map(a => {
    const [status, cls] = noticeStatus(a);
    const who = a.audience === "Selected" ? a.userIds.map(userName).join(", ") || "niemand" : AUDIENCES[a.audience];
    return `<div class="notice ${a.level} ${cls === "ok" ? "" : "inactive"}" data-id="${a.id}">
      ${icon(a.level === "Info" ? "info" : "alert")}
      <div>
        <div class="notice-title">${esc(a.title || "(ohne Titel)")}</div>
        ${a.message ? `<div class="notice-text">${esc(a.message)}</div>` : ""}
        <div class="notice-meta">
          <span class="badge ${cls}">${esc(status)}</span>
          <span class="badge neutral">${esc(LEVELS[a.level])}</span>
          <span class="badge neutral">${icon("users", "ico inline")}${esc(who)}</span>
          ${a.endsAt ? `<span class="badge neutral">bis ${fmtDateTime(a.endsAt)}</span>` : ""}
          <span class="badge neutral">${a.dismissible ? "wegklickbar" : "bleibt stehen"}</span>
          <span class="muted small">von ${esc(a.createdBy)}, ${relTime(a.created)}</span>
        </div>
      </div>
      <div class="notice-actions">
        <button class="icon-btn" data-act="edit" title="Bearbeiten">${icon("edit")}</button>
        ${a.dismissible ? `<button class="icon-btn" data-act="reset" title="Allen erneut zeigen">${icon("refresh")}</button>` : ""}
        <button class="icon-btn" data-act="delete" title="Löschen">${icon("trash")}</button>
      </div>
    </div>`;
  }).join("");
}

$("noticeList").addEventListener("click", async e => {
  const btn = e.target.closest("button[data-act]");
  if (!btn) return;
  const id = btn.closest(".notice").dataset.id;
  const a = state.settings.announcements.find(x => x.id === id);

  try {
    if (btn.dataset.act === "edit") openNotice(a);
    if (btn.dataset.act === "reset") {
      await api("POST", `/admin/announcements/${id}/reset`);
      toast("Wird allen erneut angezeigt");
    }
    if (btn.dataset.act === "delete") {
      if (!await confirmDialog("Benachrichtigung löschen?", `"${a.title || a.message}" verschwindet für alle.`, "Löschen")) return;
      await api("DELETE", `/admin/announcements/${id}`);
      toast("Benachrichtigung gelöscht");
      loadNotices();
      reloadMe();
    }
  } catch (err) { toast(err.message, true); }
});

$("addNotice").addEventListener("click", () => openNotice(null));

async function openNotice(a, onlyUserId) {
  if (!state.settings) state.settings = await api("GET", "/admin/settings");
  state.editingNotice = a;
  $("noticeDialogTitle").textContent = a ? "Benachrichtigung bearbeiten" : "Neue Benachrichtigung";
  $("nTitle").value = a?.title ?? "";
  $("nMessage").value = a?.message ?? "";
  setNoticeLevel(a?.level ?? "Info");
  $("nAudience").value = a?.audience ?? (onlyUserId ? "Selected" : "All");
  $("nStart").value = toLocalInput(a?.startsAt);
  $("nEnd").value = toLocalInput(a?.endsAt);
  $("nDismissible").checked = a?.dismissible ?? true;
  $("nEnabled").checked = a?.enabled ?? true;

  const picked = new Set(a?.userIds ?? (onlyUserId ? [onlyUserId] : []));
  $("nUsers").innerHTML = state.settings.users.map(u =>
    `<label><input type="checkbox" value="${u.id}" ${picked.has(u.id) ? "checked" : ""}>${esc(u.name)}</label>`).join("");
  $("nError").hidden = true;
  syncNoticeForm();
  $("noticeDialog").showModal();
}

function setNoticeLevel(level) {
  state.noticeLevel = level;
  $("nLevel").querySelectorAll("button").forEach(b => b.classList.toggle("on", b.dataset.l === level));
}

function syncNoticeForm() {
  $("nUsers").hidden = $("nAudience").value !== "Selected";
  $("nPreview").innerHTML = bannerHtml({
    title: $("nTitle").value.trim() || "Titel", message: $("nMessage").value.trim(),
    level: state.noticeLevel, dismissible: $("nDismissible").checked, id: "preview"
  }, true);
}

$("nLevel").addEventListener("click", e => {
  const b = e.target.closest("button[data-l]");
  if (b) { setNoticeLevel(b.dataset.l); syncNoticeForm(); }
});
["nTitle", "nMessage", "nAudience", "nDismissible"].forEach(id => $(id).addEventListener("input", syncNoticeForm));
$("nAudience").addEventListener("change", syncNoticeForm);

$("nSave").addEventListener("click", async () => {
  const payload = {
    title: $("nTitle").value.trim(),
    message: $("nMessage").value.trim(),
    level: state.noticeLevel,
    audience: $("nAudience").value,
    userIds: [...$("nUsers").querySelectorAll("input:checked")].map(i => i.value),
    startsAt: fromLocalInput($("nStart").value),
    endsAt: fromLocalInput($("nEnd").value),
    dismissible: $("nDismissible").checked,
    enabled: $("nEnabled").checked,
  };

  if (payload.audience === "Selected" && !payload.userIds.length) {
    $("nError").textContent = "Bitte mindestens eine Person auswählen.";
    $("nError").hidden = false;
    return;
  }

  try {
    const a = state.editingNotice;
    if (a) await api("PUT", `/admin/announcements/${a.id}`, payload);
    else await api("POST", "/admin/announcements", payload);
    $("noticeDialog").close();
    toast("Benachrichtigung gespeichert");
    if (!$("view-notices").hidden) loadNotices();
    else state.settings = await api("GET", "/admin/settings");
    updateNoticeCount();
    reloadMe();
  } catch (err) {
    $("nError").textContent = err.message;
    $("nError").hidden = false;
  }
});

// Protokoll

const STATUS = {
  Ok: { label: "Erfolgreich", cls: "ok", icon: "check" },
  Blocked: { label: "Blockiert", cls: "off", icon: "alert" },
  Warning: { label: "Warnung", cls: "warn", icon: "alert" },
  Error: { label: "Fehler", cls: "warn", icon: "plug" },
  admin: { label: "Verwaltung", cls: "neutral", icon: "edit" },
};

const ENTITY_LABELS = {
  NAME: "Name", GEBURTSDATUM: "Geburtsdatum", VERSICHERTENNR: "Versichertennr.", IBAN: "IBAN", EMAIL: "E-Mail",
  TELEFON: "Telefon", ADRESSE: "Adresse", ORT: "Ort", DATUM: "Datum", GESUNDHEIT: "Gesundheit"
};

const audit = { range: "7", page: 0, pageSize: 50, total: 0, items: [], index: -1, timer: null, detailId: null };

function auditFilters() {
  const f = {};
  const today = new Date();
  today.setHours(0, 0, 0, 0);

  if (audit.range === "today") f.from = today.toISOString();
  if (audit.range === "7") f.from = new Date(today.getTime() - 6 * 864e5).toISOString();
  if (audit.range === "30") f.from = new Date(today.getTime() - 29 * 864e5).toISOString();
  if (audit.range === "custom") {
    if ($("auditFrom").value) f.from = new Date($("auditFrom").value + "T00:00:00").toISOString();
    if ($("auditTo").value) f.to = new Date($("auditTo").value + "T23:59:59.999").toISOString();
  }

  const q = $("auditSearch").value.trim();
  if (q) f.q = q;
  if ($("auditUser").value) f.user = $("auditUser").value;
  if ($("auditAction").value) f.action = $("auditAction").value;
  if ($("auditStatus").value) f.status = $("auditStatus").value;
  if ($("auditRoute").value) f.route = $("auditRoute").value;
  return f;
}

function auditParams(extra = {}) {
  return new URLSearchParams({ ...auditFilters(), ...extra }).toString();
}

async function loadAudit() {
  if (!state.settings) state.settings = await api("GET", "/admin/settings");
  const current = $("auditUser").value;
  $("auditUser").innerHTML = '<option value="">Alle</option>' +
    state.settings.users.map(u => `<option ${u.name === current ? "selected" : ""}>${esc(u.name)}</option>`).join("");

  const res = await api("GET", "/admin/audit?" + auditParams({ limit: audit.pageSize, offset: audit.page * audit.pageSize }));
  audit.items = res.items;
  audit.total = res.total;
  renderAudit();
}

// Suchbegriffe im Text markieren, nach dem Escapen, damit nichts Fremdes als HTML durchkommt
function highlight(text) {
  let html = esc(text);
  const words = $("auditSearch").value.trim().split(/\s+/).filter(w => w.length >= 2 && !w.startsWith("#"));
  for (const w of words) {
    const safe = esc(w).replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
    html = html.replace(new RegExp(`(${safe})(?![^<]*>)`, "gi"), "<mark class=\"hit\">$1</mark>");
  }
  return html;
}

function statusOf(r) {
  return STATUS[r.status] || STATUS.admin;
}

function statusBadge(r) {
  const s = statusOf(r);
  return `<span class="badge ${s.cls}">${icon(s.icon)}${s.label}</span>`;
}

function routeTag(r) {
  if (r.route === "Local") return `<span class="route-tag local">${icon("lock")}lokal</span>`;
  if (r.route === "Cloud") return `<span class="route-tag cloud">${icon("cloud")}Cloud</span>`;
  if (r.route === "External") return `<span class="route-tag external">${icon("copy")}extern</span>`;
  return '<span class="muted">keins</span>';
}

function entityText(entities) {
  if (!entities) return "";
  return entities.split(",").map(p => {
    const [k, v] = p.trim().split(":");
    return `${ENTITY_LABELS[k] || k} ${v}`;
  }).join(" · ");
}

// Was in der letzten Spalte steht: bei Anfragen die Frage, sonst der Grund
function mainText(r) {
  return r.action === "admin" || r.blocked || !r.prompt ? (r.reason || "") : r.prompt;
}

function renderAudit() {
  const f = auditFilters();
  const active = ["q", "user", "action", "status", "route"].filter(k => f[k]).length + (audit.range !== "7" ? 1 : 0);
  $("auditReset").hidden = active === 0;
  ["auditUser", "auditAction", "auditStatus", "auditRoute"].forEach(id =>
    $(id).closest(".select-field").classList.toggle("set", !!$(id).value));
  $("auditSearchClear").hidden = !$("auditSearch").value;

  const from = audit.total ? audit.page * audit.pageSize + 1 : 0;
  const to = audit.page * audit.pageSize + audit.items.length;
  $("auditCount").innerHTML = audit.total
    ? `<strong>${audit.total.toLocaleString("de-DE")}</strong> ${audit.total === 1 ? "Eintrag" : "Einträge"}${active ? " mit diesen Filtern" : ""}`
    : "";

  if (!audit.items.length) {
    $("auditTable").innerHTML = `<div class="audit-empty">${icon("search")}<p>Keine Einträge gefunden.${active ? " Vielleicht einen Filter lockern?" : ""}</p></div>`;
    $("auditPager").innerHTML = "";
    return;
  }

  $("auditTable").innerHTML = `<table class="audit-table">
    <colgroup>
      <col style="width:132px"><col style="width:120px"><col style="width:100px"><col style="width:132px">
      <col style="width:124px"><col style="width:172px"><col>
    </colgroup>
    <thead><tr><th>Zeit</th><th>Benutzer</th><th>Art</th><th>Status</th><th>Ziel</th><th>Erkannt</th><th>Frage oder Grund</th></tr></thead>
    <tbody>
    ${audit.items.map((r, i) => {
      const text = mainText(r);
      const model = (r.model || "").replace(/\s*\(.*\)$/, "");
      return `<tr data-i="${i}" tabindex="0" class="${r.id === audit.detailId ? "selected" : ""}">
        <td class="cell-two"><div class="cell-main">${esc(fmtDateTime(r.timestamp))}</div><div class="cell-sub">${relTime(r.timestamp)}</div></td>
        <td title="${esc(r.user)}">${highlight(r.user)}</td>
        <td>${esc(ACTIONS[r.action] || r.action)}</td>
        <td>${statusBadge(r)}</td>
        <td class="cell-two" title="${esc(r.model || "")}"><div class="cell-main">${routeTag(r)}</div><div class="cell-sub">${esc(model)}</div></td>
        <td title="${esc(entityText(r.entities))}">${entityText(r.entities) ? highlight(entityText(r.entities)) : '<span class="muted">nichts</span>'}</td>
        <td title="${esc(text)}">${highlight(text) || '<span class="muted">kein Text gespeichert</span>'}</td>
      </tr>`;
    }).join("")}
    </tbody></table>`;

  const pages = Math.max(1, Math.ceil(audit.total / audit.pageSize));
  $("auditPager").innerHTML = pages > 1 ? `
    <span>${from} bis ${to} von ${audit.total.toLocaleString("de-DE")}</span>
    <span class="head-actions">
      <button class="btn small ghost" data-page="-1" ${audit.page === 0 ? "disabled" : ""}>${icon("up", "ico")}Neuere</button>
      <span>Seite ${audit.page + 1} von ${pages}</span>
      <button class="btn small ghost" data-page="1" ${audit.page >= pages - 1 ? "disabled" : ""}>Ältere${icon("down", "ico")}</button>
    </span>` : "";
}

function reloadAudit(resetPage = true) {
  if (resetPage) audit.page = 0;
  loadAudit().catch(e => toast(e.message, true));
}

$("auditSearch").addEventListener("input", () => {
  $("auditSearchClear").hidden = !$("auditSearch").value;
  clearTimeout(audit.timer);
  audit.timer = setTimeout(reloadAudit, 300);
});
$("auditSearchClear").addEventListener("click", () => { $("auditSearch").value = ""; reloadAudit(); $("auditSearch").focus(); });
["auditUser", "auditAction", "auditStatus", "auditRoute", "auditFrom", "auditTo"].forEach(id => $(id).addEventListener("change", () => reloadAudit()));
$("auditLoad").addEventListener("click", () => reloadAudit(false));

function setAuditRange(range) {
  audit.range = range;
  $("auditRange").querySelectorAll("button").forEach(b => b.classList.toggle("on", b.dataset.r === range));
  $("auditCustomRange").hidden = range !== "custom";
}

$("auditRange").addEventListener("click", e => {
  const b = e.target.closest("button[data-r]");
  if (!b) return;
  setAuditRange(b.dataset.r);
  reloadAudit();
});

$("auditReset").addEventListener("click", () => {
  $("auditSearch").value = "";
  ["auditUser", "auditAction", "auditStatus", "auditRoute", "auditFrom", "auditTo"].forEach(id => $(id).value = "");
  setAuditRange("7");
  reloadAudit();
});

$("auditPager").addEventListener("click", e => {
  const b = e.target.closest("button[data-page]");
  if (!b) return;
  audit.page += Number(b.dataset.page);
  reloadAudit(false);
  $("auditTable").scrollIntoView({ behavior: "smooth", block: "start" });
});

$("auditTable").addEventListener("click", e => {
  const tr = e.target.closest("tr[data-i]");
  if (tr) openAuditDetail(Number(tr.dataset.i));
});
$("auditTable").addEventListener("keydown", e => {
  const tr = e.target.closest("tr[data-i]");
  if (tr && (e.key === "Enter" || e.key === " ")) { e.preventDefault(); openAuditDetail(Number(tr.dataset.i)); }
});

// Von außen, z.B. aus der Übersicht: Filter setzen und Protokoll öffnen
function showAuditWith({ status = "", range = "7", user = "" } = {}) {
  $("auditSearch").value = "";
  ["auditAction", "auditRoute", "auditFrom", "auditTo"].forEach(id => $(id).value = "");
  $("auditStatus").value = status;
  setAuditRange(range);
  audit.page = 0;
  show("audit");
  if (user) {
    // Die Benutzerliste wird beim Laden neu gebaut, daher danach setzen
    const opt = document.createElement("option");
    opt.textContent = user;
    $("auditUser").appendChild(opt);
    $("auditUser").value = user;
  }
}

// Detailansicht

async function openAuditDetail(index) {
  const r = audit.items[index];
  if (!r) return;
  audit.index = index;
  await showDetail(r.id);
}

async function showDetail(id) {
  audit.detailId = id;
  document.querySelectorAll(".audit-table tr.selected").forEach(tr => tr.classList.remove("selected"));
  document.querySelector(`.audit-table tr[data-i="${audit.items.findIndex(x => x.id === id)}"]`)?.classList.add("selected");

  let d;
  try {
    d = await api("GET", `/admin/audit/${id}`);
  } catch (e) { toast(e.message, true); return; }

  const r = d.entry;
  const s = statusOf(r);
  const inList = audit.items.findIndex(x => x.id === id);
  audit.index = inList;

  $("drawerTitle").textContent = `Eintrag #${r.id}`;
  $("drawerPos").textContent = inList >= 0 ? `${audit.page * audit.pageSize + inList + 1} von ${audit.total.toLocaleString("de-DE")}` : "";
  $("drawerPrev").disabled = inList <= 0;
  $("drawerNext").disabled = inList < 0 || inList >= audit.items.length - 1;

  const entities = (r.entities || "").split(",").map(p => p.trim()).filter(Boolean).map(p => {
    const [k, v] = p.split(":");
    return chip(`${ENTITY_LABELS[k] || k} × ${v}`, k === "GESUNDHEIT" ? "amber" : "", k === "GESUNDHEIT" ? "steth" : "");
  }).join("");

  const prompt = r.prompt
    ? highlight(r.prompt).replace(/\[[A-Z]+_\d+\]/g, m => `<mark class="ph">${m}</mark>`)
    : "";

  const intact = d.hashValid && d.chainValid;
  const integrity = intact
    ? "Der Eintrag ist unverändert und passt lückenlos an seinen Vorgänger."
    : !d.hashValid
      ? "Der Inhalt dieses Eintrags wurde nachträglich verändert."
      : "Die Verkettung zum vorherigen Eintrag passt nicht. Davor wurde vermutlich etwas gelöscht oder verändert.";

  const fact = (label, value, wide = false) => `<div class="fact ${wide ? "wide" : ""}"><dt>${label}</dt><dd>${value}</dd></div>`;

  $("drawerBody").innerHTML = `
    <div class="drawer-status">
      <span class="badge ${s.cls}">${icon(s.icon)}${s.label}</span>
      <span class="muted">${esc(new Date(r.timestamp).toLocaleString("de-DE", { dateStyle: "full", timeStyle: "medium" }))}</span>
    </div>

    <dl class="facts">
      ${fact("Benutzer", `${esc(r.user)}<button class="link" data-filter-user="${esc(r.user)}">nur diesen zeigen</button>`)}
      ${fact("Art", esc(ACTIONS[r.action] || r.action))}
      ${r.action !== "admin" ? `
        ${fact("Ziel", routeTag(r))}
        ${fact("Sensibilität", esc(SENSITIVITY[r.sensitivity] ?? r.sensitivity ?? "keine"))}
        ${fact("Modell und Verbindung", esc(r.model || "keins"), true)}
        ${fact("Dauer", r.durationMs ? `${(r.durationMs / 1000).toLocaleString("de-DE", { maximumFractionDigits: 2 })} s` : "unter 1 ms")}
        ${fact("Antwortlänge", r.responseLength ? `${r.responseLength.toLocaleString("de-DE")} Zeichen` : "keine Antwort")}
        ${fact("Manipulationsverdacht", r.injectionScore ? `Punktzahl ${r.injectionScore}` : "keiner")}
        ${fact("Eintrag", `#${r.id}`)}` : ""}
    </dl>

    ${r.action !== "admin" ? `
    <div class="drawer-section">
      <h3>${icon("shield", "ico inline")}Erkannte und ersetzte Daten</h3>
      <div class="chips" style="margin:0">${entities || '<span class="muted">Keine Personendaten erkannt.</span>'}</div>
    </div>` : ""}

    ${r.prompt ? `
    <div class="drawer-section">
      <h3>${icon("chat", "ico inline")}Frage, wie die KI sie gesehen hat<span class="spacer"></span>
        <button class="btn small ghost" id="copyPrompt">${icon("copy")}Kopieren</button></h3>
      <div class="text-block">${prompt}</div>
    </div>` : ""}

    ${r.reason ? `
    <div class="drawer-section">
      <h3>${icon(r.blocked ? "alert" : "info", "ico inline")}${r.action === "admin" ? "Änderung" : "Grund"}</h3>
      <div class="text-block">${highlight(r.reason)}</div>
    </div>` : ""}

    <div class="drawer-section">
      <h3>${icon("lock", "ico inline")}Echtheit</h3>
      <div class="integrity ${intact ? "ok" : "bad"}">
        ${icon(intact ? "check" : "alert")}
        <div style="min-width:0">
          <strong>${intact ? "Unverändert" : "Auffällig"}</strong>
          <div class="muted small">${integrity}</div>
          <div class="hash" title="${esc(r.hash)}">Hash ${esc(r.hash)}</div>
          <div class="hash" title="${esc(r.previousHash)}">Vorgänger ${esc(r.previousHash || "keiner, erster Eintrag")}</div>
        </div>
      </div>
    </div>`;

  $("drawerBackdrop").hidden = false;
  $("drawer").hidden = false;
  $("drawer").focus();
  $("drawerBody").scrollTop = 0;
}

function closeDetail() {
  $("drawer").hidden = true;
  $("drawerBackdrop").hidden = true;
  document.querySelector(".audit-table tr.selected")?.focus();
}

function stepDetail(delta) {
  const i = audit.index + delta;
  if (i >= 0 && i < audit.items.length) openAuditDetail(i);
}

$("drawerClose").addEventListener("click", closeDetail);
$("drawerBackdrop").addEventListener("click", closeDetail);
$("drawerPrev").addEventListener("click", () => stepDetail(-1));
$("drawerNext").addEventListener("click", () => stepDetail(1));
$("drawer").addEventListener("keydown", e => {
  if (e.key === "Escape") closeDetail();
  if (e.key === "ArrowUp" || e.key === "k") { e.preventDefault(); stepDetail(-1); }
  if (e.key === "ArrowDown" || e.key === "j") { e.preventDefault(); stepDetail(1); }
});

$("drawerBody").addEventListener("click", async e => {
  const u = e.target.closest("[data-filter-user]");
  if (u) {
    closeDetail();
    $("auditUser").value = u.dataset.filterUser;
    reloadAudit();
  }
  if (e.target.closest("#copyPrompt")) {
    const r = await api("GET", `/admin/audit/${audit.detailId}`);
    try {
      await navigator.clipboard.writeText(r.entry.prompt || "");
      toast("Kopiert");
    } catch { toast("Kopieren nicht möglich", true); }
  }
});

$("auditVerify").addEventListener("click", async () => {
  try {
    const v = await api("GET", "/admin/audit/verify");
    $("verifyResult").innerHTML = v.ok
      ? chip(`Alle ${v.checked.toLocaleString("de-DE")} Einträge geprüft, nichts verändert`, "ok", "check")
      : chip(v.problem || `Eintrag #${v.brokenAtId} wurde nachträglich verändert oder gelöscht`, "warn", "alert");
  } catch (e) { toast(e.message, true); }
});

$("auditCsv").addEventListener("click", async () => {
  try {
    const res = await api("GET", "/admin/audit/export.csv?" + auditParams(), undefined, { raw: true });
    const url = URL.createObjectURL(await res.blob());
    Object.assign(document.createElement("a"), { href: url, download: "zwijg-protokoll.csv" }).click();
    URL.revokeObjectURL(url);
    toast(`${audit.total.toLocaleString("de-DE")} Einträge exportiert`);
  } catch (e) { toast(e.message, true); }
});

// Start

resume();

// Übersicht mit dem Protokoll verbinden
$("tiles").addEventListener("click", e => {
  if (e.target.closest(".tile.clickable")) showAuditWith({ status: "Blocked", range: "7" });
});

$("dashEvents").addEventListener("click", async e => {
  const ev = e.target.closest("[data-audit-id]");
  if (!ev) return;
  showAuditWith({ range: "7" });
  await loadAudit().catch(() => {});
  showDetail(Number(ev.dataset.auditId));
});

// Vorlagen im Chat: mit Variablen erst das Formular, dann ausführen oder einfügen
$("templateBar").addEventListener("click", e => {
  const b = e.target.closest("[data-template]");
  if (!b) return;
  const t = state.me.templates.find(x => x.id === b.dataset.template);
  const vars = parseVars(t.text);

  if (vars.length) openRunDialog(t, vars);
  else if (t.mode === "Run") sendContent(t.text.trim(), { title: t.title, fields: [] });
  else insertIntoPrompt(t.text);
});

// Steht schon Text im Feld, kommt er hinter die Vorlage
function insertIntoPrompt(text) {
  const current = $("prompt").value.trim();
  $("prompt").value = text.trimEnd() + NL + (current ? NL + current : "");
  $("prompt").focus();
  $("prompt").setSelectionRange($("prompt").value.length, $("prompt").value.length);
  autoGrow();
  preview();
}

// Verlauf: letzte Unterhaltungen in der Seitenleiste

const RECENT_COUNT = 5;

function historyEnabled() {
  return !!state.me?.history?.enabled;
}

function convCollapsed() {
  try { return localStorage.getItem("zwijg.convCollapsed") === "1"; } catch { return false; }
}

function applyHistory() {
  const on = historyEnabled();
  const collapsed = convCollapsed();
  $("convSection").hidden = !on;
  $("convToggle").setAttribute("aria-expanded", String(!collapsed));
  $("convList").hidden = collapsed;
  if (on && !state.convLoaded) {
    state.convLoaded = true;
    loadConversations();
  }
}

$("convToggle").addEventListener("click", () => {
  const collapsed = !convCollapsed();
  try { localStorage.setItem("zwijg.convCollapsed", collapsed ? "1" : "0"); } catch { /* dann eben nicht merken */ }
  applyHistory();
});

async function loadConversations() {
  if (!historyEnabled()) return;
  try {
    const r = await api("GET", "/v1/conversations?limit=100");
    state.convs = r.items;
    state.convTotal = r.total;
    renderConversations();
  } catch { /* Liste ist nicht kritisch */ }
}

function renderConversations() {
  if (!historyEnabled()) return;
  const pinned = state.convs.filter(c => c.pinned);
  const recent = state.convs.filter(c => !c.pinned);
  const shown = state.convShowAll ? recent : recent.slice(0, RECENT_COUNT);

  // Nur Symbole, die etwas bedeuten: Pin bei angepinnten, sonst bleibt die Spalte leer
  const item = c => `<div class="conv-item ${c.id === state.conversationId ? "active" : ""}" data-conv="${c.id}" tabindex="0"
      title="${esc(c.title)}, ${relTime(c.updated)}">
      <span class="conv-lead">${c.pinned ? icon("pin") : ""}</span>
      <span class="conv-title">${esc(c.title)}</span>
      <span class="conv-time">${shortTime(c.updated)}</span>
      <button class="conv-dots" data-conv-menu="${c.id}" aria-label="Aktionen">${icon("dots")}</button>
    </div>`;

  let html = "";
  if (pinned.length) html += '<div class="conv-label">Angepinnt</div>' + pinned.map(item).join("");
  // "Zuletzt" nur als Trenner, wenn es darüber auch angepinnte gibt
  if (recent.length) html += (pinned.length ? '<div class="conv-label">Zuletzt</div>' : "") + shown.map(item).join("");
  if (recent.length > RECENT_COUNT)
    html += `<button class="conv-more" data-conv-more>${state.convShowAll ? "Weniger anzeigen" : `Mehr anzeigen (${recent.length - RECENT_COUNT})`}</button>`;
  if (!state.convs.length) html += '<div class="conv-empty">Noch keine gespeicherten Unterhaltungen.</div>';

  $("convList").innerHTML = html;
}

async function openConversation(id) {
  try {
    const c = await api("GET", `/v1/conversations/${id}`);
    state.conversationId = c.id;
    state.secrets = c.secrets || [];
    renderSecrets();
    state.history = c.messages.map(m => ({ role: m.role, content: m.content }));
    show("chat");
    $("chat").innerHTML = "";
    if (!state.history.length) emptyChat();
    state.history = c.messages.map(m => m.display
      ? { role: m.role, content: m.content, zwijg_display: m.display }
      : { role: m.role, content: m.content });
    for (const m of state.history) {
      if (m.zwijg_display) addTemplateCard(m.zwijg_display);
      else addMessage(m.role === "user" ? "user" : "assistant", m.content);
    }
    renderConversations();
    $("prompt").focus();
  } catch (err) {
    toast(err.message, true);
    loadConversations();
  }
}

function conversationMenu(anchor, id) {
  const c = state.convs.find(x => x.id === id);
  if (!c) return;
  openMenu(anchor, [
    { icon: "pin", label: c.pinned ? "Lösen" : "Anpinnen", run: () => updateConversation(id, { pinned: !c.pinned }) },
    { icon: "edit", label: "Umbenennen", run: () => renameConversation(c) },
    "-",
    { icon: "trash", label: "Löschen", danger: true, run: () => deleteConversation(c) },
  ]);
}

async function updateConversation(id, changes) {
  try {
    await api("PUT", `/v1/conversations/${id}`, changes);
    await loadConversations();
  } catch (err) { toast(err.message, true); }
}

async function renameConversation(c) {
  const title = await promptDialog("Unterhaltung umbenennen", c.title);
  if (title && title !== c.title) updateConversation(c.id, { title });
}

async function deleteConversation(c) {
  if (!c || !await confirmDialog("Unterhaltung löschen?", `"${c.title}" wird endgültig gelöscht.`, "Löschen")) return;
  try {
    await api("DELETE", `/v1/conversations/${c.id}`);
    if (state.conversationId === c.id) newConversation();
    await loadConversations();
    toast("Unterhaltung gelöscht");
  } catch (err) { toast(err.message, true); }
}

$("convList").addEventListener("click", e => {
  const dots = e.target.closest("[data-conv-menu]");
  if (dots) { e.stopPropagation(); conversationMenu(dots, dots.dataset.convMenu); return; }
  if (e.target.closest("[data-conv-more]")) { state.convShowAll = !state.convShowAll; renderConversations(); return; }
  if (e.target.closest("[data-conv-new]")) { show("chat"); newConversation(); return; }
  const item = e.target.closest("[data-conv]");
  if (item) openConversation(item.dataset.conv);
});

// Rechtsklick öffnet dasselbe Menü wie die drei Punkte
$("convList").addEventListener("contextmenu", e => {
  const item = e.target.closest("[data-conv]");
  if (!item) return;
  e.preventDefault();
  conversationMenu(item.querySelector("[data-conv-menu]") || item, item.dataset.conv);
});

$("convList").addEventListener("keydown", e => {
  const item = e.target.closest("[data-conv], [data-conv-new]");
  if (!item || e.target.closest("button")) return;
  if (e.key === "Enter") item.click();
  if (e.key === "Delete" && item.dataset.conv) deleteConversation(state.convs.find(c => c.id === item.dataset.conv));
});

// Einfacher Eingabedialog, weil prompt() im Browser nicht ins Design passt
function promptDialog(title, value) {
  $("promptTitle").textContent = title;
  $("promptInput").value = value ?? "";
  const d = $("promptDialog");
  d.returnValue = "";
  d.showModal();
  $("promptInput").select();
  return new Promise(resolve => d.addEventListener("close",
    () => resolve(d.returnValue === "yes" ? $("promptInput").value.trim() : null), { once: true }));
}

$("promptForm").addEventListener("submit", e => {
  if (e.submitter?.value === "yes" && !$("promptInput").value.trim()) e.preventDefault();
});

// Verlauf in den Regeln (Admin)

async function loadHistorySettings() {
  const h = await api("GET", "/admin/history");
  $("hEnabled").checked = h.options.enabled;
  $("hMax").value = h.options.maxConversations;
  $("hDays").value = h.options.retentionDays;
  $("hPinned").value = h.options.maxPinned;
  $("hGrid").classList.toggle("off", !h.options.enabled);
  $("hStats").textContent = h.conversations
    ? `Aktuell gespeichert: ${h.conversations} ${h.conversations === 1 ? "Unterhaltung" : "Unterhaltungen"} von ${h.users} ${h.users === 1 ? "Person" : "Personen"}.`
    : "Aktuell sind keine Unterhaltungen gespeichert.";
}

$("hEnabled").addEventListener("change", () => $("hGrid").classList.toggle("off", !$("hEnabled").checked));

$("hSave").addEventListener("click", async () => {
  const enabled = $("hEnabled").checked;
  if (!enabled && !await confirmDialog("Verlauf ausschalten?", "Alle gespeicherten Unterhaltungen aller Benutzer werden dabei gelöscht.", "Ausschalten und löschen")) return;
  try {
    await api("PUT", "/admin/history", {
      enabled,
      maxConversations: Number($("hMax").value),
      retentionDays: Number($("hDays").value),
      maxPinned: Number($("hPinned").value),
    });
    toast("Verlauf gespeichert");
    await loadHistorySettings();
    await reloadMe();
    state.convLoaded = false;
    applyHistory();
  } catch (err) { toast(err.message, true); }
});

$("hDeleteAll").addEventListener("click", async () => {
  if (!await confirmDialog("Alle Verläufe löschen?", "Die gespeicherten Unterhaltungen aller Benutzer werden endgültig gelöscht, auch angepinnte.", "Alles löschen")) return;
  try {
    const r = await api("DELETE", "/admin/conversations");
    toast(`${r.deleted} Unterhaltungen gelöscht`);
    newConversation();
    await loadHistorySettings();
    loadConversations();
  } catch (err) { toast(err.message, true); }
});

// Enter im Eingabedialog speichert, statt den ersten Knopf (Abbrechen) auszulösen
$("promptInput").addEventListener("keydown", e => {
  if (e.key === "Enter" && $("promptInput").value.trim()) {
    e.preventDefault();
    $("promptDialog").close("yes");
  }
});

// Kurze Zeitangabe für die Seitenleiste: "jetzt", "12 Min.", "3 Std.", "gestern", "Mo", "12.09."
function shortTime(iso) {
  const d = new Date(iso);
  const min = Math.round((Date.now() - d) / 60000);
  if (min < 1) return "jetzt";
  if (min < 60) return `${min} Min.`;
  const today = new Date(); today.setHours(0, 0, 0, 0);
  if (d >= today) return `${Math.round(min / 60)} Std.`;
  const days = Math.floor((today - d) / 864e5) + 1;
  if (days === 1) return "gestern";
  if (days < 7) return d.toLocaleDateString("de-DE", { weekday: "short" }).replace(".", "");
  return d.toLocaleDateString("de-DE", { day: "2-digit", month: "2-digit" });
}

// Enter in einem Feld führt aus, statt den ersten Knopf im Formular (Schließen) auszulösen
$("runForm").addEventListener("keydown", e => {
  if (e.key === "Enter" && e.target.tagName === "INPUT") {
    e.preventDefault();
    $("runForm").requestSubmit($("runGo"));
  }
});

$("convNew").addEventListener("click", () => {
  show("chat");
  newConversation();
});

async function resetPassword(u) {
  if (!await confirmDialog("Passwort zurücksetzen?", `${u.name} bekommt ein neues Startpasswort und wird überall abgemeldet. Beim nächsten Anmelden muss ein eigenes Passwort festgelegt werden.`, "Zurücksetzen")) return;
  try {
    const r = await api("POST", `/admin/users/${u.id}/password-reset`);
    showCredentials("Neues Startpasswort", `Für ${u.name}. Wird nur jetzt angezeigt und muss beim Anmelden geändert werden.`,
      [{ label: "Benutzername", value: u.username }, { label: "Startpasswort", value: r.password }]);
    loadUsers();
  } catch (err) { toast(err.message, true); }
}

// Gut lesbares Startpasswort ohne verwechselbare Zeichen, z.B. "kT7m-Qx3p-Hn8w"
function generatePassword() {
  const chars = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
  // Zahlen über dem letzten vollen Vielfachen verwerfen, sonst kämen manche Zeichen minimal öfter vor
  const limit = Math.floor(0x100000000 / chars.length) * chars.length;
  const pick = () => {
    let n;
    do n = crypto.getRandomValues(new Uint32Array(1))[0]; while (n >= limit);
    return chars[n % chars.length];
  };
  return [0, 1, 2].map(() => Array.from({ length: 4 }, pick).join("")).join("-");
}

$("uPwGenerate").addEventListener("click", () => {
  $("uPassword").value = generatePassword();
  $("uMustChange").checked = true;
});
