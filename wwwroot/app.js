"use strict";

const POLL_MS = 5000;   
const TICK_MS = 1000;   

const GAUGE_RADIUS = 26;
const GAUGE_CIRCUMFERENCE = 2 * Math.PI * GAUGE_RADIUS;

const STATE_TEXT = {
  Up: "ÇALIŞIYOR",
  Warning: "UYARI",
  Down: "SORUNLU",
  Offline: "KAPALI",
  Unknown: "BİLİNMİYOR"
};

const CATEGORY_KEY = "serverMonitorCategory";

const KIND_ICON = {
  Server: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="7" rx="2"></rect><rect x="3" y="13" width="18" height="7" rx="2"></rect><path d="M7 7.5h.01M7 16.5h.01"></path></svg>',
  Computer: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="12" rx="2"></rect><path d="M8 20h8M12 16v4"></path></svg>'
};

const state = {
  servers: [],
  summary: null,
  lastRunUtc: null,
  intervalSeconds: 30,
  definitions: [],
  openId: null,
  editingId: null,
  category: readCategory(),
  groupFilter: ""   
};

function readCategory() {
  try {
    const saved = localStorage.getItem(CATEGORY_KEY);
    return saved === "Server" || saved === "Computer" ? saved : "all";
  } catch {
    return "all";
  }
}


function displayState(s) {
  return s.category === "Computer" && s.state === "Down" && s.online === false ? "Offline" : s.state;
}

let pollTimer = null;
let tickTimer = null;

const el = id => document.getElementById(id);

const dom = {
  content: el("content"),
  passive: el("passiveDevices"),
  summary: el("summary"),
  healthBar: el("healthBar"),
  themeBtn: el("themeBtn"),
  settingsBtn: el("settingsBtn"),
  logoutBtn: el("logoutBtn"),
  settingsModal: el("settingsModal"),
  settingsClose: el("settingsClose"),
  filter: el("filter"),
  interval: el("interval"),
  refreshBtn: el("refreshBtn"),
  addBtn: el("addBtn"),
  autoRefresh: el("autoRefresh"),
  lastRun: el("lastRun"),
  toast: el("toast"),

  backdrop: el("backdrop"),
  drawer: el("drawer"),
  drawerTitle: el("drawerTitle"),
  drawerHost: el("drawerHost"),
  drawerBody: el("drawerBody"),
  drawerClose: el("drawerClose"),
  drawerEdit: el("drawerEdit"),
  drawerRefresh: el("drawerRefresh"),

  modal: el("formModal"),
  form: el("serverForm"),
  formTitle: el("formTitle"),
  formError: el("formError"),
  formDelete: el("formDelete"),
  formClose: el("formClose"),
  formCancel: el("formCancel"),
  formSave: el("formSave"),
  portRows: el("portRows"),
  httpRows: el("httpRows"),
  addPort: el("addPort"),
  addHttp: el("addHttp"),
  groupList: el("groupList"),
  wmiFields: el("wmiFields"),
  wmiPassHint: el("wmiPassHint")
};


async function api(path, options) {
  const res = await fetch(path, {
    headers: { "Content-Type": "application/json" },
    ...options
  });

  
  if (res.status === 401 && !path.startsWith("/api/auth")) {
    location.replace("/login.html");
    throw new Error("Oturum açmanız gerekiyor.");
  }

  if (!res.ok) {
    let message = "HTTP " + res.status;
    try {
      const body = await res.json();
      if (body && (body.message || body.detail)) message = body.message || body.detail;
    } catch {  }
    throw new Error(message);
  }

  return res.status === 204 ? null : res.json();
}

function toast(message, isError) {
  dom.toast.textContent = message;
  dom.toast.classList.toggle("error", !!isError);
  dom.toast.hidden = false;
  clearTimeout(toast._timer);
  toast._timer = setTimeout(() => { dom.toast.hidden = true; }, 4000);
}


async function loadStatus() {
  try {
    const data = await api("/api/status", { cache: "no-store" });
    state.servers = data.servers || [];
    state.summary = data.summary;
    state.lastRunUtc = data.lastRunUtc;
    state.intervalSeconds = data.intervalSeconds;

    if (document.activeElement !== dom.interval) {
      dom.interval.value = data.intervalSeconds;
    }

    document.body.classList.add("loaded");
    render();
  } catch (err) {
    dom.lastRun.textContent = "Bağlantı hatası: " + err.message;
  }
}

async function loadDefinitions() {
  try {
    state.definitions = await api("/api/servers");
    const groups = [...new Set(state.definitions.map(d => d.group).filter(Boolean))];
    dom.groupList.innerHTML = groups.map(g => `<option value="${esc(g)}">`).join("");


    passiveSignature = "__reload__";
    renderDisabled();
  } catch (err) {
    console.error(err);
  }
}

async function refreshAll() {
  dom.refreshBtn.disabled = true;
  dom.refreshBtn.classList.add("busy");
  try {
    const data = await api("/api/refresh", { method: "POST" });
    state.servers = data.servers;
    state.summary = data.summary;
    state.lastRunUtc = new Date().toISOString();
    render();
  } catch (err) {
    toast("Kontrol başarısız: " + err.message, true);
  } finally {
    dom.refreshBtn.disabled = false;
    dom.refreshBtn.classList.remove("busy");
  }
}

async function refreshOne(id) {
  try {
    const updated = await api("/api/refresh/" + encodeURIComponent(id), { method: "POST" });
    state.servers = state.servers.map(s => (s.id === updated.id ? updated : s));
    render();
  } catch (err) {
    toast("Kontrol başarısız: " + err.message, true);
  }
}



function render() {
  updateGroupFilter();
  renderSummary();
  renderCards();
  renderDisabled();
  renderFooter();
  if (state.openId) renderDrawer();
}



let passiveSignature = "";

function renderDisabled() {
  if (!dom.passive) return;

  const term = dom.filter.value.trim().toLocaleLowerCase("tr");
  const disabled = (state.definitions || [])
    .filter(d => d.enabled === false)
    .filter(d => state.category === "all" || (d.category || "Server") === state.category)
    .filter(d => matchesGroupFilter(d.group))
    .filter(d => !term ||
      d.name.toLocaleLowerCase("tr").includes(term) ||
      d.host.toLocaleLowerCase("tr").includes(term) ||
      (d.group || "").toLocaleLowerCase("tr").includes(term));


  const signature = state.category + "|" + state.groupFilter + "|" + term + "|" + disabled.map(d => d.id).join(",");
  if (signature === passiveSignature) return;
  passiveSignature = signature;

  if (disabled.length === 0) {
    dom.passive.innerHTML = "";
    return;
  }

  dom.passive.innerHTML = `
    <h2 class="group-title"><span>Pasif cihazlar</span><em>${disabled.length}</em></h2>
    <div class="passive-grid">${disabled.map(passiveChipHtml).join("")}</div>
    <p class="hint passive-hint">İzleme dışı bırakılmış cihazlar. Geri almak için tıklayıp <strong>İzlemeye dahil et</strong>'i işaretleyin.</p>`;

  dom.passive.querySelectorAll("[data-edit]").forEach(chip => {
    chip.addEventListener("click", () => {
      const def = state.definitions.find(d => d.id === chip.dataset.edit);
      if (def) openForm(def);
    });
  });
}

function passiveChipHtml(d) {
  return `
    <button type="button" class="passive-chip" data-edit="${esc(d.id)}" title="Düzenle ve geri al">
      <span class="passive-kind">${KIND_ICON[d.category || "Server"] || ""}</span>
      <span class="passive-ident">
        <span class="passive-name">${esc(d.name)}</span>
        <span class="passive-host">${esc(d.host)}</span>
      </span>
      <span class="passive-badge">PASİF</span>
    </button>`;
}



let animationSeq = 0;


function animateNumber(node, target) {
  const from = Number(node.dataset.value ?? 0);
  if (from === target) {
    node.textContent = target;
    return;
  }

  node.dataset.value = target;

  if (prefersReducedMotion()) {
    node.textContent = target;
    return;
  }

  const token = ++animationSeq;
  node.dataset.anim = token;
  const started = performance.now();
  const duration = 450;

  const step = now => {
    if (Number(node.dataset.anim) !== token) return;  
    const t = Math.min(1, (now - started) / duration);
    const eased = 1 - Math.pow(1 - t, 3);
    node.textContent = Math.round(from + (target - from) * eased);
    if (t < 1) requestAnimationFrame(step);
  };

  requestAnimationFrame(step);
}


function parseGroup(raw) {
  const g = (raw || "Genel").trim() || "Genel";
  const parts = g.split(/\s*[\/>]\s*/).map(p => p.trim()).filter(Boolean);
  return { top: parts[0] || "Genel", sub: parts.slice(1).join(" / ") };
}


function normGroup(raw) {
  const { top, sub } = parseGroup(raw);
  return sub ? `${top} / ${sub}` : top;
}


function matchesGroupFilter(groupStr) {
  if (!state.groupFilter) return true;
  const sel = state.groupFilter;
  if (/[\/>]/.test(sel)) return normGroup(groupStr) === normGroup(sel);  
  return parseGroup(groupStr).top === sel;                              
}


function categoryServers() {
  return state.servers.filter(s =>
    (state.category === "all" || (s.category || "Server") === state.category) &&
    matchesGroupFilter(s.group));
}


let groupFilterSig = "";

