// BubblesBot dashboard — vanilla JS, no framework.
// Talks to the in-bot HTTP server: /api/settings, /api/settings/schema, /ws

const $ = id => document.getElementById(id);

const VK_NAMES = {
  0x08: "Backspace", 0x09: "Tab", 0x0D: "Enter", 0x10: "Shift", 0x11: "Ctrl", 0x12: "Alt",
  0x14: "CapsLock", 0x1B: "Esc", 0x20: "Space", 0x21: "PgUp", 0x22: "PgDn", 0x23: "End",
  0x24: "Home", 0x25: "Left", 0x26: "Up", 0x27: "Right", 0x28: "Down", 0x2D: "Insert",
  0x2E: "Delete",
  0xC0: "`",  0xBB: "=",  0xBD: "-",  0xDB: "[",  0xDD: "]",  0xDC: "\\",
  0xBA: ";",  0xDE: "'",  0xBC: ",",  0xBE: ".",  0xBF: "/",
};
for (let i = 0; i < 26; i++) VK_NAMES[0x41 + i] = String.fromCharCode(65 + i);
for (let i = 0; i < 10; i++) VK_NAMES[0x30 + i] = String.fromCharCode(48 + i);
for (let i = 1; i <= 12; i++) VK_NAMES[0x6F + i] = "F" + i; // VK_F1=0x70

const vkLabel = vk => VK_NAMES[vk] || `VK 0x${vk.toString(16).toUpperCase()}`;

let schema = null;
let settings = null;

async function loadSchema()    { schema   = await fetch("/api/settings/schema").then(r => r.json()); }
async function loadSettings()  { settings = await fetch("/api/settings").then(r => r.json()); }
async function pushSettings()  {
  const body = JSON.stringify(settings);
  await fetch("/api/settings", { method: "PUT", headers: { "Content-Type": "application/json" }, body });
}

// ── Path traversal helpers (for nested settings) ──────────────────────────
// Schema fields carry a `path` array — ["loot", "minChaosValue"] — so we can read/write
// nested objects without a flat name registry. Top-level fields still emit a single-segment
// path, so the same helpers work uniformly.

function pathGet(obj, path) {
  let cur = obj;
  for (const seg of path) {
    if (cur == null) return undefined;
    cur = cur[seg];
  }
  return cur;
}

function pathSet(obj, path, value) {
  let cur = obj;
  for (let i = 0; i < path.length - 1; i++) {
    const seg = path[i];
    if (cur[seg] == null || typeof cur[seg] !== "object") cur[seg] = {};
    cur = cur[seg];
  }
  cur[path[path.length - 1]] = value;
}

// Resolve a schema field's path, falling back to `[name]` for legacy single-segment fields.
const fieldPath = f => Array.isArray(f.path) && f.path.length > 0 ? f.path : [f.name];

// ── Settings UI ────────────────────────────────────────────────────────────

function renderSettings() {
  const root = $("settings");
  root.innerHTML = "";

  // Group fields by category, render section headers between them.
  const byCat = {};
  for (const f of schema.fields) (byCat[f.category] ||= []).push(f);

  for (const [cat, fields] of Object.entries(byCat)) {
    const head = document.createElement("div"); head.className = "section-head"; head.textContent = cat;
    root.appendChild(head);
    for (const f of fields) root.appendChild(renderField(f));
  }
}

