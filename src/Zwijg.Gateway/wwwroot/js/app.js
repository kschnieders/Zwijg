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

const ACTIONS = { chat: "Chat", document: "Dokument", protect: "Text schützen", dictation: "Diktat", files: "Dateien", admin: "Verwaltung", login: "Anmeldung" };
const SENSITIVITY = { None: "keine", Low: "niedrig", Medium: "mittel", High: "hoch" };
const MODES = { Auto: "Automatisch", LocalOnly: "Nur lokal", CloudOnly: "Nur Cloud" };
const LEVELS = { Info: "Info", Warning: "Hinweis", Critical: "Wichtig" };
const AUDIENCES = { All: "Alle", Staff: "Nur Mitarbeiter", Admins: "Nur Admins", Selected: "Bestimmte Personen" };
const NL = String.fromCharCode(10);

const state = {
  key: "", me: null, meJson: "", settings: null, history: [], editing: null, preset: null,
  previewTimer: null, userFilter: "all", userStats: {}, editingUser: null, editingNotice: null, noticeLevel: "Info",
  conversationId: null, convs: [], convTotal: 0, convShowAll: false, secrets: [], patient: ""
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

// Der Zugangsschlüssel gilt nur, solange der Tab offen ist. Früher lag er dauerhaft im localStorage,
// ein alter Eintrag wird einmal übernommen und dort gelöscht.
function storage(action, value) {
  try {
    const old = localStorage.getItem("zwijg.key");
    if (old !== null) {
      localStorage.removeItem("zwijg.key");
      if (!sessionStorage.getItem("zwijg.key")) sessionStorage.setItem("zwijg.key", old);
    }
  } catch { /* privater Modus */ }
  try {
    if (action === "get") return sessionStorage.getItem("zwijg.key") || "";
    if (action === "set") sessionStorage.setItem("zwijg.key", value);
    if (action === "del") sessionStorage.removeItem("zwijg.key");
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

  // Beim ersten Mal die Einführung, nach einem Pflicht-Passwortwechsel erst danach
  if (!me.tourSeen) {
    if ($("passwordDialog").open) $("passwordDialog").addEventListener("close", () => setTimeout(startTour, 300), { once: true });
    else setTimeout(startTour, 300);
  }
}

const UNREACHABLE = "Zwijg ist nicht erreichbar. Bitte später noch einmal versuchen.";

// Kein Netz, oder der Proxy davor meldet, dass Zwijg selbst nicht läuft
function unreachable(res) {
  return !res || res.status === 502 || res.status === 503 || res.status === 504;
}

async function loginWithKey(key) {
  state.key = key.trim();
  const res = await fetch("/v1/me", { headers: authHeaders() }).catch(() => null);
  if (!res?.ok) {
    state.key = "";
    if (unreachable(res)) return UNREACHABLE;
    const data = await res.json().catch(() => ({}));
    // Ein abgelehnter Schlüssel soll beim nächsten Öffnen nicht wieder probiert werden
    if (res.status === 401) storage("del");
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
  }).catch(() => null);
  if (unreachable(res)) return UNREACHABLE;
  const data = await res.json().catch(() => ({}));
  if (!res.ok) return data.error || "Anmeldung fehlgeschlagen";

  state.key = "";
  storage("del");
  const me = await fetch("/v1/me", { headers: authHeaders() }).catch(() => null);
  if (unreachable(me)) return UNREACHABLE;
  if (!me.ok) return "Anmeldung fehlgeschlagen";
  await enterApp(await me.json());
  return null;
}

// Beim Öffnen: gespeicherter Schlüssel oder noch laufende Sitzung
async function resume() {
  // Meldung vom Abmelden, das die Seite neu geladen hat
  let message;
  try {
    message = sessionStorage.getItem("zwijg.loginMessage") || undefined;
    sessionStorage.removeItem("zwijg.loginMessage");
  } catch { /* privater Modus */ }

  try {
    const saved = storage("get");
    if (saved) {
      const error = await loginWithKey(saved);
      if (!error) return;
      if (error === UNREACHABLE) { showLogin(UNREACHABLE); return; }
    }

    const res = await fetch("/v1/me", { headers: authHeaders() }).catch(() => null);
    if (unreachable(res)) { showLogin(UNREACHABLE); return; }
    if (res.ok) {
      await enterApp(await res.json());
      return;
    }
    showLogin(message);
  } catch {
    showLogin(UNREACHABLE);
  }
}

function showLogin(message) {
  $("app").hidden = true;
  $("login").hidden = false;
  $("loginPassword").value = "";
  $("loginKey").value = "";
  // setLoginMode blendet die Fehlermeldung aus, deshalb erst danach setzen
  setLoginMode(loginMode);
  $("loginError").hidden = !message;
  $("loginError").textContent = message || "";
}

// Abmelden lädt die Seite neu. So bleibt von der vorigen Person nichts übrig: kein Entwurf,
// kein Dokument, keine Vorschau, keine laufende Anfrage und keine geladenen Einstellungen.
let loggingOut = false;

async function logout(message) {
  storage("del");
  state.key = "";
  state.me = null;
  if (loggingOut) return;
  loggingOut = true;
  cancelDictation();
  // Anmeldung gleich zeigen, das Neuladen kann bei langsamem Server dauern.
  // Anmelden geht erst nach dem Neuladen, sonst landen Reste dieser Sitzung bei der nächsten Person.
  showLogin(message);
  $("loginForm").querySelector("button[type=submit]").disabled = true;

  try { if (message) sessionStorage.setItem("zwijg.loginMessage", message); } catch { /* privater Modus */ }

  // Erst nach dem Abmelden beim Server neu laden, sonst meldet resume() die alte Sitzung wieder an.
  // Fehler dabei sind egal.
  await fetch("/auth/logout", { method: "POST", headers: { "X-Requested-With": "zwijg" }, signal: AbortSignal.timeout?.(5000) })
    .catch(() => {});

  // Firefox stellt Formularwerte nach dem Neuladen wieder her, deshalb direkt davor leeren.
  // Auch was während des Wartens noch eingefügt wurde.
  for (const f of document.querySelectorAll("input, textarea, select")) {
    if (f.type === "checkbox" || f.type === "radio") f.checked = f.defaultChecked;
    else if (f.tagName === "SELECT") for (const o of f.options) o.selected = o.defaultSelected;
    else f.value = f.defaultValue ?? "";
  }
  location.reload();
}

$("loginForm").addEventListener("submit", async e => {
  e.preventDefault();
  if (loggingOut) return;
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
    { icon: "tour", label: "Einführung ansehen", run: startTour },
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
  // Eigene Farben hängen vom Modus ab
  if (typeof branding !== "undefined") applyBranding();
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
      ["Im Ordner von Zwijg PowerShell öffnen und ausführen. Das Skript beendet Zwijg, sichert Programm und Daten und startet die neue Version zur Probe. Klappt das nicht, stellt es den alten Stand wieder her:", "powershell -ExecutionPolicy Bypass -File update.ps1"],
      ["Danach Zwijg wieder starten:", ".\\Zwijg.Gateway.exe"],
      ["Macht die neue Version später Probleme, Zwijg beenden und zurück zum Stand vor dem Update:", "powershell -ExecutionPolicy Bypass -File update.ps1 -Zurueck"],
    ],
  },
  linux: {
    title: "Linux Paket",
    steps: [
      ["Zwijg beenden, zum Beispiel wenn es als Dienst läuft:", "sudo systemctl stop zwijg"],
      ["Im Ordner von Zwijg ausführen. Das Skript sichert Programm und Daten und startet die neue Version zur Probe. Klappt das nicht, stellt es den alten Stand wieder her:", "./update.sh"],
      ["Danach wieder starten:", "sudo systemctl start zwijg"],
      ["Macht die neue Version später Probleme, Zwijg beenden und zurück zum Stand vor dem Update:", "./update.sh --zurueck"],
    ],
  },
  docker: {
    title: "Docker",
    steps: [
      ["Im Ordner mit der docker-compose.yml ausführen. Die Daten liegen im Volume und bleiben erhalten. Vor dem Umstellen sichert Zwijg sie selbst nach data/sicherungen, wie es zurückgeht, steht in der Anleitung Betrieb:", "docker compose pull && docker compose up -d"],
      ["Nur einmal nötig, wenn Zwijg danach meldet, dass es /app/data nicht ändern darf (älteres Volume, das noch root gehört). Danach wieder docker compose up -d:", "docker compose run --rm --no-deps --user root --entrypoint chown zwijg -R 1654 /app/data"],
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

// Release Texte aus CHANGELOG.md als einfaches Markdown: Überschriften, Listen, fett, Code und Links zu GitHub
function renderNotes(md) {
  const inline = t => esc(t)
    .replace(/\*\*(.+?)\*\*/g, "<strong>$1</strong>")
    .replace(/`([^`]+)`/g, "<code>$1</code>")
    .replace(/\[([^\]]+)\]\((https:\/\/github\.com\/[^)\s]+)\)/g, '<a href="$2" target="_blank" rel="noopener">$1</a>');

  let html = "", list = false;
  for (const raw of String(md || "").split(/\r?\n/)) {
    const line = raw.trim();
    const item = line.match(/^[-*]\s+(.*)$/);
    if (item) {
      if (!list) { html += "<ul>"; list = true; }
      html += `<li>${inline(item[1])}</li>`;
      continue;
    }
    if (list) { html += "</ul>"; list = false; }
    const heading = line.match(/^#{2,4}\s+(.*)$/);
    if (heading) html += `<h5>${inline(heading[1])}</h5>`;
    else if (line) html += `<p>${inline(line)}</p>`;
  }
  return html + (list ? "</ul>" : "");
}

function updateSeen(version) {
  try {
    if (version) localStorage.setItem("zwijg.updateSeen", version);
    return localStorage.getItem("zwijg.updateSeen") || "";
  } catch { return ""; }
}

// Hinweis oben für Admins, wenn es eine neue Version gibt. Wegklicken gilt bis zur nächsten Version.
function updateBannerHtml() {
  const v = versionInfo;
  if (!state.me?.admin || !v?.updateAvailable) return "";
  if (updateSeen() === v.latest) return "";
  const title = v.security ? `Sicherheitsupdate: Zwijg ${v.latest} ist verfügbar` : `Zwijg ${v.latest} ist verfügbar`;
  const text = v.security
    ? `Installiert ist ${v.current}. Die neue Version schließt Sicherheitslücken und sollte bald eingespielt werden.`
    : `Installiert ist ${v.current}.`;
  return `<div class="banner ${v.security ? "Critical" : "Info"}">
    ${icon(v.security ? "alert" : "download")}
    <div class="banner-body">
      <div class="banner-title">${esc(title)}</div>
      <div class="banner-text">${esc(text)} <button type="button" class="link-btn" data-update-open>Was ist neu und wie aktualisieren?</button></div>
    </div>
    <button type="button" class="icon-btn" data-update-seen title="Ausblenden" aria-label="Ausblenden">${icon("x")}</button>
  </div>`;
}

async function loadVersion(refresh = false) {
  if (!state.me?.admin) return;
  try {
    versionInfo = await api("GET", "/admin/version" + (refresh ? "?refresh=true" : ""));
  } catch {
    versionInfo = null;
  }
  renderVersionRow();
  renderBanners();
  if ($("updateDialog").open) renderUpdateDialog();
}

function renderVersionRow() {
  const el = $("versionStatus");
  if (!el) return;
  const v = versionInfo;
  if (!v) { el.innerHTML = ""; return; }
  const badge = v.updateAvailable && v.security ? chip(`Sicherheitsupdate auf ${v.latest}`, "warn", "alert")
    : v.updateAvailable ? chip(`Update auf ${v.latest}`, "info", "download")
    : v.error ? chip("nicht geprüft", "", "alert")
    : !v.enabled && !v.checkedAt ? chip("Prüfung aus")
    : chip("aktuell", "ok", "check");
  el.innerHTML = `<strong>${esc(v.current)}</strong>${badge}`;
}

function renderUpdateDialog() {
  const v = versionInfo;
  if (!v) return;

  $("updVerdict").innerHTML = v.updateAvailable && v.security
    ? `<div class="doc-verdict bad">${icon("alert")}<div><strong>Sicherheitsupdate auf ${esc(v.latest)}</strong><span>Installiert ist ${esc(v.current)}. Die neue Version schließt Sicherheitslücken, bitte bald einspielen. Einstellungen, Protokoll und Verlauf bleiben erhalten.</span></div></div>`
    : v.updateAvailable
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

  // Alle Versionen seit der installierten, neueste zuerst
  const releases = v.releases || [];
  $("updNotesBox").hidden = !(v.updateAvailable && releases.length);
  $("updNotes").innerHTML = releases.map(r => `<div class="upd-release">
      <h4>Version ${esc(r.version)}${r.publishedAt ? ` <span class="muted small">vom ${fmtDateTime(r.publishedAt)}</span>` : ""}${r.security ? chip("Sicherheit", "warn", "alert") : ""}</h4>
      <div class="notes-md">${r.notes ? renderNotes(r.notes) : '<p class="muted">Keine Beschreibung.</p>'}</div>
    </div>`).join("");

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

$("dashStatus").addEventListener("click", e => {
  if (e.target.closest("#versionRow")) openUpdateDialog();
  if (e.target.closest("#backupRow")) show("backup");
});
$("dashStatus").addEventListener("keydown", e => { if (e.key === "Enter" && e.target.closest("#versionRow")) openUpdateDialog(); });

$("updHowto").addEventListener("click", async e => {
  const b = e.target.closest("[data-copy-cmd]");
  if (!b) return;
  await navigator.clipboard.writeText(b.dataset.copyCmd);
  toast("Befehl kopiert");
});

// Text schützen: die Zuordnung Platzhalter zu echtem Wert lebt nur hier im Speicher, nie im Browserspeicher
const protect = { mapping: [], protectedText: "", restoredText: "", secrets: [] };

const PLACEHOLDER = /\[\s*([A-Z]+)_(\d+)\s*\]/g;

async function runProtect() {
  const text = $("protectInput").value;
  if (!text.trim()) return;
  $("protectRun").disabled = true;
  $("protectStatus").textContent = "wird geschützt ...";
  try {
    const r = await api("POST", "/v1/protect", { text, known: protect.mapping, secrets: protect.secrets, patient: $("protectPatient").value.trim() || null });
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
  $("protectPatient").value = "";
  protect.mapping = [];
  protect.secrets = [];
  renderProtectSecrets();
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

// Kurze Einführung beim ersten Anmelden. Hebt nacheinander einzelne Stellen hervor, überspringen geht immer.
// Gezeigt wird nur, was die Person auch benutzen darf.
const tour = { steps: [], i: 0 };

function tourSteps() {
  const me = state.me;
  return [
    { view: "chat", title: "Willkommen bei Zwijg",
      text: "Zwijg schützt Patientendaten, bevor eine Frage an eine KI geht. In einer Minute zeigen wir dir die wichtigsten Stellen." },
    { el: "#prompt", view: "chat", title: "Ganz normal schreiben",
      text: "Hier stellst du Fragen wie in jedem Chat, auch mit Namen, Geburtsdaten oder Versichertennummern. Zwijg ersetzt sie vor dem Versand und setzt sie in der Antwort wieder ein." },
    { el: ".patient-bar", view: "chat", title: "Patient eintragen",
      text: "Geht es um einen bestimmten Patienten, hier einmal Name und Geburtsdatum eintragen. Zwijg versteckt sie dann in der ganzen Unterhaltung, auch wenn nur der Nachname im Satz steht." },
    me.showPreview && { el: "#previewCard", view: "chat", title: "Das sieht die KI",
      text: "Hier steht beim Tippen genau der Text, der rausgeht. Ersetzte Stellen sind gelb. Erkennt Zwijg etwas nicht, im Eingabefeld markieren und auf Verstecken klicken." },
    me.dictation && { el: "#dictate", view: "chat", title: "Diktieren",
      text: "Sprechen statt tippen, mit Strg+M. Die Spracherkennung läuft auf diesem Rechner." },
    me.canUseDocuments && { el: "#navDoc", title: "Dokumente",
      text: "PDFs, Scans und Fotos von Befunden hochladen. Zwijg prüft sie und du kannst Fragen dazu stellen." },
    me.cloudAllowed && { el: "#navProtect", title: "Text schützen",
      text: "Für Programme, die nicht an Zwijg angebunden sind: Text hier schützen, dort einfügen und die Antwort hier mit den echten Daten zurückholen." },
    { el: "#convSection", title: "Verlauf",
      text: "Unterhaltungen werden verschlüsselt gespeichert. So kannst du später weitermachen." },
    me.files && { el: "#navFiles", title: "Dateien verschlüsseln",
      text: "Röntgenbilder und Befunde mit Kennwort verschlüsseln, für Empfänger ohne KIM. Die Datei öffnet man mit 7-Zip oder WinRAR." },
    me.admin && { el: ".nav .admin-only", title: "Verwaltung",
      text: "Als Admin richtest du hier die KI Modelle, Regeln und Benutzer ein. Im Protokoll steht, wer wann was gefragt hat, ohne Klartext." },
    { el: "#whoBox", title: "Dein Konto",
      text: "Hier änderst du dein Passwort, wählst hell oder dunkel und kannst diese Einführung jederzeit wieder ansehen." },
    { title: "Los geht's",
      text: "Antworten der KI bitte immer prüfen, bevor du sie übernimmst. Zwijg hilft dabei und markiert Wirkstoffe und Dosierungen, die nicht in deiner Frage standen." },
    // Schritte mit eigener Ansicht werden erst beim Zeigen sichtbar, die anderen nur, wenn es sie gerade gibt
  ].filter(s => s && (!s.el || s.view || tourTargets(s.el).length));
}

// Sichtbare Elemente zu einem Schritt, bei der Verwaltung mehrere Knöpfe auf einmal
function tourTargets(selector) {
  return [...document.querySelectorAll(selector)].filter(el => !el.hidden && el.getClientRects().length);
}

function startTour() {
  if (!state.me || !$("tour").hidden) return;
  document.querySelectorAll("dialog[open]").forEach(d => d.close());
  tour.steps = tourSteps();
  tour.i = 0;
  $("tour").hidden = false;
  document.addEventListener("keydown", tourKeys, true);
  window.addEventListener("resize", placeTour);
  showTourStep();
}

async function endTour() {
  $("tour").hidden = true;
  document.removeEventListener("keydown", tourKeys, true);
  window.removeEventListener("resize", placeTour);
  if (state.me && !state.me.tourSeen) {
    state.me.tourSeen = true;
    await api("POST", "/v1/me/tour").catch(() => {});
  }
}

function showTourStep() {
  const step = tour.steps[tour.i];
  if (step.view && document.getElementById("view-" + step.view)?.hidden) show(step.view);
  const last = tour.i === tour.steps.length - 1;

  $("tourStep").textContent = `${tour.i + 1} von ${tour.steps.length}`;
  $("tourTitle").textContent = step.title;
  $("tourText").textContent = step.text;
  $("tourBack").hidden = tour.i === 0;
  $("tourNext").textContent = tour.i === 0 ? "Los" : last ? "Fertig" : "Weiter";
  $("tourSkip").hidden = last;

  const targets = step.el ? tourTargets(step.el) : [];
  targets[0]?.scrollIntoView({ block: "nearest" });
  placeTour();
  $("tourNext").focus();
}

// Markierung um das Ziel legen und die Karte daneben, so dass sie ganz zu sehen ist
function placeTour() {
  const step = tour.steps[tour.i];
  const spot = $("tourSpot"), card = $("tourCard");
  const targets = step?.el ? tourTargets(step.el) : [];
  const pad = 6, gap = 14, margin = 12;

  card.classList.toggle("center", !targets.length);
  spot.hidden = !targets.length;
  $("tour").classList.toggle("dim", !targets.length);
  if (!targets.length) {
    card.style.left = card.style.top = "";
    return;
  }

  const rects = targets.map(t => t.getBoundingClientRect());
  const r = {
    left: Math.min(...rects.map(x => x.left)) - pad, top: Math.min(...rects.map(x => x.top)) - pad,
    right: Math.max(...rects.map(x => x.right)) + pad, bottom: Math.max(...rects.map(x => x.bottom)) + pad,
  };
  Object.assign(spot.style, { left: r.left + "px", top: r.top + "px", width: r.right - r.left + "px", height: r.bottom - r.top + "px" });

  const w = card.offsetWidth, h = card.offsetHeight, vw = innerWidth, vh = innerHeight;
  let left, top;
  if (vw < 640) {
    // Schmaler Bildschirm: Karte unten oder oben, je nachdem wo Platz ist
    left = margin;
    top = r.bottom + gap + h < vh ? r.bottom + gap : Math.max(margin, r.top - gap - h);
  } else if (r.right + gap + w + margin < vw) {
    left = r.right + gap; top = r.top;
  } else if (r.left - gap - w > margin) {
    left = r.left - gap - w; top = r.top;
  } else if (r.bottom + gap + h + margin < vh) {
    left = r.left; top = r.bottom + gap;
  } else {
    left = r.left; top = r.top - gap - h;
  }
  card.style.left = Math.min(Math.max(margin, left), vw - w - margin) + "px";
  card.style.top = Math.min(Math.max(margin, top), vh - h - margin) + "px";
}

function tourKeys(e) {
  if (e.key === "Escape") { e.preventDefault(); e.stopPropagation(); endTour(); }
  else if (e.key === "ArrowRight") { e.preventDefault(); tourGo(1); }
  else if (e.key === "ArrowLeft") { e.preventDefault(); tourGo(-1); }
}

function tourGo(delta) {
  const next = tour.i + delta;
  if (next >= tour.steps.length) return endTour();
  if (next < 0) return;
  tour.i = next;
  showTourStep();
}

$("tourNext").addEventListener("click", () => tourGo(1));
$("tourBack").addEventListener("click", () => tourGo(-1));
$("tourSkip").addEventListener("click", endTour);

// Sicherung, nur für Admins
const BACKUP_STATES = {
  ok: ["ok", "check", "Sicherung läuft"],
  pending: ["info", "clock", "Eingerichtet, die erste Sicherung kommt bald"],
  off: ["warn", "alert", "Keine Sicherung eingerichtet"],
  failed: ["warn", "alert", "Letzte Sicherung fehlgeschlagen"],
  stale: ["warn", "alert", "Letzte Sicherung ist älter als zwei Tage"],
  external: ["", "info", "Die Praxis sichert selbst"],
};
let backupInfo = null;

function backupNeedsAttention(state) {
  return ["off", "failed", "stale"].includes(state);
}

async function loadBackup() {
  const r = await api("GET", "/admin/backup");
  backupInfo = r;
  const s = r.settings;
  $("bkEnabled").checked = s.enabled;
  $("bkExternal").checked = s.external;
  $("bkDir").value = s.directory || "";
  $("bkTime").value = s.time || "02:00";
  $("bkPw").value = $("bkPw2").value = "";
  $("bkPwLabel").textContent = s.hasPassword ? "Neues Passwort (leer lassen zum Behalten)" : "Passwort für die Verschlüsselung";
  $("bkRun").disabled = !s.enabled;

  const last = r.status.lastSuccess;
  const verdict = {
    ok: ["ok", "check", "Sicherung läuft", `Letzte Sicherung ${fmtDateTime(last)}, nächste täglich um ${esc(s.time)} Uhr.`],
    pending: ["", "clock", "Eingerichtet", `Die erste Sicherung startet in den nächsten Minuten, danach täglich um ${esc(s.time)} Uhr.`],
    off: ["warn", "alert", "Keine Sicherung eingerichtet", "Fällt die Festplatte aus, sind Benutzer, Regeln, Verlauf und das Protokoll weg. Bitte einen Zielordner und ein Passwort festlegen."],
    failed: ["bad", "alert", "Letzte Sicherung fehlgeschlagen", esc(r.status.lastError || "") + (last ? ` Die letzte gelungene war ${fmtDateTime(last)}.` : "")],
    stale: ["bad", "alert", "Letzte Sicherung ist zu alt", `Die letzte gelungene war ${fmtDateTime(last)}. Läuft der Rechner nachts, und ist das Ziel erreichbar?`],
    external: ["", "info", "Die Praxis sichert selbst", "Zwijg sichert nicht und warnt auch nicht. Wichtig ist, dass der ganze Ordner data mitgesichert wird."],
  }[r.state];
  $("bkVerdict").innerHTML = `<div class="doc-verdict ${verdict[0]}">${icon(verdict[1])}<div><strong>${verdict[2]}</strong><span>${verdict[3]}</span></div></div>`
    + (r.sameDisk ? `<div class="doc-verdict warn">${icon("alert")}<div><strong>Gleiche Festplatte</strong><span>Das Ziel liegt auf derselben Festplatte wie Zwijg. Bei einem Plattenausfall wäre die Sicherung auch weg. Besser ein anderes Laufwerk, ein NAS oder eine USB Platte.</span></div></div>` : "");

  $("bkFiles").innerHTML = r.files.length
    ? `<div class="backup-files">${r.files.map(f => `<div class="backup-file">${icon("lock")}<span class="backup-name" title="${esc(f.name)}">${fmtDateTime(f.created)}</span><span class="muted small">${f.size < 1048576 ? Math.max(1, Math.round(f.size / 1024)) + " KB" : (f.size / 1048576).toFixed(1) + " MB"}</span></div>`).join("")}</div>`
    : '<p class="muted small">Noch keine Sicherung in diesem Ordner.</p>';

  const newest = r.files[0] ? (s.directory.replace(/[\\/]+$/, "") + (s.directory.includes("\\") ? "\\" : "/") + r.files[0].name) : "<Datei>";
  $("bkRestoreCmd").textContent = `Zwijg.Gateway --zurueckspielen "${newest}"`;
  renderBackupNav();
}

async function saveBackup() {
  const pw = $("bkPw").value;
  if (pw && pw !== $("bkPw2").value) {
    toast("Die beiden Passwörter sind nicht gleich", true);
    return;
  }
  try {
    await api("PUT", "/admin/backup", {
      enabled: $("bkEnabled").checked,
      external: $("bkExternal").checked,
      directory: $("bkDir").value.trim(),
      time: $("bkTime").value || "02:00",
      password: pw || null,
    });
    toast(pw ? "Gespeichert. Bitte das Passwort sicher aufbewahren." : "Gespeichert");
    await loadBackup();
    await reloadMe();
  } catch (err) { toast(err.message, true); }
}

async function runBackup() {
  const b = $("bkRun");
  b.disabled = true;
  const label = b.innerHTML;
  b.innerHTML = `${icon("clock")}wird gesichert ...`;
  try {
    await api("POST", "/admin/backup/run");
    toast("Sicherung erstellt");
  } catch (err) { toast(err.message, true); }
  finally {
    b.innerHTML = label;
    await loadBackup().catch(() => {});
    await reloadMe();
  }
}

$("bkSave").addEventListener("click", saveBackup);
$("bkRun").addEventListener("click", runBackup);
$("bkEnabled").addEventListener("change", () => { if ($("bkEnabled").checked) $("bkExternal").checked = false; });
$("bkExternal").addEventListener("change", () => { if ($("bkExternal").checked) $("bkEnabled").checked = false; });

// Roter Punkt in der Navigation und Hinweis nach der Anmeldung, solange etwas nicht stimmt
function renderBackupNav() {
  $("backupDot").hidden = !(state.me?.admin && backupNeedsAttention(state.me.backup?.state));
}

function backupBannerHtml() {
  const b = state.me?.backup;
  if (!state.me?.admin || !backupNeedsAttention(b?.state)) return "";
  try { if (sessionStorage.getItem("zwijg.backupSeen") === b.state) return ""; } catch { }
  const text = {
    off: "Fällt die Festplatte aus, sind Benutzer, Regeln, Verlauf und Protokoll weg.",
    failed: b.lastError || "",
    stale: b.lastSuccess ? `Die letzte gelungene Sicherung war ${fmtDateTime(b.lastSuccess)}.` : "",
  }[b.state];
  return `<div class="banner ${b.state === "off" ? "Warning" : "Critical"}">
    ${icon("alert")}
    <div class="banner-body">
      <div class="banner-title">${esc(BACKUP_STATES[b.state][2])}</div>
      <div class="banner-text">${esc(text)} <button type="button" class="link-btn" data-backup-open>Sicherung einrichten</button></div>
    </div>
    <button type="button" class="icon-btn" data-backup-seen title="Bis zur nächsten Anmeldung ausblenden" aria-label="Ausblenden">${icon("x")}</button>
  </div>`;
}

function backupStatusRow() {
  const b = state.me?.backup;
  if (!b) return "";
  const [kind, ico, label] = BACKUP_STATES[b.state] || BACKUP_STATES.off;
  const text = b.state === "ok" ? `zuletzt ${relTime(b.lastSuccess)}` : label;
  return `<div class="status-row clickable" id="backupRow" tabindex="0" title="Sicherung"><span class="label">Sicherung</span><span class="version-status">${chip(text, kind, ico)}</span></div>`;
}

// Eigenes Aussehen: Praxisname, Logo, Akzentfarbe und Hintergrund der Anmeldung. Gilt schon auf der Anmeldeseite.
// Die Werte kommen vom Server und sind dort geprüft (Farben nur als #RRGGBB).
var branding = null; // var, weil applyTheme schon vorher laufen kann

async function loadBranding() {
  branding = await fetch("/branding").then(r => r.ok ? r.json() : null).catch(() => null);
  applyBranding();
}

function hexToRgb(hex) {
  const n = parseInt(hex.slice(1), 16);
  return [n >> 16 & 255, n >> 8 & 255, n & 255];
}

function mixColor(a, b, t) {
  const x = hexToRgb(a), y = hexToRgb(b);
  return "#" + x.map((v, i) => Math.round(v + (y[i] - v) * t).toString(16).padStart(2, "0")).join("");
}

function luminance(hex) {
  const [r, g, b] = hexToRgb(hex).map(v => { v /= 255; return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

function contrast(a, b) {
  const [l1, l2] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (l1 + 0.05) / (l2 + 0.05);
}

// Aus einer Farbe die Varianten für hell und dunkel ableiten, damit beides gut lesbar bleibt
function applyBranding(b = branding) {
  const root = document.documentElement.style;
  const dark = document.documentElement.dataset.theme === "dark";
  const props = ["--accent", "--accent-2", "--accent-soft", "--accent-ink"];

  if (b?.accent) {
    const base = dark ? mixColor(b.accent, "#ffffff", 0.35) : b.accent;
    root.setProperty("--accent", base);
    root.setProperty("--accent-2", mixColor(base, dark ? "#ffffff" : "#000000", 0.15));
    root.setProperty("--accent-soft", dark ? mixColor(base, "#151615", 0.78) : mixColor(base, "#ffffff", 0.88));
    root.setProperty("--accent-ink", luminance(base) > 0.45 ? "#111111" : "#ffffff");
  } else {
    props.forEach(p => root.removeProperty(p));
  }

  $("login").style.background = b?.gradient ? `linear-gradient(${b.gradient.angle}deg, ${b.gradient.from}, ${b.gradient.to})` : "";

  for (const brand of document.querySelectorAll(".sidebar .brand, .login .brand")) {
    const logo = brand.querySelector(".logo"), name = brand.querySelector(".brand-name"), sub = brand.querySelector(".brand-sub");
    logo.dataset.original ??= logo.innerHTML;
    name.dataset.original ??= name.textContent;
    sub.dataset.original ??= sub.textContent;

    if (b?.logo) {
      logo.innerHTML = "";
      const img = document.createElement("img");
      img.src = b.logo;
      img.alt = "";
      logo.appendChild(img);
    } else {
      logo.innerHTML = logo.dataset.original;
    }
    logo.classList.toggle("has-image", !!b?.logo);

    // Mit eigenem Namen bleibt "mit Zwijg" stehen, so verlangt es die Namensnennung
    name.textContent = b?.name || name.dataset.original;
    sub.textContent = b?.name ? "mit Zwijg" : sub.dataset.original;
  }
}

// Seite Darstellung für Admins, Änderungen wirken sofort als Vorschau
function brandingForm() {
  return {
    name: $("brName").value.trim() || null,
    accent: $("brAccentOn").checked ? $("brAccent").value : null,
    gradient: $("brGradientOn").checked ? { from: $("brFrom").value, to: $("brTo").value, angle: Number($("brAngle").value) } : null,
    logo: branding?.logo || null,
  };
}

function previewBranding() {
  const f = brandingForm();
  $("brAccent").disabled = !$("brAccentOn").checked;
  for (const id of ["brFrom", "brTo", "brAngle"]) $(id).disabled = !$("brGradientOn").checked;
  $("brAngleValue").textContent = $("brAngle").value + "°";
  $("brPreview").style.background = f.gradient ? `linear-gradient(${f.gradient.angle}deg, ${f.gradient.from}, ${f.gradient.to})` : "";
  $("brContrast").hidden = !(f.accent && contrast(f.accent, "#ffffff") < 3);
  $("brLogoBox").innerHTML = f.logo ? `<img src="${esc(f.logo)}" alt="">` : '<span class="muted small">Kein Logo</span>';
  $("brLogoRemove").hidden = !f.logo;
  applyBranding(f);
}

async function loadBrandingAdmin() {
  await loadBranding();
  const settings = await api("GET", "/admin/settings");
  $("fiEnabled").checked = !!settings.files?.enabled;
  $("fiMax").value = settings.files?.maxSizeMb ?? 500;
  const b = branding || {};
  $("brName").value = b.name || "";
  $("brAccentOn").checked = !!b.accent;
  $("brAccent").value = b.accent || "#1f6f5c";
  $("brGradientOn").checked = !!b.gradient;
  $("brFrom").value = b.gradient?.from || "#e3f0eb";
  $("brTo").value = b.gradient?.to || "#1f6f5c";
  $("brAngle").value = b.gradient?.angle ?? 135;
  previewBranding();
}

async function saveBranding() {
  const f = brandingForm();
  try {
    await api("PUT", "/admin/branding", {
      practiceName: f.name,
      accent: f.accent,
      gradientFrom: f.gradient?.from || null,
      gradientTo: f.gradient?.to || null,
      gradientAngle: f.gradient?.angle ?? 135,
    });
    toast("Darstellung gespeichert");
    await loadBrandingAdmin();
  } catch (err) { toast(err.message, true); }
}

for (const id of ["brName", "brAccentOn", "brAccent", "brGradientOn", "brFrom", "brTo", "brAngle"])
  $(id).addEventListener("input", previewBranding);
$("brSave").addEventListener("click", saveBranding);
$("brReset").addEventListener("click", () => {
  $("brName").value = "";
  $("brAccentOn").checked = false;
  $("brGradientOn").checked = false;
  previewBranding();
  toast("Zurückgesetzt, mit Speichern übernehmen");
});

$("brLogoFile").addEventListener("change", async e => {
  const file = e.target.files[0];
  e.target.value = "";
  if (!file) return;
  const form = new FormData();
  form.append("file", file);
  try {
    await api("POST", "/admin/branding/logo", form);
    toast("Logo hochgeladen");
    await loadBranding();
    previewBranding();
  } catch (err) { toast(err.message, true); }
});

$("brLogoRemove").addEventListener("click", async () => {
  try {
    await api("DELETE", "/admin/branding/logo");
    await loadBranding();
    previewBranding();
  } catch (err) { toast(err.message, true); }
});

// Verlässt man die Seite ohne Speichern, gilt wieder das gespeicherte Aussehen
document.querySelectorAll(".nav button").forEach(b => b.addEventListener("click", () => { if (b.dataset.view !== "branding") applyBranding(); }));

// Dateien verschlüsseln und öffnen. Die Arbeit macht der Server, hier nur Auswahl, Kennwort und Herunterladen.
const filesState = { list: [] };

// Gut diktierbares Kennwort aus Silben, etwa "Kalome-Tusape-Nirado-Bufeki-47". Rund 80 Bit Zufall.
function generateFilePassword() {
  const consonants = "bdfgklmnprstvz", vowels = "aeiou";
  const random = max => {
    const limit = Math.floor(0x100000000 / max) * max;
    const buf = new Uint32Array(1);
    do crypto.getRandomValues(buf); while (buf[0] >= limit);
    return buf[0] % max;
  };
  const syllable = () => consonants[random(consonants.length)] + vowels[random(vowels.length)];
  const groups = Array.from({ length: 4 }, () => {
    const g = syllable() + syllable() + syllable();
    return g[0].toUpperCase() + g.slice(1);
  });
  return groups.join("-") + "-" + String(random(90) + 10);
}

function formatSize(bytes) {
  return bytes < 1048576 ? Math.max(1, Math.round(bytes / 1024)) + " KB" : (bytes / 1048576).toFixed(1) + " MB";
}

function renderFileList() {
  const total = filesState.list.reduce((n, f) => n + f.size, 0);
  $("fiList").innerHTML = filesState.list.map((f, i) => `<div class="backup-file">${icon("doc")}<span class="backup-name">${esc(f.name)}</span>
      <span class="muted small">${formatSize(f.size)}</span>
      <button class="icon-btn" type="button" data-remove-file="${i}" title="Entfernen" aria-label="Entfernen">${icon("x")}</button></div>`).join("");
  $("fiTotal").textContent = filesState.list.length ? `${filesState.list.length} ${filesState.list.length === 1 ? "Datei" : "Dateien"}, ${formatSize(total)}` : "";
  $("fiEncrypt").disabled = !filesState.list.length;
}

function addFiles(files) {
  filesState.list.push(...files);
  renderFileList();
}

// Antwort als Datei speichern, den Namen schickt der Server mit
async function downloadResponse(res) {
  const header = res.headers.get("Content-Disposition") || "";
  const star = header.match(/filename\*=UTF-8''([^;]+)/i);
  const plain = header.match(/filename="?([^";]+)"?/i);
  const name = star ? decodeURIComponent(star[1]) : plain ? plain[1] : "datei";
  const url = URL.createObjectURL(await res.blob());
  const a = document.createElement("a");
  a.href = url;
  a.download = name;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10000);
  return name;
}

async function sendFiles(path, form, button, label) {
  button.disabled = true;
  const original = button.innerHTML;
  button.innerHTML = `${icon("clock")}${label}`;
  try {
    const res = await api("POST", path, form, { raw: true });
    if (!res.ok) {
      const data = await res.json().catch(() => ({}));
      throw new Error(data.error || `Fehler ${res.status}`);
    }
    return await downloadResponse(res);
  } finally {
    button.innerHTML = original;
    button.disabled = false;
  }
}

$("fiDrop").addEventListener("dragover", e => { e.preventDefault(); $("fiDrop").classList.add("over"); });
$("fiDrop").addEventListener("dragleave", () => $("fiDrop").classList.remove("over"));
$("fiDrop").addEventListener("drop", e => {
  e.preventDefault();
  $("fiDrop").classList.remove("over");
  addFiles([...e.dataTransfer.files]);
});
$("fiInput").addEventListener("change", e => { addFiles([...e.target.files]); e.target.value = ""; });
$("fiList").addEventListener("click", e => {
  const b = e.target.closest("[data-remove-file]");
  if (!b) return;
  filesState.list.splice(Number(b.dataset.removeFile), 1);
  renderFileList();
});

$("fiGenerate").addEventListener("click", () => {
  $("fiPassword").value = generateFilePassword();
  $("fiPassword").type = "text";
});
$("fiShow").addEventListener("click", () => { $("fiPassword").type = $("fiPassword").type === "password" ? "text" : "password"; });
$("fiCopy").addEventListener("click", () => copyText($("fiPassword").value, "Kennwort kopiert"));

$("fiEncrypt").addEventListener("click", async () => {
  const password = $("fiPassword").value;
  const max = (state.me.files?.maxSizeMb || 500) * 1048576;
  const total = filesState.list.reduce((n, f) => n + f.size, 0);
  if (password.length < (state.me.files?.minPassword || 12)) {
    toast(`Das Kennwort braucht mindestens ${state.me.files?.minPassword || 12} Zeichen. Mit Erzeugen geht es am einfachsten.`, true);
    return;
  }
  if (total > max) {
    toast(`Zusammen höchstens ${state.me.files.maxSizeMb} MB`, true);
    return;
  }

  // Kennwort und Name vor den Dateien, der Server verschlüsselt dann gleich beim Lesen
  const form = new FormData();
  form.append("password", password);
  form.append("name", $("fiName").value.trim() || "dokumente");
  for (const f of filesState.list) form.append("file", f, f.name);

  try {
    const name = await sendFiles("/v1/files/encrypt", form, $("fiEncrypt"), "wird verschlüsselt ...");
    toast(`${name} gespeichert. Kennwort bitte getrennt weitergeben.`);
    filesState.list = [];
    renderFileList();
  } catch (err) { toast(err.message, true); }
});

$("fiOpenInput").addEventListener("change", e => {
  $("fiOpenName").textContent = e.target.files[0]?.name || "Keine Datei gewählt";
  $("fiOpen").disabled = !e.target.files[0];
});

$("fiOpen").addEventListener("click", async () => {
  const file = $("fiOpenInput").files[0];
  if (!file) return;
  const form = new FormData();
  form.append("password", $("fiOpenPassword").value);
  form.append("file", file, file.name);
  try {
    const name = await sendFiles("/v1/files/decrypt", form, $("fiOpen"), "wird geöffnet ...");
    toast(`${name} gespeichert`);
    $("fiOpenPassword").value = "";
  } catch (err) { toast(err.message, true); }
});

// Einstellung für Admins auf der Seite Darstellung
async function saveFilesSetting() {
  try {
    await api("PUT", "/admin/files", { enabled: $("fiEnabled").checked, maxSizeMb: Number($("fiMax").value) || 500 });
    toast("Gespeichert");
    await reloadMe();
  } catch (err) { toast(err.message, true); }
}
$("fiSave").addEventListener("click", saveFilesSetting);

// Über Zwijg: Ersteller, Quellcode und Lizenzen. Geht auch ohne Anmeldung.
async function openAbout() {
  $("aboutDialog").showModal();
  const h = await fetch("/health").then(r => r.json()).catch(() => null);
  $("aboutVersion").textContent = h?.version ? "Version " + h.version : "";

  // Was ist neu in der installierten Version, aus der mitgelieferten CHANGELOG.md
  const c = await fetch("/changelog").then(r => r.json()).catch(() => null);
  $("aboutNewBox").hidden = !c?.notes;
  $("aboutNew").innerHTML = c?.notes ? renderNotes(c.notes) : "";
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
  $("dictate").hidden = !me.dictation;
  $("navFiles").hidden = !me.files;
  $("previewCard").hidden = !me.showPreview;
  $("chatLayout").classList.toggle("solo", !me.showPreview);
  $("routeCloud").hidden = !me.cloudAllowed;
  if (!me.cloudAllowed && $("route").value === "cloud") $("route").value = "";

  renderUsage();
  renderTemplateBar();
  applyHistory();
  renderBanners();
  if (me.admin && !versionInfo) loadVersion();
}

function renderTemplateBar() {
  const list = state.me.templates || [];
  $("templateBar").hidden = !list.length;
  $("templateBar").innerHTML = list.map(t =>
    `<button type="button" class="${t.mode === "Run" ? "run" : ""}" data-template="${esc(t.id)}" title="${t.mode === "Run" ? "Wird direkt ausgeführt" : "Wird ins Eingabefeld eingefügt"}">${icon(t.mode === "Run" ? "send" : "doc")}${esc(t.title)}</button>`).join("");
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
  // Gilt für Schlüssel und Passwort-Sitzung, nur abgemeldet nicht
  if (!state.me) return;
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
    ${a.dismissible && dismiss ? `<button type="button" class="icon-btn" data-dismiss="${esc(a.id)}" title="Ausblenden" aria-label="Ausblenden">${icon("x")}</button>` : ""}
  </div>`;
}

function renderBanners() {
  $("banners").innerHTML = backupBannerHtml() + updateBannerHtml() + (state.me.announcements || []).map(a => bannerHtml(a)).join("");
  renderBackupNav();
}

$("banners").addEventListener("click", async e => {
  if (e.target.closest("[data-update-open]")) { openUpdateDialog(); return; }
  if (e.target.closest("[data-backup-open]")) { show("backup"); return; }
  if (e.target.closest("[data-backup-seen]")) {
    try { sessionStorage.setItem("zwijg.backupSeen", state.me.backup.state); } catch { }
    renderBanners();
    return;
  }
  if (e.target.closest("[data-update-seen]")) { updateSeen(versionInfo?.latest); renderBanners(); return; }
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
  if (view === "files" && !state.me.files) view = "chat";
  if (!document.getElementById("view-" + view) || (!state.me.admin && document.querySelector(`.nav button[data-view="${view}"]`)?.classList.contains("admin-only"))) view = "chat";
  history.replaceState(null, "", "#" + view);
  document.querySelectorAll(".nav button").forEach(b => b.classList.toggle("active", b.dataset.view === view));
  // Bei vielen Einträgen ist das Menü scrollbar, der aktive soll sichtbar sein
  document.querySelector(".nav button.active")?.scrollIntoView({ block: "nearest" });
  document.querySelectorAll(".view").forEach(v => v.hidden = v.id !== "view-" + view);

  const loaders = { dashboard: loadDashboard, connections: loadConnections, rules: loadRules, users: loadUsers, notices: loadNotices, audit: loadAudit, backup: loadBackup, branding: loadBrandingAdmin };
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

// Antworten können sich überholen. Angezeigt wird nur die zur letzten Anfrage.
let previewSeq = 0;

async function preview() {
  if (!state.me?.showPreview) return;
  const seq = ++previewSeq;
  const text = $("prompt").value;
  if (!text.trim()) {
    $("preview").innerHTML = '<span class="muted">Beim Tippen erscheint hier die geschützte Fassung.</span>';
    $("chips").innerHTML = "";
    return;
  }

  try {
    const r = await api("POST", "/v1/check", { text, secrets: state.secrets, patient: state.patient || null });
    if (seq !== previewSeq) return;
    $("preview").innerHTML = esc(r.pseudonymized).replace(/\[[A-Z]+_\d+\]/g, m => `<mark>${m}</mark>`);

    const chips = Object.entries(r.entities).map(([k, v]) => chip(`${k} × ${v}`));
    if (!chips.length) chips.push(chip("keine Personendaten erkannt", "ok", "check"));
    if (r.healthTerms?.length) {
      const shown = r.healthTerms.slice(0, 4).join(", ") + (r.healthTerms.length > 4 ? " ..." : "");
      chips.push(chip(`Gesundheitsdaten: ${shown}`, "amber", "steth"));
    }
    chips.push(chip(`Sensibilität ${SENSITIVITY[r.sensitivity] ?? r.sensitivity}`, r.sensitivity === "High" ? "amber" : ""));
    const local = r.route === "Local" || !state.me.cloudAllowed;
    if (r.blocked) chips.push(chip("wird blockiert: Weiterleitung steht auf Nur Cloud", "warn", "x"));
    else chips.push(chip(local ? "bleibt lokal" : "darf in die Cloud", "ok", local ? "lock" : "cloud"));
    for (const rule of r.rules || []) {
      const a = { LocalOnly: ["bleibt lokal", "ok", "lock"], Block: ["wird blockiert", "warn", "x"], Warn: ["wird protokolliert", "amber", "list"] }[rule.action];
      if (a) chips.push(chip(`Regel "${rule.name}": ${a[0]}`, a[1], a[2]));
    }
    if (r.injection.score > 0)
      chips.push(chip(`Manipulationsverdacht (${r.injection.score}): ${r.injection.findings.map(f => f.rule).join(", ")}`, "warn", "alert"));
    $("chips").innerHTML = chips.join("");
  } catch (e) {
    if (seq !== previewSeq) return;
    $("preview").innerHTML = `<span class="muted">${esc(e.message)}</span>`;
  }
}

function addMessage(role, text, meta, checks) {
  const chat = $("chat");
  chat.querySelector(".chat-empty")?.remove();
  const div = document.createElement("div");
  div.className = "msg " + role;
  div.textContent = text;
  if (role === "user") {
    div.dataset.text = text;
    markSecrets(div);
  }
  if (checks?.length) {
    div.innerHTML = markChecks(text, checks);
    div.insertAdjacentHTML("beforeend", checkBox(checks));
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

// Antwort-Check: Wirkstoffe, Dosierungen und Laborwerte, die nur in der Antwort stehen, gelb markieren.
// Der Server liefert die Stellen. Erst am Rohtext trennen, dann jedes Stück escapen.
const CHECK_KINDS = { Wirkstoff: "Wirkstoff", Dosierung: "Dosierung", Einnahme: "Einnahme", Laborwert: "Laborwert" };

function markChecks(text, checks) {
  let html = "", pos = 0;
  for (const c of [...checks].sort((a, b) => a.start - b.start)) {
    if (c.start < pos || c.start + c.length > text.length) continue;
    html += esc(text.slice(pos, c.start))
      + `<mark class="check" title="${esc(CHECK_KINDS[c.kind] || c.kind)}, steht nicht in der Frage">${esc(text.slice(c.start, c.start + c.length))}</mark>`;
    pos = c.start + c.length;
  }
  return html + esc(text.slice(pos));
}

function checkBox(checks) {
  const unique = [...new Map(checks.map(c => [c.key, c])).values()];
  return `<div class="check-box">${icon("alert")}<div><strong>Bitte prüfen</strong>
    <span>Diese Angaben stehen nicht in deiner Frage. Die KI kann sie erfunden haben.</span>
    <div class="check-list">${unique.map(c => `<span class="chip warn">${esc(CHECK_KINDS[c.kind] || c.kind)}: ${esc(c.text)}</span>`).join("")}</div></div></div>`;
}

// Selbst markierte Geheimnisse: gelten für die ganze Unterhaltung, gehen nie an die KI.
// Der Server ersetzt sie überall durch Platzhalter und setzt sie in der Antwort wieder ein.
const SECRET_KINDS = { GEHEIM: "Geheim", NAME: "Person", FIRMA: "Firma", NUMMER: "Nummer", ORT: "Ort", DATEN: "Sonstiges" };
let secretLabel = "GEHEIM";

// Der Dialog dient dem Chat und "Text schützen". secretTarget sagt, wohin der neue Eintrag gehört.
let secretTarget = "chat";
const SECRET_INFO = {
  chat: "Diese Stelle wird in der ganzen Unterhaltung durch einen Platzhalter ersetzt, auch in früheren und späteren Nachrichten. Die KI bekommt sie nie zu sehen, in der Antwort steht wieder der echte Text.",
  protect: "Diese Stelle wird beim Schützen durch einen Platzhalter ersetzt, auch in weiteren Texten bis Neu beginnen. In der Antwort aus dem anderen Programm setzt Zwijg wieder den echten Text ein.",
};

function secretList() {
  return secretTarget === "protect" ? protect.secrets : state.secrets;
}

function selectedPromptText() {
  const p = $("prompt");
  return p.value.slice(p.selectionStart, p.selectionEnd).trim();
}

function isSecret(text, list = state.secrets) {
  return list.some(s => s.value.toLowerCase() === text.toLowerCase());
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

// Dasselbe für "Text schützen"
function selectedProtectText() {
  const p = $("protectInput");
  return p.value.slice(p.selectionStart, p.selectionEnd).trim();
}

function updateProtectHide() {
  const sel = document.activeElement === $("protectInput") ? selectedProtectText() : "";
  $("protectHide").hidden = !(sel.length >= 2 && sel.length <= 500 && !isSecret(sel, protect.secrets));
}

for (const ev of ["select", "keyup", "mouseup", "input", "blur"])
  $("protectInput").addEventListener(ev, () => setTimeout(updateProtectHide, 0));

$("protectHide").addEventListener("mousedown", e => e.preventDefault());
$("protectHide").addEventListener("click", () => openSecretDialog(selectedProtectText(), "protect"));

$("protectInput").addEventListener("keydown", e => {
  if (e.ctrlKey && e.shiftKey && e.key.toLowerCase() === "h") {
    e.preventDefault();
    const sel = selectedProtectText();
    if (sel.length >= 2) openSecretDialog(sel, "protect");
  }
});

function renderProtectSecrets() {
  const list = $("protectSecrets");
  list.hidden = protect.secrets.length === 0;
  list.innerHTML = protect.secrets.length === 0 ? "" :
    `<div class="secret-list-title">${icon("lock")}Wird zusätzlich versteckt</div>` +
    protect.secrets.map((s, i) => `<div class="secret-item">
        <span class="secret-item-value" title="${esc(s.value)}">${esc(s.value)}</span>
        <span class="secret-item-kind">${esc(SECRET_KINDS[s.label] || s.label)}</span>
        <button class="icon-btn" type="button" data-remove-secret="${i}" title="Nicht mehr verstecken" aria-label="Nicht mehr verstecken">${icon("x")}</button>
      </div>`).join("");
}

$("protectSecrets").addEventListener("click", e => {
  const b = e.target.closest("[data-remove-secret]");
  if (!b) return;
  protect.secrets.splice(Number(b.dataset.removeSecret), 1);
  renderProtectSecrets();
});

$("prompt").addEventListener("keydown", e => {
  if (e.ctrlKey && e.shiftKey && e.key.toLowerCase() === "h") {
    e.preventDefault();
    const sel = selectedPromptText();
    if (sel.length >= 2) openSecretDialog(sel);
  }
});

function openSecretDialog(text, target = "chat") {
  if (text.length < 2) return;
  secretTarget = target;
  $("secretInfo").textContent = SECRET_INFO[target];
  $("secretForm").dataset.value = text;
  $("secretValue").textContent = text;
  setSecretLabel("GEHEIM");
  $("secretDialog").showModal();
}

function setSecretLabel(label) {
  secretLabel = label;
  for (const b of $("secretKinds").querySelectorAll("button"))
    b.classList.toggle("active", b.dataset.label === label);
  const n = secretList().filter(s => s.label === label).length + 1;
  $("secretExample").textContent = `[${label}_${n}]`;
}

$("secretKinds").addEventListener("click", e => {
  const b = e.target.closest("button[data-label]");
  if (b) setSecretLabel(b.dataset.label);
});

$("secretForm").addEventListener("submit", e => {
  if (e.submitter?.value !== "save") return;
  const value = $("secretForm").dataset.value;
  const list = secretList();
  if (!isSecret(value, list)) list.push({ value, label: secretLabel });

  if (secretTarget === "protect") {
    renderProtectSecrets();
    toast("Wird beim Schützen versteckt");
    const p = $("protectInput");
    p.focus();
    p.setSelectionRange(p.selectionEnd, p.selectionEnd);
    updateProtectHide();
    return;
  }

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
  const ok = await sendContent(text, null);

  // Bei einem Fehler den Text zurückholen, damit nichts neu getippt werden muss.
  // Nicht nach dem Abmelden und nicht, wenn inzwischen etwas Neues im Feld steht.
  if (!ok && state.me && !$("prompt").value) {
    $("prompt").value = text;
    autoGrow();
    preview();
  }
}

// Schickt eine Nachricht. Bei direkt ausgeführten Vorlagen steht im Chat nur die Karte (display),
// die eigentliche Anweisung geht im Hintergrund an die KI. Gibt true zurück, wenn eine Antwort kam.
async function sendContent(text, display) {
  if ($("send").disabled) return false;
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
    if (state.patient) body.zwijg.patient = state.patient;
    if (historyEnabled()) body.zwijg.conversation = state.conversationId || "new";
    const res = await api("POST", "/v1/chat/completions", body, { raw: true, headers: routeHeaders() });
    const data = await res.json().catch(() => ({}));
    typing.remove();

    if (!res.ok) {
      state.history.pop();
      if (res.status === 403 && data.locked) { logout(data.error); return false; }
      const findings = (data.details?.findings || []).map(f => chip(`${f.rule}: ${f.snippet}`, "warn")).join(" ");
      addMessage("error", data.error || `Fehler ${res.status}`, findings);
      return false;
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
      (count > 0 ? chip(`${count} Werte geschützt`, "", "shield") : ""),
      data.zwijg_check);
    return true;
  } catch (e) {
    typing.remove();
    state.history.pop();
    addMessage("error", e.message);
    return false;
  } finally {
    $("send").disabled = false;
    $("prompt").focus();
    reloadMe();
  }
}

$("send").addEventListener("click", send);

// Patient dieser Unterhaltung: Name und Geburtsdatum werden überall versteckt, auch ohne "Herr" oder "Frau" davor.
// Steht nur im Speicher der Seite und verschlüsselt in der gespeicherten Unterhaltung, nie im Browser Speicher.
function setPatient(value) {
  state.patient = value.trim();
  if ($("patient").value !== value) $("patient").value = value;
  $("patientHint").hidden = !state.patient;
  $("patientClear").hidden = !state.patient;
}

$("patient").addEventListener("input", () => {
  setPatient($("patient").value);
  preview();
});
$("patient").addEventListener("keydown", e => {
  if (e.key === "Enter") { e.preventDefault(); $("prompt").focus(); }
});
$("patientClear").addEventListener("click", () => {
  setPatient("");
  preview();
  $("patient").focus();
});

function newConversation() {
  state.history = [];
  state.conversationId = null;
  state.secrets = [];
  renderSecrets();
  setPatient("");
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
  $("docPatient").value = "";
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

// Patient beim Dokument: ändert er sich nach dem Hochladen, wird neu geprüft
let docPatientTimer = null;
$("docPatient").addEventListener("input", () => {
  clearTimeout(docPatientTimer);
  docPatientTimer = setTimeout(() => { if (doc.file) checkDocument(); }, 600);
});

async function checkDocument() {
  const form = new FormData();
  form.append("file", doc.file);
  form.append("patient", $("docPatient").value.trim());
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
  form.append("patient", $("docPatient").value.trim());

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
      + `<div class="msg assistant">${j.check?.length ? markChecks(j.answer, j.check) + checkBox(j.check) : esc(j.answer)}</div><div class="chips">${chips.join("")}</div>`;
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
    ${backupStatusRow()}
    <div class="status-row clickable" id="versionRow" tabindex="0" title="Version und Updates"><span class="label">Version</span><span class="version-status" id="versionStatus"></span></div>`;
  renderVersionRow();
  loadVersion();

  const eventIcon = { blocked: "alert", error: "plug", admin: "edit" };
  $("dashEvents").innerHTML = s.recent.length
    ? s.recent.map(e => `<div class="event ${e.kind} clickable" data-audit-id="${esc(e.id)}" tabindex="0">${icon(eventIcon[e.kind])}<div class="event-text">
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

  const option = (c, selected) => `<option value="${esc(c.id)}" ${selected ? "selected" : ""}>${esc(c.name)}</option>`;
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

    return `<div class="card conn" data-id="${esc(c.id)}">
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
      <div class="conn-test" id="test-${esc(c.id)}"></div>
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
    ? `Gespeichert (${c.apiKeyHint || "verborgen"}). Leer lassen, um ihn zu behalten. Bei neuer Adresse oder neuem Typ bitte neu eingeben.`
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

// Diktieren: im Browser aufnehmen, als WAV mit 16 kHz an Zwijg schicken, Text ins Eingabefeld.
// Der Text geht nicht von selbst raus, damit man Hörfehler vorher sieht.
const dictation = { ctx: null, stream: null, chunks: [], rate: 48000, started: 0, timer: null, busy: false };
const MAX_DICTATION_SECONDS = 300;

function dictationRecording() {
  return dictation.stream !== null;
}

async function startDictation() {
  if (!window.isSecureContext || !navigator.mediaDevices?.getUserMedia) {
    toast("Das Mikrofon geht im Browser nur über HTTPS oder direkt auf diesem Rechner (localhost).", true);
    return;
  }

  try {
    dictation.stream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true } });
  } catch {
    dictation.stream = null;
    toast("Kein Zugriff auf das Mikrofon. Bitte im Browser erlauben.", true);
    return;
  }

  dictation.ctx = new AudioContext();
  dictation.rate = dictation.ctx.sampleRate;
  dictation.chunks = [];
  await dictation.ctx.audioWorklet.addModule("js/recorder-worklet.js");
  const source = dictation.ctx.createMediaStreamSource(dictation.stream);
  const capture = new AudioWorkletNode(dictation.ctx, "zwijg-capture");
  capture.port.onmessage = e => dictation.chunks.push(e.data);

  // Ohne Verbindung zum Ausgang rechnet der Browser nicht, deshalb stumm anschließen
  const mute = dictation.ctx.createGain();
  mute.gain.value = 0;
  source.connect(capture).connect(mute).connect(dictation.ctx.destination);

  dictation.started = Date.now();
  dictation.timer = setInterval(updateDictationButton, 250);
  updateDictationButton();
}

function releaseMicrophone() {
  clearInterval(dictation.timer);
  dictation.stream?.getTracks().forEach(t => t.stop());
  dictation.ctx?.close();
  dictation.stream = null;
  dictation.ctx = null;
}

function cancelDictation() {
  releaseMicrophone();
  dictation.chunks = [];
  updateDictationButton();
}

async function stopDictation() {
  const chunks = dictation.chunks, rate = dictation.rate;
  releaseMicrophone();
  dictation.chunks = [];
  dictation.busy = true;
  updateDictationButton();

  try {
    const form = new FormData();
    form.append("file", new Blob([encodeWav(downsample(chunks, rate))], { type: "audio/wav" }), "diktat.wav");
    form.append("language", "de");
    const r = await api("POST", "/v1/audio/transcriptions", form);
    // Inzwischen abgemeldet: nichts mehr ins Eingabefeld schreiben
    if (!state.me) return;
    if (!r.text) {
      toast("Nichts verstanden. Bitte etwas näher ans Mikrofon.", true);
      return;
    }
    insertAtCursor($("prompt"), r.text);
  } catch (err) {
    toast(err.message, true);
  } finally {
    dictation.busy = false;
    updateDictationButton();
  }
}

// Auf 16 kHz Mono bringen, durch Mitteln über die Abtastwerte
function downsample(chunks, rate) {
  const length = chunks.reduce((n, c) => n + c.length, 0);
  const all = new Float32Array(length);
  let pos = 0;
  for (const c of chunks) { all.set(c, pos); pos += c.length; }

  const ratio = rate / 16000;
  const out = new Float32Array(Math.floor(length / ratio));
  for (let i = 0; i < out.length; i++) {
    const start = Math.floor(i * ratio), end = Math.min(length, Math.floor((i + 1) * ratio));
    let sum = 0;
    for (let j = start; j < end; j++) sum += all[j];
    out[i] = end > start ? sum / (end - start) : 0;
  }
  return out;
}

function encodeWav(samples) {
  const buffer = new ArrayBuffer(44 + samples.length * 2);
  const v = new DataView(buffer);
  const text = (o, s) => [...s].forEach((ch, i) => v.setUint8(o + i, ch.charCodeAt(0)));
  text(0, "RIFF"); v.setUint32(4, 36 + samples.length * 2, true); text(8, "WAVE");
  text(12, "fmt "); v.setUint32(16, 16, true); v.setUint16(20, 1, true); v.setUint16(22, 1, true);
  v.setUint32(24, 16000, true); v.setUint32(28, 32000, true); v.setUint16(32, 2, true); v.setUint16(34, 16, true);
  text(36, "data"); v.setUint32(40, samples.length * 2, true);
  for (let i = 0; i < samples.length; i++) {
    const s = Math.max(-1, Math.min(1, samples[i]));
    v.setInt16(44 + i * 2, s < 0 ? s * 0x8000 : s * 0x7fff, true);
  }
  return buffer;
}

function insertAtCursor(field, text) {
  const before = field.value.slice(0, field.selectionStart);
  const spaced = (before && !/\s$/.test(before) ? " " : "") + text;
  field.setRangeText(spaced, field.selectionStart, field.selectionEnd, "end");
  field.focus();
  field.dispatchEvent(new Event("input"));
}

function updateDictationButton() {
  const b = $("dictate");
  b.classList.toggle("recording", dictationRecording());
  b.disabled = dictation.busy;
  if (dictationRecording()) {
    const seconds = Math.floor((Date.now() - dictation.started) / 1000);
    if (seconds >= MAX_DICTATION_SECONDS) { stopDictation(); return; }
    $("dictateLabel").textContent = `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")} Stopp`;
    b.title = "Aufnahme beenden (Strg+M), Esc bricht ab";
  } else {
    $("dictateLabel").textContent = dictation.busy ? "wird geschrieben ..." : "Diktieren";
    b.title = "Diktieren (Strg+M)";
  }
}

function toggleDictation() {
  if (dictation.busy) return;
  if (dictationRecording()) stopDictation();
  else startDictation().catch(err => { cancelDictation(); toast(err.message, true); });
}

$("dictate").addEventListener("click", toggleDictation);
document.addEventListener("keydown", e => {
  if ($("dictate").hidden) return;
  if (e.ctrlKey && !e.shiftKey && e.key.toLowerCase() === "m") { e.preventDefault(); toggleDictation(); }
  if (e.key === "Escape" && dictationRecording()) cancelDictation();
});

// Einstellungen für Admins unter Regeln, Erkennung
let dictationPoll = null;

async function loadDictation() {
  const r = await api("GET", "/admin/dictation");
  const o = r.settings;
  $("dictEnabled").checked = o.enabled;
  $("dictDir").value = o.modelDir || "";
  $("dictDir").placeholder = r.modelDir;
  $("dictThreads").value = o.threads || "";
  $("dictModels").innerHTML = r.models.map(m => `
    <label class="dict-model ${m.id === o.model ? "on" : ""}">
      <input type="radio" name="dictModel" value="${esc(m.id)}" ${m.id === o.model ? "checked" : ""}>
      <span><strong>${esc(m.label)}</strong> <span class="muted small">${esc(m.id)}, ${Math.round(m.bytes / 1e6)} MB</span>
        <small class="muted">${esc(m.note)}</small></span>
      ${m.present ? `<span class="chip ok">${icon("check")}geladen</span>` : ""}
    </label>`).join("");

  const d = r.download;
  const loading = d.running;
  $("dictProgress").hidden = !loading;
  if (loading) {
    const pct = d.total ? Math.round(100 * d.received / d.total) : 0;
    $("dictProgress").querySelector("i").style.width = pct + "%";
    $("dictProgressText").textContent = `${esc(d.model)}: ${Math.round(d.received / 1e6)} von ${Math.round(d.total / 1e6)} MB`;
  }
  $("dictDownload").disabled = loading;

  $("dictStatus").innerHTML = r.ready
    ? `<div class="doc-verdict ok">${icon("check")}<div><strong>Bereit</strong><div class="muted small">Im Chat erscheint der Knopf Diktieren.</div></div></div>`
    : `<div class="doc-verdict warn">${icon("alert")}<div><strong>${o.enabled ? "Sprachmodell fehlt" : "Ausgeschaltet"}</strong>
        <div class="muted small">${d.error ? esc(d.error) : o.enabled ? "Modell auswählen, laden und speichern. Es wird nur einmal heruntergeladen und läuft danach ohne Internet." : ""}</div></div></div>`;

  clearTimeout(dictationPoll);
  if (loading) dictationPoll = setTimeout(() => loadDictation().catch(() => {}), 1000);
  else if (d.model && !d.error) reloadMe();
}

function selectedDictationModel() {
  return document.querySelector("input[name=dictModel]:checked")?.value || "small";
}

async function saveDictation() {
  await api("PUT", "/admin/dictation", {
    enabled: $("dictEnabled").checked,
    model: selectedDictationModel(),
    modelDir: $("dictDir").value,
    threads: Number($("dictThreads").value) || 0,
    language: "de",
  });
}

$("dictModels").addEventListener("change", () => {
  for (const l of $("dictModels").querySelectorAll(".dict-model")) l.classList.toggle("on", l.querySelector("input").checked);
});

$("dictSave").addEventListener("click", async () => {
  try {
    await saveDictation();
    toast("Diktieren gespeichert");
    await loadDictation();
    await reloadMe();
  } catch (err) { toast(err.message, true); }
});

$("dictDownload").addEventListener("click", async () => {
  try {
    await saveDictation();
    await api("POST", "/admin/dictation/download", { model: selectedDictationModel() });
    await loadDictation();
  } catch (err) { toast(err.message, true); }
});

async function loadRules() {
  state.settings = await api("GET", "/admin/settings");
  loadOcr().catch(err => toast(err.message, true));
  loadDictation().catch(err => toast(err.message, true));
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
  $("iCheck").checked = i.checkAnswers !== false;
  $("iCheckNote").checked = i.checkNoteForPrograms !== false;
  $("iCheckNote").disabled = !$("iCheck").checked;
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
    checkAnswers: $("iCheck").checked,
    checkNoteForPrograms: $("iCheckNote").checked,
  };
}

let instructionsSeq = 0;

function syncInstructions(immediate = false) {
  $("styleGrid").classList.toggle("off", !$("iEnabled").checked);
  $("iCustomCount").textContent = `${$("iCustom").value.length} / 4000`;
  clearTimeout(ruleState.previewTimer);
  ruleState.previewTimer = setTimeout(async () => {
    // Wie im Chat: eine ältere, langsamere Antwort darf die neue Vorschau nicht überschreiben
    const seq = ++instructionsSeq;
    try {
      const r = await api("POST", "/admin/instructions/preview", instructionInput());
      if (seq !== instructionsSeq) return;
      const footer = $("iFooter").value.trim();
      const text = r.text + (footer ? `${NL}${NL}Unter jeder Antwort: ${footer}` : "");
      $("iPreview").textContent = text.trim() || "Keine Anweisungen. Die KI bekommt nur die Frage.";
      $("iPreview").classList.toggle("empty", !text.trim());
    } catch { /* Vorschau ist nicht wichtig genug für eine Fehlermeldung */ }
  }, immediate ? 0 : 250);
}

$("iCheck").addEventListener("change", () => $("iCheckNote").disabled = !$("iCheck").checked);

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

// Auch beim Regeltest darf eine ältere Antwort die neuere nicht überschreiben
let ruleTestSeq = 0;

function syncRuleForm() {
  const label = $("rLabel").value.trim().toUpperCase() || "EIGENE";
  $("rLabelPreview").textContent = `Wird zu [${label}_1]. Nur Großbuchstaben, 2 bis 20 Zeichen.`;

  clearTimeout(ruleState.testTimer);
  ruleState.testTimer = setTimeout(async () => {
    const seq = ++ruleTestSeq;
    const text = $("rTest").value;
    if (!text.trim() || !$("rPatterns").value.trim()) { $("rTestResult").innerHTML = ""; return; }
    try {
      const r = await api("POST", "/admin/rules/test", { rule: ruleInput(), text });
      if (seq !== ruleTestSeq) return;
      if (!r.hits.length) { $("rTestResult").innerHTML = chip("kein Treffer", "", "x"); return; }
      let html = "", pos = 0;
      for (const h of r.hits.sort((a, b) => a.start - b.start)) {
        if (h.start < pos) continue;
        html += esc(text.slice(pos, h.start)) + `<mark>${esc(text.slice(h.start, h.start + h.length))}</mark>`;
        pos = h.start + h.length;
      }
      $("rTestResult").innerHTML = chip(`${r.hits.length} ${r.hits.length === 1 ? "Treffer" : "Treffer"}`, "ok", "check") + "<div>" + html + esc(text.slice(pos)) + "</div>";
    } catch (err) {
      if (seq !== ruleTestSeq) return;
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
  $("idleMinutes").value = settings.idleLogoutMinutes ?? 30;
  renderUsers();
}

$("idleSave").addEventListener("click", async () => {
  try {
    await api("PUT", "/admin/session", { idleLogoutMinutes: Number($("idleMinutes").value) || 0 });
    toast("Gespeichert");
    await reloadMe();
  } catch (err) { toast(err.message, true); }
});

// Abmelden nach Inaktivität. Alle offenen Tabs zählen zusammen, sonst würde ein vergessener zweiter Tab
// auch den abmelden, der gerade arbeitet. Gespeichert wird nur die Uhrzeit der letzten Eingabe.
const IDLE_KEY = "zwijg.lastActivity";
let idleLocal = Date.now(), idleWritten = 0;

function markActive() {
  idleLocal = Date.now();
  if (idleLocal - idleWritten > 5000) {
    idleWritten = idleLocal;
    try { localStorage.setItem(IDLE_KEY, String(idleLocal)); } catch { }
  }
  if (!$("idleWarning").hidden) $("idleWarning").hidden = true;
}

function lastActive() {
  try { return Math.max(idleLocal, Number(localStorage.getItem(IDLE_KEY)) || 0); } catch { return idleLocal; }
}

for (const ev of ["mousemove", "keydown", "click", "scroll", "touchstart"])
  document.addEventListener(ev, markActive, { passive: true, capture: true });

$("idleStay").addEventListener("click", () => { idleWritten = 0; markActive(); });

setInterval(() => {
  const minutes = state.me?.idleLogoutMinutes;
  if (!minutes) return;
  const idle = Date.now() - lastActive();
  if (idle >= minutes * 60000) logout("Wegen Inaktivität abgemeldet. Bitte neu anmelden.");
  else if (idle >= minutes * 60000 - 60000) $("idleWarning").hidden = false;
}, 5000);

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
      return `<tr data-id="${esc(u.id)}" class="${u.active ? "" : "locked"}">
        <td><div class="user-cell"><span class="avatar small ${u.active ? "" : "off"}">${esc(initials(u.name))}</span>
          <div><strong>${esc(u.name)}</strong>${u.id === state.me.id ? ' <span class="muted small">(du)</span>' : ""}
          <div class="sub">${esc(u.username)}${u.hasPassword ? "" : " · nur Schlüssel"}${u.keyHint ? "" : " · ohne Schlüssel"}${u.mustChangePassword ? " · Startpasswort" : ""}${u.note ? " · " + esc(u.note) : ""}</div></div></div></td>
        <td>${u.admin ? '<span class="badge admin">Admin</span>' : '<span class="badge neutral">Mitarbeiter</span>'}</td>
        <td><div class="perm-icons">${perm(u.showPreview, "eye", "Das sieht die KI")}${perm(u.canUseDocuments, "doc", "Dokumente")}${perm(u.cloudAllowed, "cloud", "Cloud erlaubt")}</div></td>
        <td class="nowrap">${u.dailyLimit ? `${st.today || 0} / ${u.dailyLimit}` : '<span class="muted">keins</span>'}</td>
        <td class="nowrap">${relTime(st.lastSeen)}</td>
        <td class="num">${st.month || 0}${st.blocked ? ` <span class="badge off" title="blockiert">${st.blocked}</span>` : ""}</td>
        <td>${u.active ? '<span class="badge ok">aktiv</span>' : '<span class="badge off">gesperrt</span>'}</td>
        <td><button class="icon-btn" data-menu="${esc(u.id)}" aria-label="Aktionen">${icon("dots")}</button></td>
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
    return `<div class="notice ${a.level} ${cls === "ok" ? "" : "inactive"}" data-id="${esc(a.id)}">
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
    `<label><input type="checkbox" value="${esc(u.id)}" ${picked.has(u.id) ? "checked" : ""}>${esc(u.name)}</label>`).join("");
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

// Suchbegriffe im Text markieren. Erst am Rohtext trennen, dann jedes Stück escapen,
// sonst zerlegt eine Suche nach "amp" oder "39" die HTML-Entities.
function highlight(text) {
  const words = $("auditSearch").value.trim().split(/\s+/).filter(w => w.length >= 2 && !w.startsWith("#"));
  if (!words.length) return esc(text);

  // Mit Klammer im Muster liefert split die Treffer an den ungeraden Stellen
  const pattern = new RegExp("(" + words
    .sort((a, b) => b.length - a.length)
    .map(w => w.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")).join("|") + ")", "gi");
  return String(text ?? "").split(pattern)
    .map((part, i) => i % 2 ? `<mark class="hit">${esc(part)}</mark>` : esc(part))
    .join("");
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

loadBranding();
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
  const item = c => `<div class="conv-item ${c.id === state.conversationId ? "active" : ""}" data-conv="${esc(c.id)}" tabindex="0"
      title="${esc(c.title)}, ${relTime(c.updated)}">
      <span class="conv-lead">${c.pinned ? icon("pin") : ""}</span>
      <span class="conv-title">${esc(c.title)}</span>
      <span class="conv-time">${shortTime(c.updated)}</span>
      <button class="conv-dots" data-conv-menu="${esc(c.id)}" aria-label="Aktionen">${icon("dots")}</button>
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
    setPatient(c.patient || "");
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