function updateGroupFilter() {
  const select = el("groupFilter");
  if (!select) return;


  const tops = new Map();
  for (const s of [...(state.servers || []), ...(state.definitions || [])]) {
    if (!s.group) continue;
    const { top, sub } = parseGroup(s.group);
    if (!tops.has(top)) tops.set(top, new Set());
    if (sub) tops.get(top).add(sub);
  }

  const allValues = [];
  for (const top of tops.keys()) {
    allValues.push(top);
    for (const sub of tops.get(top)) allValues.push(`${top} / ${sub}`);
  }


  if (state.groupFilter && !allValues.includes(state.groupFilter)) state.groupFilter = "";

 
  const sig = state.groupFilter + "|" + allValues.join(",");
  if (sig === groupFilterSig) return;
  groupFilterSig = sig;

  const sel = state.groupFilter;
  let html = '<option value="">Tüm bölümler</option>';
  for (const top of [...tops.keys()].sort((a, b) => a.localeCompare(b, "tr"))) {
    html += `<option value="${esc(top)}"${top === sel ? " selected" : ""}>${esc(top)}</option>`;
    for (const sub of [...tops.get(top)].sort((a, b) => a.localeCompare(b, "tr"))) {
      const val = `${top} / ${sub}`;
      html += `<option value="${esc(val)}"${val === sel ? " selected" : ""}>&nbsp;&nbsp;└ ${esc(sub)}</option>`;
    }
  }
  select.innerHTML = html;
}

function computeSummary(servers) {
  const summary = { total: servers.length, up: 0, warning: 0, down: 0, offline: 0, unknown: 0 };
  for (const server of servers) {
    const key = { Up: "up", Warning: "warning", Down: "down", Offline: "offline" }[displayState(server)] || "unknown";
    summary[key]++;
  }
  return summary;
}

function renderSummary() {
  const s = computeSummary(categoryServers());

  dom.summary.querySelectorAll("[data-stat]").forEach(node => {
    animateNumber(node, s[node.dataset.stat] ?? 0);
  });

  renderCategoryTabs();


  const all = computeSummary(state.servers);
  const worst = all.down > 0 ? "Down" : all.warning > 0 ? "Warning" : "Up";

  const dot = document.querySelector(".brand-dot");
  if (dot) dot.className = "brand-dot state-" + worst;


  if (document.body.dataset.health !== worst) document.body.dataset.health = worst;

  if (!dom.healthBar) return;

  const total = s.total || 0;
  dom.healthBar.innerHTML = total === 0 ? "" : [
    ["Up", s.up, "çalışıyor"],
    ["Warning", s.warning, "uyarı"],
    ["Down", s.down, "sorunlu"],
    ["Offline", s.offline, "kapalı"],
    ["Unknown", s.unknown, "bilinmiyor"]
  ].filter(([, count]) => count > 0)
   .map(([key, count, label]) =>
     `<span class="seg-${key}" style="width:${(count / total) * 100}%" title="${count} ${label}"></span>`)
   .join("");
}

function renderCategoryTabs() {
  const tabs = el("categoryTabs");
  if (!tabs) return;

  const counts = {
    all: state.servers.length,
    Server: state.servers.filter(s => (s.category || "Server") === "Server").length,
    Computer: state.servers.filter(s => s.category === "Computer").length
  };

  tabs.querySelectorAll(".cat-tab").forEach(tab => {
    tab.classList.toggle("active", tab.dataset.category === state.category);
  });

  tabs.querySelectorAll("[data-count]").forEach(node => {
    setText(node, String(counts[node.dataset.count] ?? 0));
  });
}

function selectCategory(category) {
  state.category = category;
  try { localStorage.setItem(CATEGORY_KEY, category); } catch {  }
  renderSummary();
  renderCards();
  renderDisabled();
}



const cardNodes = new Map();
let layoutSignature = "";

function visibleServers() {
  const servers = categoryServers();
  const term = dom.filter.value.trim().toLocaleLowerCase("tr");
  if (!term) return servers;

  return servers.filter(s =>
    s.name.toLocaleLowerCase("tr").includes(term) ||
    s.host.toLocaleLowerCase("tr").includes(term) ||
    (s.group || "").toLocaleLowerCase("tr").includes(term));
}

function groupServers(servers) {
  const order = { Down: 0, Warning: 1, Unknown: 2, Up: 3, Offline: 4 };
  const tops = new Map();   

  for (const server of servers) {
    const { top, sub } = parseGroup(server.group);
    if (!tops.has(top)) tops.set(top, new Map());
    const subs = tops.get(top);
    if (!subs.has(sub)) subs.set(sub, []);
    subs.get(sub).push(server);
  }

  for (const subs of tops.values())
    for (const list of subs.values())
      list.sort((a, b) =>
        (order[displayState(a)] - order[displayState(b)]) || a.name.localeCompare(b.name, "tr"));

  return tops;
}


function sortedSubKeys(subs) {
  return [...subs.keys()].sort((a, b) => (a === "" ? -1 : b === "" ? 1 : a.localeCompare(b, "tr")));
}

function renderCards() {
  const servers = visibleServers();

  if (servers.length === 0) {
    const empty = categoryServers().length === 0;
    const noun = { Server: "sunucu", Computer: "bilgisayar" }[state.category] || "cihaz";

    dom.content.innerHTML = `<p class="placeholder">${
      !empty
        ? "Aramayla eşleşen " + noun + " yok."
        : `Henüz izlenen ${noun} yok. Sağ üstteki <strong>+ Sunucu ekle</strong> ile ekleyip türünü seçin.`
    }</p>`;
    cardNodes.clear();
    layoutSignature = "";
    return;
  }

  const groups = groupServers(servers);
  const signature = [...groups]
    .map(([top, subs]) => top + ":" +
      sortedSubKeys(subs).map(sub => sub + ">" + subs.get(sub).map(s => s.id).join(",")).join(";"))
    .join("|");


  if (signature !== layoutSignature) {
    buildLayout(groups);
    layoutSignature = signature;
  }

  for (const server of servers) {
    const node = cardNodes.get(server.id);
    if (node) updateCard(server, node);
  }
}

function buildLayout(groups) {
  const liveIds = new Set();
  for (const subs of groups.values())
    for (const list of subs.values())
      list.forEach(s => liveIds.add(s.id));

  for (const id of [...cardNodes.keys()]) {
    if (!liveIds.has(id)) cardNodes.delete(id);
  }

  dom.content.innerHTML = "";
  let position = 0;

  const appendGrid = list => {
    const grid = document.createElement("div");
    grid.className = "grid";
    for (const server of list) {
      let node = cardNodes.get(server.id);
      if (!node) {
        node = createCard(server, position);
        cardNodes.set(server.id, node);
      }
      grid.appendChild(node.root);
      position++;
    }
    dom.content.appendChild(grid);
  };

  for (const [top, subs] of groups) {
    const total = [...subs.values()].reduce((n, l) => n + l.length, 0);

    const title = document.createElement("h2");
    title.className = "group-title";
    title.innerHTML = `<span>${esc(top)}</span><em>${total} cihaz</em>`;
    dom.content.appendChild(title);

    for (const sub of sortedSubKeys(subs)) {
      if (sub !== "") {
        const subTitle = document.createElement("h3");
        subTitle.className = "subgroup-title";
        subTitle.innerHTML = `<span>${esc(sub)}</span><em>${subs.get(sub).length}</em>`;
        dom.content.appendChild(subTitle);
      }
      appendGrid(subs.get(sub));
    }
  }
}

function createCard(server, position) {
  const root = document.createElement("article");
  root.className = "card card-enter";
  root.dataset.id = server.id;
  root.style.animationDelay = Math.min(position * 60, 420) + "ms";

  root.innerHTML = `
    <div class="card-head">
      <div class="card-ident">
        <div class="card-title-row">
          <span class="card-kind"></span>
          <div class="card-name"></div>
        </div>
        <div class="card-host"></div>
      </div>
      <span class="badge"></span>
    </div>
    <div class="gauges"></div>
    <div class="checks"></div>
    <div class="drive-line"></div>
    <div class="mini-wrap"></div>
    <div class="spark-wrap"></div>
    <div class="card-foot">
      <span class="foot-time"></span>
      <span class="foot-uptime"></span>
      <span class="foot-hint">grafikler için tıkla →</span>
    </div>`;


  root.addEventListener("animationend", () => root.classList.remove("card-enter"), { once: true });
  root.addEventListener("click", () => openDrawer(server.id));

  return {
    root,
    refs: {
      name: root.querySelector(".card-name"),
      kind: root.querySelector(".card-kind"),
      host: root.querySelector(".card-host"),
      badge: root.querySelector(".badge"),
      gauges: root.querySelector(".gauges"),
      checks: root.querySelector(".checks"),
      driveLine: root.querySelector(".drive-line"),
      mini: root.querySelector(".mini-wrap"),
      spark: root.querySelector(".spark-wrap"),
      time: root.querySelector(".foot-time"),
      uptime: root.querySelector(".foot-uptime")
    }
  };
}

function updateCard(server, node) {
  const { root, refs } = node;
  const shown = displayState(server);
  const category = server.category || "Server";

  if (root.dataset.state !== shown) {
    root.dataset.state = shown;
    root.classList.remove("state-Up", "state-Warning", "state-Down", "state-Offline", "state-Unknown");
    root.classList.add("state-" + shown);
    refs.badge.className = "badge state-" + shown;
    refs.badge.textContent = STATE_TEXT[shown] || shown;
  }

  setHtml(refs.kind, KIND_ICON[category] || "");
  if (refs.kind) refs.kind.title = category === "Computer" ? "Bilgisayar" : "Sunucu";

  setText(refs.name, server.name);
  setText(refs.host, server.host);

  if (shown === "Offline") {

    updateGauges(refs.gauges, null);
    setHtml(refs.checks, `<div class="offline-note">Cihaz kapalı veya ağda değil${lastSeenText(server)}</div>`);
    setHtml(refs.driveLine, "");
    setHtml(refs.mini, "");
  } else {
    updateGauges(refs.gauges, server.metrics);
    setHtml(refs.checks, (server.checks || []).map(checkRowHtml).join(""));
    setHtml(refs.driveLine, driveLineHtml(server.metrics));
    setHtml(refs.mini, miniResponseHtml(server.history));
  }

  setHtml(refs.spark, sparkHtml(server.history, category));

  setText(refs.time, lastCheckText(server));
  setText(refs.uptime, "%" + (server.uptimePercent ?? 0) + (category === "Computer" ? " açık" : " uptime"));
}