function renderField(f) {
  const wrap = document.createElement("div"); wrap.className = "field";
  const lbl  = document.createElement("label"); lbl.textContent = f.displayName; wrap.appendChild(lbl);
  if (f.description) {
    const d = document.createElement("div"); d.className = "desc"; d.textContent = f.description; wrap.appendChild(d);
  }
  const ctl = document.createElement("div"); ctl.className = "ctl"; wrap.appendChild(ctl);

  const path = fieldPath(f);
  const v = pathGet(settings, path);

  if (f.type === "bool") {
    const swc = document.createElement("label"); swc.className = "switch";
    const inp = document.createElement("input"); inp.type = "checkbox"; inp.checked = !!v;
    inp.onchange = () => { pathSet(settings, path, inp.checked); pushSettings(); };
    const sl  = document.createElement("span"); sl.className = "slider";
    swc.append(inp, sl); ctl.appendChild(swc);
  }
  else if (f.type === "skills") {
    ctl.appendChild(renderSkillsEditor(f));
  }
  else if (f.type === "flasks") {
    ctl.appendChild(renderFlasksEditor(f));
  }
  else if (f.type === "options") {
    const sel = document.createElement("select");
    for (const o of (f.options || [])) {
      const opt = document.createElement("option");
      opt.value = o.value; opt.textContent = o.label;
      if (Number(o.value) === Number(v)) opt.selected = true;
      sel.appendChild(opt);
    }
    sel.onchange = () => { pathSet(settings, path, parseInt(sel.value)); pushSettings(); };
    ctl.appendChild(sel);
  }
  else if (f.type === "keycode") {
    const btn = document.createElement("button"); btn.type = "button"; btn.className = "key-btn";
    btn.textContent = vkLabel(v);
    btn.onclick = () => captureKey(btn, vk => { pathSet(settings, path, vk); pushSettings(); btn.textContent = vkLabel(vk); });
    ctl.appendChild(btn);
  }
  else if (f.type === "stringlist") {
    ctl.appendChild(renderStringListEditor(f, path));
  }
  else if (f.type === "modtable") {
    ctl.appendChild(renderModTableEditor(f, path));
  }
  else if (f.type === "float" || f.type === "int") {
    if (f.min != null && f.max != null) {
      const rng = document.createElement("input"); rng.type = "range";
      rng.min = f.min; rng.max = f.max; rng.step = f.step || 1; rng.value = v ?? f.min;
      const disp = document.createElement("span"); disp.className = "range-display"; disp.textContent = rng.value;
      rng.oninput = () => { disp.textContent = rng.value; };
      rng.onchange = () => { pathSet(settings, path, f.type === "int" ? parseInt(rng.value) : parseFloat(rng.value)); pushSettings(); };
      ctl.append(rng, disp);
    } else {
      const num = document.createElement("input"); num.type = "number"; num.value = v ?? 0;
      num.onchange = () => { pathSet(settings, path, f.type === "int" ? parseInt(num.value) : parseFloat(num.value)); pushSettings(); };
      ctl.appendChild(num);
    }
  }
  return wrap;
}

// ── String-list editor ────────────────────────────────────────────────────
// Renders a List<string> setting as an add/remove row stack. Persists immediately on each
// edit. Used for must-loot allowlists, implicit-mod whitelists, etc.
function renderStringListEditor(f, path) {
  const wrap = document.createElement("div"); wrap.className = "stringlist";

  // Ensure the underlying array exists.
  let arr = pathGet(settings, path);
  if (!Array.isArray(arr)) {
    arr = [];
    pathSet(settings, path, arr);
  }

  const list = document.createElement("div"); list.className = "stringlist-rows";
  wrap.appendChild(list);

  function rerender() {
    list.innerHTML = "";
    arr.forEach((value, idx) => {
      const row = document.createElement("div"); row.className = "stringlist-row";
      const inp = document.createElement("input"); inp.type = "text"; inp.value = value; inp.className = "stringlist-input";
      inp.onchange = () => { arr[idx] = inp.value; pushSettings(); };
      const rm = document.createElement("button"); rm.type = "button"; rm.className = "stringlist-rm"; rm.textContent = "×";
      rm.title = "Remove";
      rm.onclick = () => { arr.splice(idx, 1); pushSettings(); rerender(); };
      row.append(inp, rm);
      list.appendChild(row);
    });
  }
  rerender();

  const addRow = document.createElement("div"); addRow.className = "stringlist-add";
  const addInp = document.createElement("input"); addInp.type = "text"; addInp.placeholder = f.placeholder || "Add entry…";
  const addBtn = document.createElement("button"); addBtn.type = "button"; addBtn.className = "stringlist-addbtn"; addBtn.textContent = "+ add";
  const commit = () => {
    const t = addInp.value.trim();
    if (!t) return;
    arr.push(t);
    addInp.value = "";
    pushSettings();
    rerender();
  };
  addBtn.onclick = commit;
  addInp.onkeydown = e => { if (e.key === "Enter") { e.preventDefault(); commit(); } };
  addRow.append(addInp, addBtn);
  wrap.appendChild(addRow);
  return wrap;
}

// ── Mod-danger table editor ───────────────────────────────────────────────
// Renders the Ultimatum modifier catalog (one row per known mod) with a tier dropdown.
// Storage is the underlying List<string> with "ModId=N" entries; only mods whose tier
// differs from the default get stored. Schema injects the catalog (`f.mods`) + tier
// definitions (`f.tiers`) inline.
function renderModTableEditor(f, path) {
  const wrap = document.createElement("div"); wrap.className = "modtable";
  const mods  = f.mods  || [];
  const tiers = f.tiers || [];

  // Underlying storage — the List<string> overrides.
  let arr = pathGet(settings, path);
  if (!Array.isArray(arr)) { arr = []; pathSet(settings, path, arr); }

  // Parse current overrides into a Map<id, value>.
  const overrides = new Map();
  for (const row of arr) {
    const eq = row.indexOf("=");
    if (eq <= 0) continue;
    const id = row.slice(0, eq).trim();
    const v  = parseInt(row.slice(eq + 1));
    if (!isNaN(v)) overrides.set(id, v);
  }

  // Effective tier for a mod (override → fallback to baked default).
  const effective = m => overrides.has(m.id) ? overrides.get(m.id) : m.defaultDanger;

  // Persist back: rebuild arr from overrides map (only entries differing from default).
  function persist() {
    const next = [];
    for (const m of mods) {
      if (!overrides.has(m.id)) continue;
      if (overrides.get(m.id) === m.defaultDanger) continue;   // matches default → don't store
      next.push(`${m.id}=${overrides.get(m.id)}`);
    }
    arr.length = 0;
    for (const e of next) arr.push(e);
    pushSettings();
  }

  // Header.
  const head = document.createElement("div"); head.className = "modtable-head";
  const hName = document.createElement("span"); hName.textContent = "Modifier";       head.appendChild(hName);
  const hId   = document.createElement("span"); hId.textContent   = "id";             head.appendChild(hId);
  const hTier = document.createElement("span"); hTier.textContent = "Danger tier";    head.appendChild(hTier);
  const hReset= document.createElement("span"); hReset.textContent= "";               head.appendChild(hReset);
  wrap.appendChild(head);

  for (const m of mods) {
    const row = document.createElement("div"); row.className = "modtable-row";

    const name = document.createElement("span"); name.className = "modtable-name"; name.textContent = m.name;
    row.appendChild(name);

    const id = document.createElement("span"); id.className = "modtable-id"; id.textContent = m.id;
    id.title = m.id;
    row.appendChild(id);

    const sel = document.createElement("select"); sel.className = "modtable-tier";
    for (const t of tiers) {
      const opt = document.createElement("option");
      opt.value = t.value; opt.textContent = t.label;
      if (t.value === effective(m)) opt.selected = true;
      sel.appendChild(opt);
    }
    // Mark as "overridden" visually if we differ from the default.
    if (overrides.has(m.id) && overrides.get(m.id) !== m.defaultDanger) row.classList.add("overridden");
    sel.onchange = () => {
      const v = parseInt(sel.value);
      if (v === m.defaultDanger) overrides.delete(m.id);
      else overrides.set(m.id, v);
      persist();
      row.classList.toggle("overridden", overrides.has(m.id) && overrides.get(m.id) !== m.defaultDanger);
    };
    row.appendChild(sel);

    const rst = document.createElement("button"); rst.type = "button"; rst.className = "modtable-reset"; rst.textContent = "↺";
    rst.title = `Reset to default (${tiers.find(t => t.value === m.defaultDanger)?.label || m.defaultDanger})`;
    rst.onclick = () => {
      overrides.delete(m.id);
      const opts = sel.options;
      for (let i = 0; i < opts.length; i++) opts[i].selected = (Number(opts[i].value) === m.defaultDanger);
      persist();
      row.classList.remove("overridden");
    };
    row.appendChild(rst);
    wrap.appendChild(row);
  }
  return wrap;
}