function lastSeenText(server) {
  const lastOnline = [...(server.history || [])].reverse().find(p => p.state !== "Down");
  const when = lastOnline?.timestampUtc || server.lastUpUtc;
  return when ? " · son açık: " + relativeTime(when) : "";
}

function relativeTime(iso) {
  const diff = Math.round((Date.now() - new Date(iso).getTime()) / 1000);
  if (diff < 60) return Math.max(0, diff) + " sn önce";
  if (diff < 3600) return Math.round(diff / 60) + " dk önce";
  if (diff < 86400) return Math.round(diff / 3600) + " sa önce";
  return new Date(iso).toLocaleString("tr-TR", { day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit" });
}

function checkRowHtml(c) {
  return `
    <div class="check">
      <span class="dot state-${c.state}"></span>
      <span class="check-label">${esc(c.label)}</span>
      <span class="check-msg">${esc(c.message || "")}</span>
    </div>`;
}

function gaugeSpecs(metrics) {
  if (!metrics) return [];

  const specs = [];

  if (isNum(metrics.cpuPercent)) {
    specs.push({ key: "cpu", label: "CPU", percent: metrics.cpuPercent, detail: "" });
  }

  if (isNum(metrics.memoryPercent)) {
    specs.push({
      key: "ram",
      label: "RAM",
      percent: metrics.memoryPercent,
      detail: metrics.memoryTotalBytes ? bytes(metrics.memoryTotalBytes) : ""
    });
  }

  const fullest = (metrics.drives || []).reduce(
    (worst, d) => (!worst || d.usedPercent > worst.usedPercent ? d : worst), null);

  if (fullest) {
    specs.push({
      key: "disk",
      label: "DİSK " + fullest.name,
      percent: fullest.usedPercent,
      detail: bytes(fullest.freeBytes) + " boş"
    });
  }

  return specs;
}


function updateGauges(container, metrics, animate = true) {
  const specs = gaugeSpecs(metrics);
  const signature = specs.map(s => s.key).join("|");

  if (container.dataset.sig !== signature) {
    container.dataset.sig = signature;
    container.innerHTML = specs.map(gaugeMarkup).join("");

    if (animate) {

      requestAnimationFrame(() => applyGaugeValues(container, specs));
      return;
    }
  }

  applyGaugeValues(container, specs);
}

function gaugeMarkup(spec) {
  return `
    <div class="gauge" data-gauge="${esc(spec.key)}">
      <svg viewBox="0 0 64 64">
        <circle class="gauge-track" cx="32" cy="32" r="${GAUGE_RADIUS}"></circle>
        <circle class="gauge-value" cx="32" cy="32" r="${GAUGE_RADIUS}"
                stroke-dasharray="0 ${GAUGE_CIRCUMFERENCE.toFixed(1)}"></circle>
        <text class="gauge-text" x="32" y="37" text-anchor="middle">–</text>
      </svg>
      <div class="gauge-label"></div>
      <div class="gauge-detail"></div>
    </div>`;
}

function applyGaugeValues(container, specs) {
  for (const spec of specs) {
    const node = container.querySelector(`[data-gauge="${spec.key}"]`);
    if (!node) continue;

    const percent = clamp(spec.percent, 0, 100);
    const dash = (percent / 100) * GAUGE_CIRCUMFERENCE;
    const ring = node.querySelector(".gauge-value");

    ring.setAttribute("stroke-dasharray", `${dash.toFixed(1)} ${(GAUGE_CIRCUMFERENCE - dash).toFixed(1)}`);
    ring.setAttribute("stroke", thresholdColor(percent));

    setText(node.querySelector(".gauge-text"), Math.round(percent) + "%");
    setText(node.querySelector(".gauge-label"), spec.label);
    setText(node.querySelector(".gauge-detail"), spec.detail || "");
  }
}

const thresholdColor = p => (p >= 95 ? "var(--down)" : p >= 85 ? "var(--warn)" : "var(--up)");


function driveLineHtml(metrics) {
  const drives = metrics?.drives || [];
  const error = metrics?.error ? `<div class="error-note">${esc(metrics.error)}</div>` : "";

  if (drives.length < 2) return error;

  const text = drives.map(d => `${d.name} %${Math.round(d.usedPercent)}`).join(" · ");
  return `<div class="drive-summary">${esc(text)}</div>` + error;
}


function miniResponseHtml(history) {
  const points = (history || []).map(p => (isNum(p.responseMs) ? p.responseMs : null));
  const values = points.filter(isNum);
  if (values.length < 2) return "";

  const W = 240, H = 34;
  const max = Math.max(...values) * 1.2 || 1;
  const xAt = i => (i / (points.length - 1)) * W;
  const yAt = v => H - 2 - (v / max) * (H - 6);

  const segments = [];
  let current = [];

  points.forEach((v, i) => {
    if (isNum(v)) current.push(`${xAt(i).toFixed(1)},${yAt(v).toFixed(1)}`);
    else if (current.length) { segments.push(current); current = []; }
  });
  if (current.length) segments.push(current);

  const lines = segments
    .filter(seg => seg.length > 1)
    .map(seg => `<polyline points="${seg.join(" ")}" fill="none" stroke="var(--accent)"
                            stroke-width="1.6" stroke-linejoin="round" stroke-linecap="round"></polyline>`)
    .join("");

  return `
    <div class="mini">
      <span class="mini-label">yanıt ${Math.round(values[values.length - 1])} ms</span>
      <svg viewBox="0 0 ${W} ${H}" preserveAspectRatio="none">${lines}</svg>
    </div>`;
}

function sparkHtml(history, category) {
  if (!history || history.length < 2) return "";


  const stateOf = p => (category === "Computer" && p.state === "Down" ? "Offline" : p.state);

  return '<div class="spark">' + history.slice(-40).map(p =>
    `<i class="state-${stateOf(p)}" title="${new Date(p.timestampUtc).toLocaleTimeString("tr-TR")}"></i>`).join("") + "</div>";
}

function renderFooter() {
  dom.lastRun.textContent = state.lastRunUtc
    ? `Son güncelleme: ${new Date(state.lastRunUtc).toLocaleString("tr-TR")} · kontrol aralığı ${state.intervalSeconds} sn`
    : "Henüz kontrol yapılmadı.";
}


function tickRelativeTimes() {
  for (const server of state.servers) {
    const node = cardNodes.get(server.id);
    if (node) setText(node.refs.time, lastCheckText(server));
  }
}

function lineChart(points, opts) {
  const W = 620, H = 150, PAD_L = 40, PAD_R = 10, PAD_T = 12, PAD_B = 20;
  const values = points.map(p => p.v).filter(isNum);

  if (values.length === 0) return '<div class="chart-empty">Henüz veri yok.</div>';

  const max = opts.max || niceMax(Math.max(...values));
  const innerW = W - PAD_L - PAD_R;
  const innerH = H - PAD_T - PAD_B;
  const baseY = H - PAD_B;

  const xAt = i => points.length < 2 ? PAD_L + innerW / 2 : PAD_L + (i / (points.length - 1)) * innerW;
  const yAt = v => baseY - (clamp(v, 0, max) / max) * innerH;

  let grid = "";
  for (let i = 0; i <= 2; i++) {
    const value = (max / 2) * i;
    const y = yAt(value);
    grid += `<line class="grid-line" x1="${PAD_L}" y1="${y}" x2="${W - PAD_R}" y2="${y}"></line>`;
    grid += `<text class="axis-text" x="${PAD_L - 6}" y="${y + 3}" text-anchor="end">${fmt(value, opts.decimals)}</text>`;
  }

  const segments = [];
  let current = [];
  points.forEach((p, i) => {
    if (isNum(p.v)) current.push([xAt(i), yAt(p.v)]);
    else if (current.length) { segments.push(current); current = []; }
  });
  if (current.length) segments.push(current);

  let shapes = "";
  for (const seg of segments) {
    if (seg.length === 1) {
      shapes += `<circle cx="${seg[0][0]}" cy="${seg[0][1]}" r="2.5" fill="${opts.color}"></circle>`;
      continue;
    }
    const line = seg.map(([x, y], i) => `${i === 0 ? "M" : "L"}${x.toFixed(1)},${y.toFixed(1)}`).join(" ");
    shapes += `<path d="${line} L${seg[seg.length - 1][0].toFixed(1)},${baseY} L${seg[0][0].toFixed(1)},${baseY} Z"
                     fill="${opts.color}" opacity="0.13"></path>`;
    shapes += `<path class="chart-line" d="${line}" fill="none" stroke="${opts.color}" stroke-width="1.8"
                     stroke-linejoin="round" stroke-linecap="round"></path>`;
  }

  const lastIndex = points.map(p => isNum(p.v)).lastIndexOf(true);
  if (lastIndex >= 0) {
    shapes += `<circle class="chart-head-dot" cx="${xAt(lastIndex)}" cy="${yAt(points[lastIndex].v)}" r="3" fill="${opts.color}"></circle>`;
  }

  const firstTime = points[0] ? timeText(points[0].t) : "";
  const lastTime = points[points.length - 1] ? timeText(points[points.length - 1].t) : "";

  return `
    <svg viewBox="0 0 ${W} ${H}" role="img">
      ${grid}
      ${shapes}
      <text class="axis-text" x="${PAD_L}" y="${H - 5}">${firstTime}</text>
      <text class="axis-text" x="${W - PAD_R}" y="${H - 5}" text-anchor="end">${lastTime}</text>
    </svg>`;
}

function chartBlock(title, points, opts) {
  const values = points.map(p => p.v).filter(isNum);
  const unit = opts.unit || "";
  const dec = opts.decimals ?? 0;

  const now = values.length ? fmt(values[values.length - 1], dec) + unit : "–";
  const stats = values.length
    ? `<div class="chart-stats">
         <span>en düşük ${fmt(Math.min(...values), dec)}${unit}</span>
         <span>ortalama ${fmt(values.reduce((a, b) => a + b, 0) / values.length, dec)}${unit}</span>
         <span>en yüksek ${fmt(Math.max(...values), dec)}${unit}</span>
         <span>${values.length} ölçüm</span>
       </div>`
    : "";

  return `
    <div class="chart">
      <div class="chart-head">
        <span class="chart-title">${esc(title)}</span>
        <span class="chart-now" style="color:${opts.color}">${now}</span>
      </div>
      ${lineChart(points, opts)}
      ${stats}
    </div>`;
}



function openDrawer(id) {
  state.openId = id;


  const server = state.servers.find(x => x.id === id);
  const isComputer = server?.category === "Computer";
  dom.drawerBody.innerHTML = isComputer
    ? '<div id="drawerInfo"></div><div id="drawerApps"></div><div id="drawerDns"></div><div id="drawerPrograms"></div><div id="drawerUsb"></div><div id="drawerLive"></div>'
    : '<div id="drawerInfo"></div><div id="drawerLive"></div><div id="drawerApps"></div><div id="drawerDns"></div>';
  dom.drawerBody.scrollTop = 0;

  appsShowAll = false;
  dom.drawer.hidden = false;
  dom.backdrop.hidden = false;
  renderDrawer();
  startAppsPolling();

  loadInfo();
  if (isComputer) {
    loadPrograms();
    loadUsb();
  }
}

function closeDrawer() {
  state.openId = null;
  stopAppsPolling();
  dom.drawer.hidden = true;
  dom.backdrop.hidden = true;
}

function renderDrawer() {
  const s = state.servers.find(x => x.id === state.openId);
  const live = el("drawerLive");
  if (!live) return;

  if (!s) {
    dom.drawerTitle.textContent = "Cihaz bulunamadı";
    dom.drawerHost.textContent = "";
    live.innerHTML = '<p class="placeholder">Bu cihaz kaldırılmış veya izleme dışı bırakılmış olabilir.</p>';
    return;
  }

  const shown = displayState(s);
  dom.drawerTitle.innerHTML = `${esc(s.name)} <span class="badge state-${shown}">${STATE_TEXT[shown] || shown}</span>`;
  dom.drawerHost.textContent = `${s.host} · ${s.group} · %${s.uptimePercent ?? 0} uptime`;

  const history = s.history || [];
  const series = key => history.map(p => ({ t: new Date(p.timestampUtc), v: isNum(p[key]) ? p[key] : null }));

  const charts = [
    chartBlock("Yanıt süresi", series("responseMs"), { unit: " ms", color: "var(--accent)", decimals: 0 }),
    chartBlock("CPU kullanımı", series("cpuPercent"), { unit: "%", color: "var(--up)", max: 100 }),
    chartBlock("RAM kullanımı", series("memoryPercent"), { unit: "%", color: "var(--warn)", max: 100 }),
    chartBlock("Disk doluluğu (en dolu sürücü)", series("diskPercent"), { unit: "%", color: "var(--down)", max: 100 })
  ].join("");

  const checks = (s.checks || []).map(checkRowHtml).join("");

  const drives = (s.metrics?.drives || []).map(d => barRow(
    "Disk " + d.name,
    d.usedPercent,
    `${bytes(d.totalBytes - d.freeBytes)} / ${bytes(d.totalBytes)}`
  )).join("");

  const osLine = s.metrics?.osName
    ? `<p class="hint">${esc(s.metrics.osName)}${s.metrics.uptime ? " · açık kalma süresi " + esc(uptimeText(s.metrics.uptime)) : ""}</p>`
    : "";

  const scroll = dom.drawerBody.scrollTop;
  const hasGauges = shown !== "Offline" && gaugeSpecs(s.metrics).length > 0;

  live.innerHTML = `
    ${hasGauges ? '<div class="drawer-section"><h3>Şu an</h3><div class="gauges" id="drawerGauges"></div></div>' : ""}

    <div class="drawer-section">
      <h3>Canlı grafikler</h3>
      ${charts}
    </div>

    <div class="drawer-section">
      <h3>Kontroller · ${lastCheckText(s)}</h3>
      <div class="checks">${checks || '<p class="placeholder">Tanımlı kontrol yok.</p>'}</div>
    </div>

    ${drives ? `<div class="drawer-section"><h3>Diskler</h3><div class="metrics">${drives}</div>${osLine}</div>` : osLine}

    ${s.metrics?.error ? `<p class="error-note">${esc(s.metrics.error)}</p>` : ""}`;

  if (hasGauges) {
    updateGauges(el("drawerGauges"), s.metrics, false);
  }

  dom.drawerBody.scrollTop = scroll;
}



const APPS_REFRESH_MS = 15000;
let appsTimer = null;
let appsShowAll = false;
let appsRequestSeq = 0;

function startAppsPolling() {
  stopAppsPolling();
  loadApps();
  loadDns();
  appsTimer = setInterval(() => { loadApps(); loadDns(); }, APPS_REFRESH_MS);
}

function stopAppsPolling() {
  if (appsTimer) clearInterval(appsTimer);
  appsTimer = null;
  appsRequestSeq++; 
}



let dnsShowAll = false;

async function loadDns() {
  const id = state.openId;
  const container = el("drawerDns");
  if (!id || !container) return;

  try {
    const [data, lists] = await Promise.all([
      api(`/api/servers/${encodeURIComponent(id)}/dns?all=${dnsShowAll}`),
      api("/api/dns/lists")
    ]);
    if (state.openId !== id) return;


    if (!data.hasStore) { container.innerHTML = ""; return; }

    const ignoredSet = new Set((lists.ignored || []).map(x => x.toLowerCase()));
    const domains = data.domains || [];
    const meta = data.windowMinutes ? `· son ${Math.round(data.windowMinutes / 60) || 1} sa` : "";


    const toggle = `
      <label class="toggle dns-toggle">
        <input type="checkbox" id="dnsAll" ${dnsShowAll ? "checked" : ""}>
        <span>Filtresiz göster</span>
      </label>`;

    const bind = () => {
      el("dnsAll")?.addEventListener("change", event => {
        dnsShowAll = event.target.checked;
        loadDns();
      });
      container.querySelectorAll("[data-hide]").forEach(b =>
        b.addEventListener("click", () => setDomainHidden(b.dataset.hide, true)));
      container.querySelectorAll("[data-show]").forEach(b =>
        b.addEventListener("click", () => setDomainHidden(b.dataset.show, false)));
    };

    if (domains.length === 0) {
      container.innerHTML = `
        <div class="drawer-section">
          <h3>En çok erişilen alan adları</h3>
          ${toggle}
          <p class="placeholder">Bu cihaz için DNS kaydı yok.${data.updatedUtc ? "" : " (Toplayıcıdan veri bekleniyor.)"}</p>
        </div>`;
      bind();
      return;
    }

    const max = domains[0].count || 1;
    const rows = domains.map(d => {
      const hidden = ignoredSet.has(d.domain.toLowerCase());
 
      const btn = hidden
        ? `<button type="button" class="dns-act show" data-show="${esc(d.domain)}" title="Tekrar göster">geri al</button>`
        : `<button type="button" class="dns-act hide" data-hide="${esc(d.domain)}" title="Bu alan adını listeden gizle">gizle</button>`;
      return `
      <div class="dns-row${hidden ? " is-hidden" : ""}">
        <span class="dns-domain" title="${esc(d.domain)}">${esc(d.domain)}</span>
        <span class="dns-bar"><span style="width:${Math.max(4, (d.count / max) * 100)}%"></span></span>
        <span class="dns-count">${d.count}</span>
        ${btn}
      </div>`;
    }).join("");

    const note = dnsShowAll
      ? "Tüm alan adları gösteriliyor (telemetri/CDN dahil)."
      : "İşletim sistemi, antivirüs, güncelleme ve analitik adresleri gizlenir.";

    container.innerHTML = `
      <div class="drawer-section">
        <h3>En çok erişilen alan adları <span class="h3-meta">${meta}</span></h3>
        ${toggle}
        <div class="dns-list">${rows}</div>
        <p class="hint">${data.updatedUtc ? relativeTime(data.updatedUtc) + " güncellendi" : ""} · yalnızca alan adı; tam adres ve içerik okunmaz. ${note}</p>
      </div>`;
    bind();
  } catch (err) {
    if (state.openId !== id) return;
    container.innerHTML = `<div class="drawer-section"><p class="error-note">${esc(err.message)}</p></div>`;
  }
}


async function setDomainHidden(domain, hide) {
  try {
    await api(hide ? "/api/dns/ignore" : "/api/dns/unignore", {
      method: "POST",
      body: JSON.stringify({ domain })
    });
    toast(hide ? `"${domain}" gizlendi.` : `"${domain}" tekrar gösteriliyor.`);
    loadDns();            
    loadDnsHiddenList();  
  } catch (err) {
    toast("İşlem başarısız: " + err.message, true);
  }
}


async function loadDnsHiddenList() {
  const box = el("dnsHiddenList");
  if (!box) return;

  try {
    const lists = await api("/api/dns/lists");
    const ignored = lists.ignored || [];

    box.innerHTML = ignored.length === 0
      ? '<p class="placeholder">Elle gizlenmiş alan adı yok. (Yerleşik telemetri/CDN filtresi ayrıca çalışır.)</p>'
      : ignored.map(d => `
          <span class="hidden-chip">${esc(d)}<button type="button" data-unhide="${esc(d)}" title="Kaldır (tekrar göster)">✕</button></span>`).join("");

    box.querySelectorAll("[data-unhide]").forEach(b =>
      b.addEventListener("click", () => setDomainHidden(b.dataset.unhide, false)));
  } catch (err) {
    box.innerHTML = `<p class="error-note">${esc(err.message)}</p>`;
  }
}



let infoSeq = 0;

async function loadInfo() {
  const id = state.openId;
  const container = el("drawerInfo");
  if (!id || !container) return;

 
  const def = state.definitions.find(d => d.id === id);
  if (!def?.windowsMetrics?.enabled) { container.innerHTML = ""; return; }

  container.innerHTML = `<div class="drawer-section"><h3>Cihaz bilgileri</h3><div class="apps-loading">Okunuyor…</div></div>`;
  const seq = ++infoSeq;

  try {
    const d = await api(`/api/servers/${encodeURIComponent(id)}/info`);
    if (seq !== infoSeq || state.openId !== id) return;

    if (d.error) {
      container.innerHTML = `<div class="drawer-section"><h3>Cihaz bilgileri</h3><p class="error-note">${esc(d.error)}</p></div>`;
      return;
    }

    const fqdn = d.hostname ? d.hostname + (d.domain ? "." + d.domain : "") : "—";
    const rows = [
      ["Bilgisayar adı", fqdn],
      ["Oturum açan", d.loggedInUser || "—"],
      ["Marka / Model", [d.manufacturer, d.model].filter(Boolean).join(" ") || "—"],
      ["Seri no", d.serialNumber || "—"],
      ["İşlemci", d.cpu ? d.cpu + (d.logicalProcessors ? ` · ${d.logicalProcessors} çekirdek` : "") : "—"],
      ["Bellek", isNum(d.memoryBytes) ? bytes(d.memoryBytes) : "—"],
      ["İşletim sistemi", [d.os, d.osArchitecture].filter(Boolean).join(" · ") + (d.osBuild ? ` (build ${d.osBuild})` : "")],
      ["Kurulum", d.osInstallDate ? new Date(d.osInstallDate).toLocaleDateString("tr-TR") : "—"],
      ["Son açılış", d.lastBootUtc ? new Date(d.lastBootUtc).toLocaleString("tr-TR") : "—"],
      ["BIOS", [d.biosVersion, d.biosDate ? new Date(d.biosDate).toLocaleDateString("tr-TR") : null].filter(Boolean).join(" · ") || "—"]
    ];

    const infoRows = rows.map(([k, v]) => `
      <div class="info-row"><span class="info-key">${esc(k)}</span><span class="info-val">${esc(v)}</span></div>`).join("");

    const adapters = (d.adapters || []).map(a => `
      <div class="nic">
        <div class="nic-name">${esc(a.name)}</div>
        <div class="nic-detail">
          ${a.mac ? `<span class="nic-mac">${esc(a.mac)}</span>` : ""}
          ${(a.ipAddresses || []).map(ip => `<span class="nic-ip">${esc(ip)}</span>`).join("")}
          ${a.gateway ? `<span class="nic-gw">ağ geçidi ${esc(a.gateway)}</span>` : ""}
          <span class="nic-dhcp">${a.dhcp ? "DHCP" : "Statik"}</span>
        </div>
      </div>`).join("");

    const av = (d.antivirus || []).map(a => {
      const cls = a.enabled && a.upToDate ? "ok" : a.enabled ? "warn" : "bad";
      const state = `${a.enabled ? "Açık" : "Kapalı"} · ${a.upToDate ? "Güncel" : "Güncel değil"}`;
      return `<div class="av-row"><span class="av-dot ${cls}"></span><span class="av-name">${esc(a.name)}</span><span class="av-state">${state}</span></div>`;
    }).join("");

    const printers = (d.printers || []).map(p => `
      <div class="printer-row">
        <span class="printer-name">${esc(p.name)}${p.isDefault ? ' <span class="badge-default">varsayılan</span>' : ""}</span>
        <span class="printer-sub">${p.isNetwork ? "Ağ" : "Yerel"}${p.port ? " · " + esc(p.port) : ""}${p.offline ? " · çevrimdışı" : ""}</span>
      </div>`).join("");

    container.innerHTML = `
      <div class="drawer-section">
        <h3>Cihaz bilgileri</h3>
        <div class="info-grid">${infoRows}</div>
        ${adapters ? `<div class="nic-list"><div class="info-subhead">Ağ bağdaştırıcıları</div>${adapters}</div>` : ""}
        ${av ? `<div class="nic-list"><div class="info-subhead">Antivirüs</div>${av}</div>` : ""}
        ${printers ? `<div class="nic-list"><div class="info-subhead">Yazıcılar</div>${printers}</div>` : ""}
      </div>`;
  } catch (err) {
    if (seq !== infoSeq || state.openId !== id) return;
    container.innerHTML = `<div class="drawer-section"><h3>Cihaz bilgileri</h3><p class="error-note">${esc(err.message)}</p></div>`;
  }
}



let programsSeq = 0;
let programsFilter = "";

async function loadPrograms() {
  const id = state.openId;
  const container = el("drawerPrograms");
  if (!id || !container) return;

  programsFilter = "";
  container.innerHTML = `<div class="drawer-section"><h3>Kurulu programlar</h3><div class="apps-loading">Okunuyor…</div></div>`;
  const seq = ++programsSeq;

  try {
    const data = await api(`/api/servers/${encodeURIComponent(id)}/programs`);
    if (seq !== programsSeq || state.openId !== id) return;
    renderPrograms(data);
  } catch (err) {
    if (seq !== programsSeq || state.openId !== id) return;
    container.innerHTML = `<div class="drawer-section"><h3>Kurulu programlar</h3><p class="error-note">${esc(err.message)}</p></div>`;
  }
}

function renderPrograms(data) {
  const container = el("drawerPrograms");
  if (!container) return;

  if (data.error) {
    container.innerHTML = `<div class="drawer-section"><h3>Kurulu programlar</h3><p class="error-note">${esc(data.error)}</p></div>`;
    return;
  }

  const all = data.programs || [];
  const term = programsFilter.toLocaleLowerCase("tr");
  const list = term
    ? all.filter(p => p.name.toLocaleLowerCase("tr").includes(term) || (p.publisher || "").toLocaleLowerCase("tr").includes(term))
    : all;

  const rows = list.map(p => `
    <div class="prog-row">
      <div class="prog-main">
        <span class="prog-name">${esc(p.name)}</span>
        <span class="prog-sub">${esc(p.publisher || "—")}${p.is32Bit ? " · 32-bit" : ""}</span>
      </div>
      <span class="prog-ver">${esc(p.version || "")}</span>
      <span class="prog-date">${p.installDate ? new Date(p.installDate).toLocaleDateString("tr-TR") : ""}</span>
    </div>`).join("");

  container.innerHTML = `
    <div class="drawer-section">
      <h3>Kurulu programlar <span class="h3-meta">· ${all.length}</span></h3>
      <input type="search" id="progFilter" class="inline-search" placeholder="Program ara..." value="${esc(programsFilter)}">
      <div class="prog-list">${rows || '<p class="placeholder">Eşleşen program yok.</p>'}</div>
    </div>`;

  const input = el("progFilter");
  if (input) {
    input.addEventListener("input", () => { programsFilter = input.value; renderPrograms(data); });
    if (programsFilter) { input.focus(); input.setSelectionRange(input.value.length, input.value.length); }
  }
}



let usbSeq = 0;

async function loadUsb() {
  const id = state.openId;
  const container = el("drawerUsb");
  if (!id || !container) return;

  container.innerHTML = `<div class="drawer-section"><h3>USB bellek kullanımı</h3><div class="apps-loading">Okunuyor…</div></div>`;
  const seq = ++usbSeq;

  try {
    const data = await api(`/api/servers/${encodeURIComponent(id)}/usb`);
    if (seq !== usbSeq || state.openId !== id) return;

    if (data.error) {
      container.innerHTML = `<div class="drawer-section"><h3>USB bellek kullanımı</h3><p class="error-note">${esc(data.error)}</p></div>`;
      return;
    }

    const devices = data.devices || [];
    const connected = devices.filter(d => d.connected).length;

    const rows = devices.map(d => {
      const size = isNum(d.sizeBytes) ? bytes(d.sizeBytes) : "";

     
      const when = d.connected
        ? "TAKILI"
        : d.lastRemovedUtc ? "çıkarıldı: " + relativeTime(d.lastRemovedUtc)
        : d.lastConnectedUtc ? "son bağlanma: " + relativeTime(d.lastConnectedUtc)
        : "tarih yok";

      
      const detail = [
        d.serial ? "Seri: " + d.serial : "",
        d.lastConnectedUtc && !d.connected ? "bağlanma: " + new Date(d.lastConnectedUtc).toLocaleString("tr-TR") : ""
      ].filter(Boolean).join(" · ");

      return `
        <div class="usb-row${d.connected ? " connected" : ""}">
          <span class="usb-dot" title="${d.connected ? "Şu an takılı" : "Geçmişte takılmış"}"></span>
          <div class="usb-main">
            <span class="usb-name">${esc(d.name)}${size ? ` <span class="usb-size">${size}</span>` : ""}</span>
            <span class="usb-sub">${esc(detail)}</span>
          </div>
          <span class="usb-when">${esc(when)}</span>
        </div>`;
    }).join("");

    container.innerHTML = `
      <div class="drawer-section">
        <h3>USB bellek kullanımı <span class="h3-meta">· ${devices.length} aygıt${connected ? `, ${connected} takılı` : ""}</span></h3>
        <div class="usb-list">${rows || '<p class="placeholder">Bu cihazda USB depolama kaydı yok.</p>'}</div>
        <p class="hint">Şu an takılı ve geçmişte takılmış USB depolama aygıtları. Veri sızıntısı denetimi içindir.</p>
      </div>`;
  } catch (err) {
    if (seq !== usbSeq || state.openId !== id) return;
    container.innerHTML = `<div class="drawer-section"><h3>USB bellek kullanımı</h3><p class="error-note">${esc(err.message)}</p></div>`;
  }
}

async function loadApps() {
  const id = state.openId;
  const container = el("drawerApps");
  if (!id || !container) return;

  const definition = state.definitions.find(d => d.id === id);
  if (!definition?.windowsMetrics?.enabled) {
    container.innerHTML = "";
    return;
  }

  const server = state.servers.find(s => s.id === id);
  if (server && server.online === false) {
    container.innerHTML = appsSection("", '<p class="placeholder">Cihaza ulaşılamıyor; uygulama listesi alınamadı.</p>');
    return;
  }

  if (!container.dataset.loaded) {
    container.innerHTML = appsSection("", '<div class="apps-loading">Uygulamalar okunuyor…</div>');
  }

  const seq = ++appsRequestSeq;

  try {
    const data = await api(`/api/servers/${encodeURIComponent(id)}/processes?all=${appsShowAll}`);
    if (seq !== appsRequestSeq || state.openId !== id) return;

    container.dataset.loaded = "1";
    container.innerHTML = appsHtml(data);
  } catch (err) {
    if (seq !== appsRequestSeq || state.openId !== id) return;
    container.innerHTML = appsSection("", `<p class="error-note">${esc(err.message)}</p>`);
  }

  el("appsAll")?.addEventListener("change", event => {
    appsShowAll = event.target.checked;
    delete container.dataset.loaded;
    loadApps();
  });
}

function appsSection(meta, body) {
  return `
    <div class="drawer-section">
      <h3>Çalışan uygulamalar ${meta ? `<span class="h3-meta">${meta}</span>` : ""}</h3>
      ${body}
    </div>`;
}

function appsHtml(data) {
  if (data.error) {
    return appsSection("", `<p class="error-note">${esc(data.error)}</p>`);
  }

  const apps = data.apps || [];
  const user = data.userName
    ? `<span class="apps-user">${KIND_ICON.Computer}<span>${esc(data.userName)}</span></span>`
    : '<span class="apps-user muted">Oturum açmış kullanıcı yok</span>';

  const toolbar = `
    <div class="apps-toolbar">
      ${user}
      <label class="toggle"><input type="checkbox" id="appsAll" ${appsShowAll ? "checked" : ""}> Arka plan işlemleri</label>
    </div>`;

  const list = apps.length === 0
    ? '<p class="placeholder">Açık uygulama yok.</p>'
    : `<div class="app-list">${apps.map(appRowHtml).join("")}</div>`;

  const meta = `· ${apps.length} uygulama · ${data.totalProcesses} işlem`;
  const note = `<p class="hint">${relativeTime(data.collectedUtc)} okundu · ${APPS_REFRESH_MS / 1000} sn'de bir yenilenir. Pencere başlıkları ve açık dosyalar okunmaz.</p>`;

  return appsSection(meta, toolbar + list + note);
}

function appRowHtml(app) {
  const hue = [...app.name].reduce((h, ch) => (h * 31 + ch.charCodeAt(0)) % 360, 7);
  const initial = (app.displayName || app.name).trim().charAt(0).toLocaleUpperCase("tr");
  const details = [app.name];
  if (app.instances > 1) details.push(app.instances + " işlem");
  if (app.startedUtc) details.push("açılış " + relativeTime(app.startedUtc));

  return `
    <div class="app-row">
      <span class="app-avatar" style="background:hsl(${hue} 55% 42%)">${esc(initial)}</span>
      <div class="app-main">
        <div class="app-name">${esc(app.displayName || app.name)}</div>
        <div class="app-sub">${esc(details.join(" · "))}</div>
      </div>
      <div class="app-mem">${bytes(app.workingSetBytes)}</div>
    </div>`;
}

function barRow(name, percent, detail) {
  const p = clamp(percent, 0, 100);
  const cls = p >= 95 ? "crit" : p >= 85 ? "warn" : "";
  return `
    <div class="metric-row">
      <span class="metric-name">${esc(name)}</span>
      <span class="bar"><span class="${cls}" style="width:${p}%"></span></span>
      <span class="metric-val">${esc(detail)}</span>
    </div>`;
}


function portRow(data) {
  const d = data || {};
  const row = document.createElement("div");
  row.className = "row-item";
  row.innerHTML = `
    <input type="number" class="row-port" min="1" max="65535" placeholder="Port" value="${esc(d.port ?? "")}">
    <input type="text" class="row-label" placeholder="Etiket (SQL, RDP…)" value="${esc(d.label ?? "")}">
    <button type="button" class="ghost small row-del">Sil</button>`;
  row.querySelector(".row-del").addEventListener("click", () => row.remove());
  return row;
}

function httpRow(data) {
  const d = data || {};
  const code = (d.expectedStatusCodes && d.expectedStatusCodes[0]) || "";
  const row = document.createElement("div");
  row.className = "row-item";
  row.innerHTML = `
    <input type="text" class="row-url" placeholder="https://10.0.0.10/health" value="${esc(d.url ?? "")}">
    <input type="text" class="row-label" placeholder="Etiket" value="${esc(d.label ?? "")}">
    <input type="number" class="row-code" min="100" max="599" placeholder="Kod" value="${esc(code)}" title="Beklenen HTTP kodu (boş = 200-399)">
    <label><input type="checkbox" class="row-insecure" ${d.ignoreCertificateErrors ? "checked" : ""}> sertifika yoksay</label>
    <button type="button" class="ghost small row-del">Sil</button>`;
  row.querySelector(".row-del").addEventListener("click", () => row.remove());
  return row;
}


function openForm(definition) {
  state.editingId = definition?.id || null;
  dom.formError.hidden = true;
  dom.formTitle.textContent = definition ? "Sunucuyu düzenle" : "Sunucu ekle";
  dom.formDelete.hidden = !definition;

  el("fName").value = definition?.name || "";
  el("fHost").value = definition?.host || "";
  el("fGroup").value = definition?.group || "";

  el("fCategory").value = definition?.category || (state.category === "Computer" ? "Computer" : "Server");
  el("fEnabled").checked = definition ? definition.enabled !== false : true;

  const ping = definition?.ping;
  el("fPingEnabled").checked = definition ? !!(ping && ping.enabled) : true;
  el("fPingWarn").value = ping?.warnAboveMs ?? 250;

  dom.portRows.innerHTML = "";
  (definition?.ports || []).forEach(p => dom.portRows.appendChild(portRow(p)));

  dom.httpRows.innerHTML = "";
  (definition?.http || []).forEach(h => dom.httpRows.appendChild(httpRow(h)));

  const wmi = definition?.windowsMetrics;
  el("fWmiEnabled").checked = !!(wmi && wmi.enabled);
  el("fWmiUser").value = wmi?.username || "";
  el("fWmiPass").value = "";
  el("fCpuWarn").value = wmi?.cpuWarnPercent ?? 80;
  el("fMemWarn").value = wmi?.memoryWarnPercent ?? 85;
  el("fDiskWarn").value = wmi?.diskWarnPercent ?? 85;
  el("fDrives").value = (wmi?.drives || []).join(", ");

  dom.wmiPassHint.textContent = wmi?.hasPassword
    ? "Kayıtlı bir parola var. Boş bırakırsan değişmez."
    : "Domain hesabıyla çalışıyorsan boş bırakabilirsin.";

  syncWmiFields();
  dom.modal.hidden = false;
  el("fHost").focus();
}

function closeForm() {
  dom.modal.hidden = true;
  state.editingId = null;
}

function syncWmiFields() {
  dom.wmiFields.hidden = !el("fWmiEnabled").checked;
}

function collectForm() {
  const ports = [...dom.portRows.querySelectorAll(".row-item")].map(row => ({
    port: Number(row.querySelector(".row-port").value),
    label: row.querySelector(".row-label").value.trim() || null
  })).filter(p => p.port > 0);

  const http = [...dom.httpRows.querySelectorAll(".row-item")].map(row => {
    const code = Number(row.querySelector(".row-code").value);
    return {
      url: row.querySelector(".row-url").value.trim(),
      label: row.querySelector(".row-label").value.trim() || null,
      expectedStatusCodes: code >= 100 ? [code] : [],
      ignoreCertificateErrors: row.querySelector(".row-insecure").checked
    };
  }).filter(h => h.url.length > 0);

  const drives = el("fDrives").value.split(",").map(d => d.trim()).filter(Boolean);

  const payload = {
    name: el("fName").value.trim(),
    host: el("fHost").value.trim(),
    group: el("fGroup").value.trim(),
    category: el("fCategory").value,
    enabled: el("fEnabled").checked,
    ping: {
      enabled: el("fPingEnabled").checked,
      warnAboveMs: Number(el("fPingWarn").value) || 0
    },
    ports,
    http,

    windowsMetrics: {
      enabled: el("fWmiEnabled").checked,
      username: el("fWmiUser").value.trim() || null,
      cpuWarnPercent: Number(el("fCpuWarn").value) || 0,
      memoryWarnPercent: Number(el("fMemWarn").value) || 0,
      diskWarnPercent: Number(el("fDiskWarn").value) || 0,
      drives
    }
  };


  const password = el("fWmiPass").value;
  if (password) payload.windowsMetrics.password = password;

  return payload;
}

async function submitForm(event) {
  event.preventDefault();
  dom.formError.hidden = true;
  dom.formSave.disabled = true;
  dom.formSave.classList.add("busy");

  try {
    const payload = collectForm();

    if (state.editingId) {
      await api("/api/servers/" + encodeURIComponent(state.editingId), {
        method: "PUT",
        body: JSON.stringify(payload)
      });
      toast("Sunucu güncellendi.");
    } else {
      await api("/api/servers", { method: "POST", body: JSON.stringify(payload) });
      toast("Sunucu eklendi.");
    }

    closeForm();
    await Promise.all([loadDefinitions(), loadStatus()]);
  } catch (err) {
    dom.formError.textContent = err.message;
    dom.formError.hidden = false;
  } finally {
    dom.formSave.disabled = false;
    dom.formSave.classList.remove("busy");
  }
}

async function deleteServer() {
  if (!state.editingId) return;

  const definition = state.definitions.find(d => d.id === state.editingId);
  const label = definition ? `${definition.name} (${definition.host})` : state.editingId;
  if (!confirm(`"${label}" izlemeden kaldırılsın mı? Geçmiş verileri de silinir.`)) return;

  const deletedId = state.editingId;

  try {
    await api("/api/servers/" + encodeURIComponent(deletedId), { method: "DELETE" });
    toast("Sunucu silindi.");
    closeForm();
    closeDrawer();

  
    state.servers = state.servers.filter(s => s.id !== deletedId);
    state.definitions = state.definitions.filter(d => d.id !== deletedId);
    layoutSignature = "";       
    passiveSignature = "__reload__";
    render();

    await Promise.all([loadDefinitions(), loadStatus()]);
  } catch (err) {
    dom.formError.textContent = err.message;
    dom.formError.hidden = false;
  }
}

async function saveInterval() {
  const seconds = Number(dom.interval.value);
  if (!seconds || seconds < 5) return;

  try {
 
    const current = await api("/api/settings");
    const settings = await api("/api/settings", {
      method: "PUT",
      body: JSON.stringify({ ...current, intervalSeconds: seconds })
    });
    state.intervalSeconds = settings.intervalSeconds;
    dom.interval.value = settings.intervalSeconds;
    toast("Kontrol aralığı " + settings.intervalSeconds + " sn yapıldı.");
  } catch (err) {
    toast("Aralık kaydedilemedi: " + err.message, true);
  }
}


function openSettings(tab) {
  dom.settingsModal.hidden = false;
  selectTab(tab || "general");
  loadGeneralSettings();
  loadNotificationSettings();
  loadSecuritySettings();
}

function closeSettings() {
  dom.settingsModal.hidden = true;
}

function selectTab(name) {
  dom.settingsModal.querySelectorAll(".tab").forEach(t => t.classList.toggle("active", t.dataset.tab === name));
  dom.settingsModal.querySelectorAll(".tab-panel").forEach(p => { p.hidden = p.dataset.panel !== name; });
  if (name === "report") loadUptimeReport();
  if (name === "dns") loadDnsConfig();
}

async function loadDnsConfig() {
  const keyNode = el("dnsKey");
  const statusNode = el("dnsStatus");

  try {
    const cfg = await api("/api/dns/config");
    const origin = location.origin;

    if (keyNode) keyNode.textContent = cfg.ingestKey;

    const taskNode = el("dnsCmdTask");
    if (taskNode) {
   
      taskNode.textContent =
        `schtasks /create /tn "DNS Toplayici" /sc minute /mo 15 /ru SYSTEM /rl HIGHEST /f ` +
        `/tr "powershell -NoProfile -ExecutionPolicy Bypass -File C:\\dns-toplayici.ps1 ` +
        `-PanelUrl '${origin}' -IngestKey '${cfg.ingestKey}'"`;
    }

    if (statusNode) {
      statusNode.className = "dns-status " + (cfg.hasData ? "ok" : "wait");
      statusNode.textContent = cfg.hasData
        ? "✓ Panel DC'den DNS verisi alıyor."
        : "Henüz veri gelmedi. Toplayıcı kurulduktan sonra ilk özet ~15 dk içinde düşer.";
    }
  } catch (err) {
    if (statusNode) { statusNode.className = "dns-status err"; statusNode.textContent = err.message; }
  }

  loadDnsHiddenList();
}

function showError(id, message) {
  const node = el(id);
  node.textContent = message || "";
  node.hidden = !message;
}

async function withBusy(button, work) {
  button.disabled = true;
  button.classList.add("busy");
  try {
    await work();
  } finally {
    button.disabled = false;
    button.classList.remove("busy");
  }
}



async function loadGeneralSettings() {
  try {
    const s = await api("/api/settings");
    el("sInterval").value = s.intervalSeconds;
    el("sTimeout").value = s.defaultTimeoutMs;
    el("sHistory").value = s.historySize;
    el("sRetention").value = s.historyRetentionDays;
  } catch (err) {
    showError("generalError", err.message);
  }
}

async function saveGeneralSettings(event) {
  event.preventDefault();
  showError("generalError", "");

  await withBusy(event.submitter || el("generalForm").querySelector("[type=submit]"), async () => {
    try {
      const saved = await api("/api/settings", {
        method: "PUT",
        body: JSON.stringify({
          intervalSeconds: Number(el("sInterval").value),
          defaultTimeoutMs: Number(el("sTimeout").value),
          historySize: Number(el("sHistory").value),
          historyRetentionDays: Number(el("sRetention").value)
        })
      });
      dom.interval.value = saved.intervalSeconds;
      state.intervalSeconds = saved.intervalSeconds;
      toast("Ayarlar kaydedildi.");
    } catch (err) {
      showError("generalError", err.message);
    }
  });
}



async function loadNotificationSettings() {
  try {
    const n = await api("/api/notifications");
    el("nEnabled").checked = n.enabled;
    el("nRecovery").checked = n.notifyOnRecovery;
    el("nComputers").checked = !!n.notifyComputers;
    el("nThreshold").value = n.failureThreshold;

    el("eEnabled").checked = n.email.enabled;
    el("eHost").value = n.email.host || "";
    el("ePort").value = n.email.port || 25;
    el("eSsl").checked = n.email.useSsl;
    el("eUser").value = n.email.username || "";
    el("ePass").value = "";
    el("ePass").placeholder = n.email.hasPassword ? "kayıtlı · değiştirmemek için boş bırakın" : "";
    el("eFrom").value = n.email.from || "";
    el("eTo").value = (n.email.to || []).join(", ");

    el("wEnabled").checked = n.webhook.enabled;
    el("wUrl").value = n.webhook.url || "";

    const c = n.changeAlerts || {};
    el("cEnabled").checked = !!c.enabled;
    el("cInterval").value = c.intervalMinutes || 60;
    el("cAv").checked = c.watchAntivirus !== false;
    el("cSoftware").checked = c.watchNewSoftware !== false;
    el("cUsb").checked = c.watchUsb !== false;
  } catch (err) {
    showError("notifyError", err.message);
  }
}

function collectNotificationSettings() {
  const payload = {
    enabled: el("nEnabled").checked,
    notifyOnRecovery: el("nRecovery").checked,
    notifyComputers: el("nComputers").checked,
    failureThreshold: Number(el("nThreshold").value) || 2,
    email: {
      enabled: el("eEnabled").checked,
      host: el("eHost").value.trim(),
      port: Number(el("ePort").value) || 25,
      useSsl: el("eSsl").checked,
      username: el("eUser").value.trim() || null,
      from: el("eFrom").value.trim(),
      to: el("eTo").value.split(/[,;]/).map(t => t.trim()).filter(Boolean)
    },
    webhook: {
      enabled: el("wEnabled").checked,
      url: el("wUrl").value.trim()
    },
    changeAlerts: {
      enabled: el("cEnabled").checked,
      intervalMinutes: Number(el("cInterval").value) || 60,
      watchAntivirus: el("cAv").checked,
      watchNewSoftware: el("cSoftware").checked,
      watchUsb: el("cUsb").checked
    }
  };


  const password = el("ePass").value;
  if (password) payload.email.password = password;

  return payload;
}

async function saveNotificationSettings(event) {
  event.preventDefault();
  showError("notifyError", "");

  await withBusy(event.submitter || el("notifyForm").querySelector("[type=submit]"), async () => {
    try {
      await api("/api/notifications", { method: "PUT", body: JSON.stringify(collectNotificationSettings()) });
      await loadNotificationSettings();
      toast("Bildirim ayarları kaydedildi.");
    } catch (err) {
      showError("notifyError", err.message);
    }
  });
}

async function sendTestNotification() {
  showError("notifyError", "");

  await withBusy(el("notifyTest"), async () => {
    try {

      await api("/api/notifications", { method: "PUT", body: JSON.stringify(collectNotificationSettings()) });
      const result = await api("/api/notifications/test", { method: "POST" });
      toast(result.message);
    } catch (err) {
      showError("notifyError", err.message);
    }
  });
}


async function loadSecuritySettings() {
  try {
    const s = await api("/api/auth/state");
    el("aEnabled").checked = s.enabled;
    el("aUser").value = s.username || "admin";
    el("aPass").value = "";
  } catch (err) {
    showError("securityError", err.message);
  }
}

async function saveSecuritySettings(event) {
  event.preventDefault();
  showError("securityError", "");

  const enabled = el("aEnabled").checked;
  const password = el("aPass").value;

  if (enabled && password && password.length < 8) {
    showError("securityError", "Parola en az 8 karakter olmalı.");
    return;
  }

  await withBusy(event.submitter || el("securityForm").querySelector("[type=submit]"), async () => {
    try {
      await api("/api/auth/settings", {
        method: "PUT",
        body: JSON.stringify({ enabled, username: el("aUser").value.trim(), password: password || null })
      });

      if (enabled) {

        if (password) {
          await api("/api/auth/login", {
            method: "POST",
            body: JSON.stringify({ username: el("aUser").value.trim(), password })
          });
        }
        toast("Giriş koruması etkin.");
      } else {
        toast("Giriş koruması kapatıldı.");
      }

      await loadAuthState();
      el("aPass").value = "";
    } catch (err) {
      showError("securityError", err.message);
    }
  });
}

async function loadAuthState() {
  try {
    const s = await api("/api/auth/state");
    if (dom.logoutBtn) {
      dom.logoutBtn.hidden = !s.enabled;
      dom.logoutBtn.title = s.enabled ? s.username + " olarak giriş yapıldı" : "";
    }
  } catch {

  }
}

async function logout() {
  try {
    await api("/api/auth/logout", { method: "POST" });
  } finally {
    location.replace("/login.html");
  }
}



async function loadUptimeReport() {
  const body = el("reportBody");
  const days = Number(el("reportDays").value) || 7;
  body.innerHTML = '<p class="placeholder">Yükleniyor…</p>';

  try {
    const report = await api("/api/uptime?days=" + days);
    const rows = report.servers;

    if (rows.length === 0) {
      body.innerHTML = '<p class="placeholder">Henüz sunucu yok.</p>';
      return;
    }

    body.innerHTML = `
      <div class="report-list">
        ${rows.map(r => {
          const pct = isNum(r.uptimePercent) ? r.uptimePercent : null;
          const color = pct === null ? "var(--unknown)" : pct >= 99.5 ? "var(--up)" : pct >= 95 ? "var(--warn)" : "var(--down)";
          return `
            <div class="report-row">
              <div class="report-ident">
                <div class="card-name">${esc(r.name)}</div>
                <div class="card-host">${esc(r.host)}</div>
              </div>
              <div class="report-bar"><span style="width:${pct ?? 0}%;background:${color}"></span></div>
              <div class="report-pct" style="color:${color}">${pct === null ? "veri yok" : "%" + pct.toFixed(2)}</div>
              <div class="report-count">${r.measurements} ölçüm</div>
            </div>`;
        }).join("")}
      </div>
      <p class="hint">Uptime, dönem içindeki ölçümlerin ÇALIŞIYOR veya UYARI olan oranıdır. Uygulamanın kapalı olduğu süreler hesaba katılmaz.</p>`;
  } catch (err) {
    body.innerHTML = `<p class="error-note">${esc(err.message)}</p>`;
  }
}



const THEME_KEY = "serverMonitorTheme";
const THEME_ORDER = ["auto", "light", "dark"];
const THEME_ICON = { auto: "🌗", light: "☀️", dark: "🌙" };
const THEME_TITLE = { auto: "Tema: sistem", light: "Tema: açık", dark: "Tema: koyu" };

function applyTheme(theme) {
  if (theme === "auto") document.documentElement.removeAttribute("data-theme");
  else document.documentElement.setAttribute("data-theme", theme);

  if (!dom.themeBtn) return;
  dom.themeBtn.textContent = THEME_ICON[theme];
  dom.themeBtn.title = THEME_TITLE[theme] + " (değiştirmek için tıkla)";
}

function readTheme() {
  try {
    const saved = localStorage.getItem(THEME_KEY);
    return THEME_ORDER.includes(saved) ? saved : "auto";
  } catch {
    return "auto";
  }
}

function cycleTheme() {
  const next = THEME_ORDER[(THEME_ORDER.indexOf(readTheme()) + 1) % THEME_ORDER.length];
  try { localStorage.setItem(THEME_KEY, next); } catch {  }
  applyTheme(next);
}



const isNum = v => typeof v === "number" && isFinite(v);
const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

const setText = (node, value) => { if (node && node.textContent !== value) node.textContent = value; };
const setHtml = (node, value) => { if (node && node.innerHTML !== value) node.innerHTML = value; };

const prefersReducedMotion = () =>
  window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;

function fmt(value, decimals) {
  return value.toFixed(decimals ?? 0);
}

function niceMax(maxValue) {
  const target = Math.max(maxValue * 1.25, 10);
  const magnitude = Math.pow(10, Math.floor(Math.log10(target)));
  return Math.ceil(target / magnitude) * magnitude;
}

function timeText(date) {
  return date.toLocaleTimeString("tr-TR", { hour: "2-digit", minute: "2-digit" });
}

function lastCheckText(s) {
  if (!s.lastCheckUtc) return "–";
  const diff = Math.round((Date.now() - new Date(s.lastCheckUtc).getTime()) / 1000);
  if (diff < 60) return Math.max(0, diff) + " sn önce";
  if (diff < 3600) return Math.round(diff / 60) + " dk önce";
  return new Date(s.lastCheckUtc).toLocaleTimeString("tr-TR");
}

function bytes(value) {
  if (!isNum(value)) return "–";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let v = value, i = 0;
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++; }
  return v.toFixed(v >= 100 || i === 0 ? 0 : 1) + " " + units[i];
}


function uptimeText(ts) {
  const match = /^(?:(\d+)\.)?(\d{2}):(\d{2}):(\d{2})/.exec(ts);
  if (!match) return ts;
  const [, d, h, m] = match;
  const parts = [];
  if (d) parts.push(d + " gün");
  if (+h) parts.push(+h + " saat");
  if (+m) parts.push(+m + " dk");
  return parts.length ? parts.join(" ") : "1 dk'dan az";
}

function esc(value) {
  return String(value ?? "").replace(/[&<>"']/g, ch =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[ch]));
}



function startPolling() {
  stopPolling();
  pollTimer = setInterval(loadStatus, POLL_MS);
  tickTimer = setInterval(tickRelativeTimes, TICK_MS);
}

function stopPolling() {
  if (pollTimer) clearInterval(pollTimer);
  if (tickTimer) clearInterval(tickTimer);
  pollTimer = null;
  tickTimer = null;
}

dom.themeBtn?.addEventListener("click", cycleTheme);
dom.settingsBtn?.addEventListener("click", () => openSettings("general"));
dom.logoutBtn?.addEventListener("click", logout);
dom.settingsClose?.addEventListener("click", closeSettings);

dom.settingsModal?.querySelectorAll(".tab").forEach(tab =>
  tab.addEventListener("click", () => selectTab(tab.dataset.tab)));

el("generalForm")?.addEventListener("submit", saveGeneralSettings);
el("notifyForm")?.addEventListener("submit", saveNotificationSettings);
el("notifyTest")?.addEventListener("click", sendTestNotification);
el("securityForm")?.addEventListener("submit", saveSecuritySettings);
el("reportDays")?.addEventListener("change", loadUptimeReport);

el("dnsHideAdd")?.addEventListener("click", async () => {
  const input = el("dnsHideInput");
  const domain = (input.value || "").trim();
  if (!domain) return;
  await setDomainHidden(domain, true);
  input.value = "";
});
el("dnsHideInput")?.addEventListener("keydown", e => {
  if (e.key === "Enter") { e.preventDefault(); el("dnsHideAdd")?.click(); }
});

dom.settingsModal?.addEventListener("click", event => {
  const button = event.target.closest("[data-copy]");
  if (!button) return;

  const text = el(button.dataset.copy)?.textContent || "";
  navigator.clipboard?.writeText(text).then(
    () => { const old = button.textContent; button.textContent = "Kopyalandı ✓"; setTimeout(() => { button.textContent = old; }, 1500); },
    () => toast("Kopyalanamadı, elle seçip kopyalayın.", true)
  );
});
dom.refreshBtn.addEventListener("click", refreshAll);
dom.filter.addEventListener("input", () => { renderCards(); renderDisabled(); });

el("groupFilter")?.addEventListener("change", e => {
  state.groupFilter = e.target.value;
  renderSummary();
  renderCards();
  renderDisabled();
});

el("pdfBtn")?.addEventListener("click", () => window.print());

window.addEventListener("beforeprint", () => {
  const header = el("printHeader");
  if (!header) return;
  const scope = [];
  if (state.category === "Server") scope.push("Sunucular");
  else if (state.category === "Computer") scope.push("Bilgisayarlar");
  if (state.groupFilter) scope.push(state.groupFilter);
  header.innerHTML =
    `<h1>Cihaz Durum Raporu</h1>` +
    `<div class="print-meta">${new Date().toLocaleString("tr-TR")}` +
    `${scope.length ? " · " + esc(scope.join(" · ")) : ""}</div>`;
});

el("wordBtn")?.addEventListener("click", () => {
  const params = new URLSearchParams();
  if (state.category && state.category !== "all") params.set("category", state.category);
  if (state.groupFilter) params.set("group", state.groupFilter);

  window.location.href = "/api/report/word" + (params.toString() ? "?" + params : "");
});
dom.interval.addEventListener("change", saveInterval);
dom.addBtn.addEventListener("click", () => openForm(null));

dom.autoRefresh.addEventListener("change", () => {
  dom.autoRefresh.checked ? startPolling() : stopPolling();
});

el("categoryTabs")?.querySelectorAll(".cat-tab").forEach(tab =>
  tab.addEventListener("click", () => selectCategory(tab.dataset.category)));

dom.drawerClose.addEventListener("click", closeDrawer);
dom.backdrop.addEventListener("click", closeDrawer);
dom.drawerRefresh.addEventListener("click", () => state.openId && refreshOne(state.openId));
dom.drawerEdit.addEventListener("click", () => {
  const definition = state.definitions.find(d => d.id === state.openId);
  if (definition) openForm(definition);
  else toast("Tanım bulunamadı, sayfayı yenileyin.", true);
});

dom.form.addEventListener("submit", submitForm);
dom.formClose.addEventListener("click", closeForm);
dom.formCancel.addEventListener("click", closeForm);
dom.formDelete.addEventListener("click", deleteServer);
dom.addPort.addEventListener("click", () => dom.portRows.appendChild(portRow()));
dom.addHttp.addEventListener("click", () => dom.httpRows.appendChild(httpRow()));
el("fWmiEnabled").addEventListener("change", syncWmiFields);

document.addEventListener("keydown", e => {
  if (e.key !== "Escape") return;
  if (dom.settingsModal && !dom.settingsModal.hidden) closeSettings();
  else if (!dom.modal.hidden) closeForm();
  else if (!dom.drawer.hidden) closeDrawer();
});

document.addEventListener("visibilitychange", () => {
  if (document.hidden) stopPolling();
  else if (dom.autoRefresh.checked) { loadStatus(); startPolling(); }
});


const isStandalone = () =>
  window.matchMedia?.("(display-mode: standalone)").matches || window.navigator.standalone === true;


const isIos = () =>
  /iphone|ipad|ipod/i.test(navigator.userAgent) ||
  (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);

function initPwa() {
  if (isStandalone()) document.documentElement.classList.add("standalone");


  if ("serviceWorker" in navigator && window.isSecureContext) {
    navigator.serviceWorker.register("/sw.js").catch(err => console.warn("Service worker kaydedilemedi:", err));
  }

  const hint = el("installHint");
  if (!hint) return;

  let dismissed = false;
  try { dismissed = localStorage.getItem("installHintDismissed") === "1"; } catch {  }

  if (isIos() && !isStandalone() && !dismissed) hint.hidden = false;

  el("installHintClose")?.addEventListener("click", () => {
    hint.hidden = true;
    try { localStorage.setItem("installHintDismissed", "1"); } catch {  }
  });
}

applyTheme(readTheme());
initPwa();
loadAuthState();
loadDefinitions();
loadStatus();
startPolling();