// ── Skills editor ──────────────────────────────────────────────────────────

const ROLE_NAMES = ["Disabled", "Walk", "Dash", "Attack", "SelfBuff", "Channel", "Aura"];

// PoE's default skill-bar slot → key binding. Slots 0-7 are the always-visible hotbar
// (Left/Mid/Right click + QWERT). Slots 8-12 are extras — typically duplicates of earlier
// slots auto-populated by the game (e.g. vaal versions, modifier-bound clones). The user
// can rebind in PoE; these defaults are good first-fill values that the user can override.
const SLOT_DEFAULT_KEY = [
  { vk: 0x01, label: "LMB" },   // 0 = Left Click
  { vk: 0x04, label: "MMB" },   // 1 = Middle Click
  { vk: 0x02, label: "RMB" },   // 2 = Right Click
  { vk: 0x51, label: "Q"   },   // 3
  { vk: 0x57, label: "W"   },   // 4
  { vk: 0x45, label: "E"   },   // 5
  { vk: 0x52, label: "R"   },   // 6
  { vk: 0x54, label: "T"   },   // 7
  { vk: 0,    label: "—"   },   // 8+: modifier/extra slots — no default
  { vk: 0,    label: "—"   },
  { vk: 0,    label: "—"   },
  { vk: 0,    label: "—"   },
  { vk: 0,    label: "—"   },
];

function renderSkillsEditor(f) {
  const wrap = document.createElement("div"); wrap.className = "skills-editor";
  const profile = settings[f.name] || (settings[f.name] = { slots: [] });
  if (!profile.slots) profile.slots = [];

  const list = document.createElement("div"); list.className = "skills-list";
  wrap.appendChild(list);

  // Detected-skills panel — collapsed by default to save vertical space. Toggled by the
  // "+ add skill" button. Stays open after one import so the user can grab a few in a row;
  // a separate close button collapses it.
  const detectedWrap = document.createElement("div"); detectedWrap.className = "skills-detected-wrap hidden";
  const detected = document.createElement("div"); detected.className = "skills-detected"; detected.id = "skills-detected";
  detectedWrap.appendChild(detected);
  const closeBtn = document.createElement("button"); closeBtn.type = "button"; closeBtn.className = "skill-add"; closeBtn.textContent = "× close";
  closeBtn.style.alignSelf = "flex-end";
  closeBtn.onclick = () => detectedWrap.classList.add("hidden");
  detectedWrap.appendChild(closeBtn);
  wrap.appendChild(detectedWrap);

  function rerender() {
    list.innerHTML = "";
    profile.slots.forEach((slot, idx) => list.appendChild(renderSkillRow(slot, idx, profile, rerender, f)));
    // Re-render the detected panel too so "+ import"/"imported" badges reflect the
    // current profile contents. The diff key in renderDetectedSkills considers profile
    // state, so this is necessary whenever slots change locally.
    if (lastLiveSkills) renderDetectedSkills(lastLiveSkills, /*forceRedraw=*/true);
  }
  rerender();

  importDetectedSkill = (entry) => {
    const def = SLOT_DEFAULT_KEY[entry.barSlot] || { vk: 0, label: "" };
    const labelName = entry.name && entry.name.length > 0
      ? entry.name
      : (def.label && def.label !== "—" ? `${def.label} skill` : `Skill ${entry.barSlot}`);
    profile.slots.push({
      name: labelName,
      vk: def.vk, role: 0, canCrossGaps: false,
      minCastIntervalMs: 100, maxRangeGrid: 30,
      chargeCount: Math.max(1, entry.maxUses || 1), chargeRechargeMs: 3000,
      gemId: entry.gemId,
    });
    pushSettings();
    rerender();
  };

  const buttonRow = document.createElement("div"); buttonRow.className = "skill-add-row";
  const showAdd = document.createElement("button"); showAdd.type = "button"; showAdd.className = "skill-add";
  showAdd.textContent = "+ add skill";
  showAdd.onclick = () => {
    detectedWrap.classList.remove("hidden");
    if (lastLiveSkills) renderDetectedSkills(lastLiveSkills, /*forceRedraw=*/true);
  };
  const addManual = document.createElement("button"); addManual.type = "button"; addManual.className = "skill-add";
  addManual.textContent = "+ blank slot";
  addManual.title = "Add an empty skill slot to fill in by hand (use 'add skill' to import detected skills)";
  addManual.onclick = () => {
    profile.slots.push({ name: "New", vk: 0, role: 0, canCrossGaps: false, minCastIntervalMs: 100, maxRangeGrid: 30, chargeCount: 1, chargeRechargeMs: 3000, gemId: 0 });
    pushSettings();
    rerender();
  };
  buttonRow.appendChild(showAdd);
  buttonRow.appendChild(addManual);
  wrap.appendChild(buttonRow);
  return wrap;
}

let importDetectedSkill = null;
let renderedLiveSkillsKey = "";
let lastLiveSkills = null;

function renderDetectedSkills(liveSkills, forceRedraw = false) {
  lastLiveSkills = liveSkills;
  const root = $("skills-detected");
  if (!root) return;

  // Skip rendering when the panel is hidden — saves a tiny bit of work and avoids
  // touching the DOM while it's collapsed.
  const wrap = root.closest(".skills-detected-wrap");
  if (wrap?.classList.contains("hidden") && !forceRedraw) return;

  // Diff key includes BOTH the live-skills payload and the profile's imported gem ids,
  // so the panel re-renders when either side changes. Without including profile, the
  // imported badge wouldn't update after add/remove.
  const importedKey = (settings.skills?.slots || []).map(s => s.gemId || 0).sort().join(",");
  const key = JSON.stringify(liveSkills || []) + "|" + importedKey;
  if (!forceRedraw && key === renderedLiveSkillsKey) return;
  renderedLiveSkillsKey = key;

  if (!liveSkills || liveSkills.length === 0) {
    root.innerHTML = "<div class='detected-empty'>(no skills detected — log into a character)</div>";
    return;
  }

  const profile = settings.skills?.slots || [];
  const importedIds = new Set(profile.map(s => Number(s.gemId)).filter(Boolean));

  // Slots 0-7 are the visible hotbar (LMB/MMB/RMB + QWERT). 8-12 are PoE's "extras" —
  // duplicate or modifier-bound entries that don't appear on the visible bar. Render
  // the visible ones first, then the extras under a less-prominent subhead.
  const visible = liveSkills.filter(e => e.barSlot < 8);
  const extras  = liveSkills.filter(e => e.barSlot >= 8);

  root.innerHTML = "<div class='detected-head'>Detected skills (visible bar)</div>";
  root.appendChild(buildDetectedGrid(visible, importedIds));
  if (extras.length > 0) {
    const sub = document.createElement("div");
    sub.className = "detected-head detected-head-sub";
    sub.textContent = "Extras (modifier-bound / duplicates — usually skip)";
    root.appendChild(sub);
    root.appendChild(buildDetectedGrid(extras, importedIds));
  }
}

function buildDetectedGrid(entries, importedIds) {
  const grid = document.createElement("div"); grid.className = "detected-grid";
  for (const e of entries) {
    const card = document.createElement("div"); card.className = "detected-card";
    const ready = e.isReady ? "✓" : "•";
    const readyClass = e.isReady ? "good" : "warn";
    const def = SLOT_DEFAULT_KEY[e.barSlot] || { vk: 0, label: "" };
    const label = e.name ? e.name : `Skill #${e.gemId}`;
    const keyLabel = def.label && def.label !== "—" ? def.label : `slot ${e.barSlot}`;
    card.innerHTML =
      `<div class="d-key">${escapeHtml(keyLabel)}</div>` +
      `<div class="d-name">${escapeHtml(label)} <span class="d-ready ${readyClass}">${ready}</span></div>` +
      `<div class="d-meta">gem ${e.gemId}${e.maxUses ? " · " + e.maxUses + "x" : ""}</div>`;
    if (importedIds.has(Number(e.gemId))) {
      const tag = document.createElement("span"); tag.className = "d-imported"; tag.textContent = "imported";
      card.appendChild(tag);
    } else {
      const btn = document.createElement("button"); btn.type = "button"; btn.className = "d-import";
      btn.textContent = "+ import";
      btn.onclick = () => { if (importDetectedSkill) importDetectedSkill(e); };
      card.appendChild(btn);
    }
    grid.appendChild(card);
  }
  return grid;
}

function renderSkillRow(slot, idx, profile, rerender, f) {
  const row = document.createElement("div"); row.className = "skill-row";

  // Name
  const name = document.createElement("input"); name.type = "text"; name.className = "skill-name";
  name.value = slot.name || ""; name.placeholder = "Name";
  name.onchange = () => { slot.name = name.value; pushSettings(); };
  row.appendChild(name);

  // Key
  const keyBtn = document.createElement("button"); keyBtn.type = "button"; keyBtn.className = "key-btn";
  keyBtn.textContent = vkLabel(slot.vk);
  keyBtn.onclick = () => captureKey(keyBtn, vk => { slot.vk = vk; pushSettings(); keyBtn.textContent = vkLabel(vk); });
  row.appendChild(keyBtn);

  // Role
  const roleSel = document.createElement("select");
  ROLE_NAMES.forEach((rn, i) => {
    const opt = document.createElement("option"); opt.value = i; opt.textContent = rn;
    if (Number(slot.role) === i) opt.selected = true;
    roleSel.appendChild(opt);
  });
  roleSel.onchange = () => { slot.role = parseInt(roleSel.value); pushSettings(); rerender(); };
  row.appendChild(roleSel);

  // Cross-gaps (only meaningful for Dash)
  if (Number(slot.role) === 2) {
    const lbl = document.createElement("label"); lbl.className = "skill-flag";
    const cg = document.createElement("input"); cg.type = "checkbox"; cg.checked = !!slot.canCrossGaps;
    cg.onchange = () => { slot.canCrossGaps = cg.checked; pushSettings(); };
    lbl.appendChild(cg);
    lbl.appendChild(document.createTextNode(" cross gaps"));
    row.appendChild(lbl);
  }

  // Numeric fields
  row.appendChild(numField("interval ms", slot.minCastIntervalMs, 0, 10000, v => { slot.minCastIntervalMs = v; pushSettings(); }));
  row.appendChild(numField("range",       slot.maxRangeGrid,      1, 200,   v => { slot.maxRangeGrid      = v; pushSettings(); }));
  row.appendChild(numField("gemId",       slot.gemId || 0,        0, 65535, v => { slot.gemId             = v; pushSettings(); }));
  if (Number(slot.role) === 2) {
    row.appendChild(numField("charges",   slot.chargeCount,       1, 10,    v => { slot.chargeCount       = v; pushSettings(); }));
    row.appendChild(numField("recharge ms", slot.chargeRechargeMs, 100, 30000, v => { slot.chargeRechargeMs = v; pushSettings(); }));
  }

  // Remove
  const rm = document.createElement("button"); rm.type = "button"; rm.className = "skill-remove"; rm.textContent = "×";
  rm.title = "Remove";
  rm.onclick = () => { profile.slots.splice(idx, 1); pushSettings(); rerender(); };
  row.appendChild(rm);

  return row;
}

function numField(label, value, min, max, onChange, isFloat) {
  const wrap = document.createElement("label"); wrap.className = "skill-num";
  wrap.textContent = label + " ";
  const inp = document.createElement("input"); inp.type = "number";
  inp.value = value; inp.min = min; inp.max = max;
  if (isFloat) inp.step = 0.05;
  inp.onchange = () => onChange(isFloat ? (parseFloat(inp.value) || 0) : (parseInt(inp.value) || 0));
  wrap.appendChild(inp);
  return wrap;
}

// ── Flasks editor ──────────────────────────────────────────────────────────

const FLASK_TRIGGERS = ["Disabled", "Hp", "Mana", "Time", "BuffMissing"];

function renderFlasksEditor(f) {
  const wrap = document.createElement("div"); wrap.className = "skills-editor";
  const profile = settings[f.name] || (settings[f.name] = { slots: [] });
  if (!profile.slots) profile.slots = [];

  const list = document.createElement("div"); list.className = "skills-list";
  wrap.appendChild(list);

  function rerender() {
    list.innerHTML = "";
    profile.slots.forEach((slot, idx) => list.appendChild(renderFlaskRow(slot, idx, profile, rerender, f)));
  }
  rerender();

  const add = document.createElement("button"); add.type = "button"; add.className = "skill-add";
  add.textContent = "+ add flask";
  add.onclick = () => {
    profile.slots.push({ name: "Flask", vk: 0, trigger: 0, hpThreshold: 0.6, manaThreshold: 0.3, intervalMs: 5000, buffName: "", cooldownMs: 4500 });
    pushSettings(); rerender();
  };
  wrap.appendChild(add);
  return wrap;
}

function renderFlaskRow(slot, idx, profile, rerender, f) {
  const row = document.createElement("div"); row.className = "skill-row";
  const name = document.createElement("input"); name.type = "text"; name.className = "skill-name";
  name.value = slot.name || ""; name.placeholder = "Name";
  name.onchange = () => { slot.name = name.value; pushSettings(); };
  row.appendChild(name);

  const keyBtn = document.createElement("button"); keyBtn.type = "button"; keyBtn.className = "key-btn";
  keyBtn.textContent = vkLabel(slot.vk);
  keyBtn.onclick = () => captureKey(keyBtn, vk => { slot.vk = vk; pushSettings(); keyBtn.textContent = vkLabel(vk); });
  row.appendChild(keyBtn);

  const tSel = document.createElement("select");
  FLASK_TRIGGERS.forEach((tn, i) => {
    const opt = document.createElement("option"); opt.value = i; opt.textContent = tn;
    if (Number(slot.trigger) === i) opt.selected = true;
    tSel.appendChild(opt);
  });
  tSel.onchange = () => { slot.trigger = parseInt(tSel.value); pushSettings(); rerender(); };
  row.appendChild(tSel);

  if (Number(slot.trigger) === 1) row.appendChild(numField("HP < ", slot.hpThreshold, 0, 1, v => { slot.hpThreshold = v; pushSettings(); }, true));
  if (Number(slot.trigger) === 2) row.appendChild(numField("Mana < ", slot.manaThreshold, 0, 1, v => { slot.manaThreshold = v; pushSettings(); }, true));
  if (Number(slot.trigger) === 4) {
    const bn = document.createElement("input"); bn.type = "text"; bn.placeholder = "buff name"; bn.value = slot.buffName || "";
    bn.onchange = () => { slot.buffName = bn.value; pushSettings(); };
    row.appendChild(bn);
  }
  row.appendChild(numField("cooldown ms", slot.cooldownMs, 0, 60000, v => { slot.cooldownMs = v; pushSettings(); }));

  const rm = document.createElement("button"); rm.type = "button"; rm.className = "skill-remove"; rm.textContent = "×";
  rm.onclick = () => { profile.slots.splice(idx, 1); pushSettings(); rerender(); };
  row.appendChild(rm);
  return row;
}

function captureKey(btn, cb) {
  btn.classList.add("capturing");
  btn.textContent = "press a key…";
  const handler = (e) => {
    e.preventDefault(); e.stopPropagation();
    window.removeEventListener("keydown", handler, true);
    btn.classList.remove("capturing");
    // e.keyCode is the Win32 VK on most keyboards in current browsers
    cb(e.keyCode);
  };
  window.addEventListener("keydown", handler, true);
}

// ── Status (WebSocket) ─────────────────────────────────────────────────────

function setConn(state, text) {
  const el = $("connection");
  el.className = "conn-pill " + state;
  el.textContent = text;
}

function row(k, v, cls = "") {
  return `<div class="status-row"><span class="k">${k}</span><span class="v ${cls}">${v}</span></div>`;
}

let lastEventsKey = "";
function renderEvents(events) {
  const root = $("events");
  if (!root) return;
  const key = events.length === 0 ? "0" : (events[0].seq + ":" + events.length);
  if (key === lastEventsKey) return;
  lastEventsKey = key;
  const cnt = $("events-count");
  if (cnt) cnt.textContent = `(${events.length})`;
  // Newest first.
  root.innerHTML = events.map(e => {
    const cat = `<span class="ev-cat">[${escapeHtml(e.category)}]</span>`;
    const ts  = `<span class="ev-t">${escapeHtml(e.t)}</span>`;
    return `<div class="ev-row">${ts} ${cat} <span class="ev-msg">${escapeHtml(e.message)}</span></div>`;
  }).join("");
}

function renderTree(nodes) {
  const root = $("tree");
  if (!nodes || nodes.length === 0) { root.innerHTML = "<div class='tree-empty'>(no active mode)</div>"; return; }
  root.innerHTML = nodes.map(n => {
    const indent = "&nbsp;&nbsp;".repeat(n.depth);
    const cls = n.status === "Success" ? "good" : n.status === "Failure" ? "bad" : n.status === "Running" ? "warn" : "";
    return `<div class="tree-row"><span class="t">${indent}${escapeHtml(n.name)}</span><span class="s ${cls}">${n.status}</span></div>`;
  }).join("");
}

function escapeHtml(s) { return String(s).replace(/[&<>"']/g, c => ({"&":"&amp;","<":"&lt;",">":"&gt;","\"":"&quot;","'":"&#39;"}[c])); }

function renderStatus(s) {
  if (s.tree) renderTree(s.tree);
  if (s.liveSkills) renderDetectedSkills(s.liveSkills);
  if (s.events) renderEvents(s.events);
  if (s.profile) {
    const el = $("connection");
    if (el && s.profile.length > 0) el.title = "profile: " + s.profile;
  }
  const grid = $("status-grid");
  const stateClass = !s.connected ? "bad"
                   : !s.shouldAct ? "warn"
                   : s.shouldLoot ? "good"
                   : "";
  grid.innerHTML = [
    row("State",        s.stateLabel,    stateClass),
    row("Foreground",   s.foreground ? "yes" : "no",   s.foreground ? "good" : "warn"),
    row("HP",           `${s.playerHp} / ${s.playerHpMax}`),
    row("Grid",         `(${s.playerGridX}, ${s.playerGridY})`),
    row("Area",         `0x${(s.areaHash || 0).toString(16).toUpperCase()}`),
    row("Labels",       s.labelsVisible),
    row("Loot key",     s.lootKeyHeld ? "HELD" : "released", s.lootKeyHeld ? "good" : ""),
    row("Mode",         s.mode || "—"),
    row("Decision",     s.modeDecision || s.lootDecision || ""),
    row("Input",        s.inputState),
  ].join("");
}

function connectStatus() {
  const proto = location.protocol === "https:" ? "wss:" : "ws:";
  const ws = new WebSocket(`${proto}//${location.host}/ws`);
  setConn("connecting", "connecting…");
  ws.onopen  = () => setConn("ok", "live");
  ws.onclose = () => { setConn("bad", "disconnected"); setTimeout(connectStatus, 1000); };
  ws.onerror = () => ws.close();
  ws.onmessage = e => { try { renderStatus(JSON.parse(e.data)); } catch {} };
}

(async () => {
  await loadSchema();
  await loadSettings();
  renderSettings();
  connectStatus();
})();
