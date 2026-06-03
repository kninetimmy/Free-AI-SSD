// Free-AI web chat — app orchestration. Wires the sidebar, composer, model /
// library pickers, settings, and per-device history to the streaming chat API.

import * as api from "./js/api.js";
import * as history from "./js/history.js";
import { streamAssistantResponse } from "./js/chat.js";

const MODEL_KEY = "freeai.chat.model";
const PARAMS_KEY = "freeai.chat.params";

const $ = (id) => document.getElementById(id);

const state = {
  models: [],
  activeModel: "",
  libraries: [],
  activeLibraryId: null,
  conversation: null,
  params: loadParams(), // { temperature: number|null, think: string }
  streaming: null, // active stream controller
};

// ---------------- boot ----------------
document.addEventListener("DOMContentLoaded", init);

async function init() {
  wireStaticEvents();
  renderChatList();

  let health;
  try {
    health = await api.checkHealth();
    setConnection(true);
  } catch {
    setConnection(false);
    setHint("Can't reach the host. Open Settings to set the host / API key.");
  }

  if (health && health.requireApiKey && !api.getApiKey()) {
    openApiKeyModal();
    return; // finish boot after the key is entered
  }

  await loadServerState();
}

async function loadServerState() {
  await Promise.all([loadModels(), loadLibraries()]);
}

async function loadModels() {
  try {
    const data = await api.getModels();
    state.models = data.models || [];
    const remembered = localStorage.getItem(MODEL_KEY);
    state.activeModel = state.models.includes(remembered) ? remembered : (state.models[0] || "");
    renderModelSelect();
    if (state.models.length === 0) setHint("No models installed on the host.");
    else if (data.embeddingModelInstalled === false) setHint("Embedding model missing — RAG answers will be unavailable.");
    else setHint("");
  } catch (err) {
    handleApiError(err);
  }
}

async function loadLibraries() {
  try {
    const data = await api.getLibraries();
    state.libraries = data.libraries || [];
    state.activeLibraryId = data.activeLibraryId || null;
    renderActiveLibraryName(data.activeLibrary);
  } catch (err) {
    handleApiError(err);
  }
}

// ---------------- rendering ----------------
function renderModelSelect() {
  const sel = $("model-select");
  sel.innerHTML = "";
  if (state.models.length === 0) {
    const o = document.createElement("option");
    o.textContent = "no models";
    o.value = "";
    sel.append(o);
    sel.disabled = true;
    return;
  }
  sel.disabled = false;
  for (const m of state.models) {
    const o = document.createElement("option");
    o.value = m;
    o.textContent = m;
    if (m === state.activeModel) o.selected = true;
    sel.append(o);
  }
}

function renderActiveLibraryName(activeLibrary) {
  const name = activeLibrary?.name
    || state.libraries.find((l) => l.id === state.activeLibraryId)?.name
    || "None";
  $("active-library-name").textContent = name;
}

function renderChatList() {
  const list = $("chat-list");
  const empty = $("chat-list-empty");
  const convos = history.listConversations();
  list.innerHTML = "";
  empty.classList.toggle("hidden", convos.length > 0);

  for (const c of convos) {
    const li = document.createElement("li");
    li.className = "chat-item" + (state.conversation && c.id === state.conversation.id ? " active" : "");
    li.dataset.id = c.id;

    const title = document.createElement("span");
    title.className = "chat-item-title";
    title.textContent = c.title || "Untitled";
    title.addEventListener("click", () => selectChat(c.id));

    const actions = document.createElement("span");
    actions.className = "chat-item-actions";
    actions.append(
      iconButton("✎", "Rename", (e) => { e.stopPropagation(); renameChat(c.id); }),
      iconButton("🗑", "Delete", (e) => { e.stopPropagation(); deleteChat(c.id); }),
    );

    li.append(title, actions);
    list.append(li);
  }
}

function renderConversation() {
  const listEl = $("message-list");
  listEl.innerHTML = "";
  const hero = $("hero");
  const conv = state.conversation;

  if (!conv || conv.messages.length === 0) {
    hero.classList.remove("hidden");
    $("conversation-title").textContent = conv ? conv.title : "New Chat";
    return;
  }
  hero.classList.add("hidden");
  $("conversation-title").textContent = conv.title;

  for (const m of conv.messages) {
    if (m.role === "user") appendUserBubble(m.content, m.ts);
    else renderStoredAssistant(m);
  }
  scrollToBottom();
}

function appendUserBubble(text, ts) {
  const wrap = el("div", "msg msg-user");
  const meta = el("div", "msg-meta");
  meta.append(span("msg-author", "You"), span("", fmtTime(ts)));
  const bubble = el("div", "bubble-user");
  bubble.textContent = text;
  wrap.append(meta, bubble);
  $("message-list").append(wrap);
  return wrap;
}

function makeAssistantCard(ts) {
  const wrap = el("div", "msg msg-assistant");
  const meta = el("div", "msg-meta");
  meta.append(span("msg-author", state.activeModel || "assistant"), span("", fmtTime(ts)));
  const card = el("div", "card-assistant");
  wrap.append(meta, card);
  $("message-list").append(wrap);
  return card;
}

function renderStoredAssistant(m) {
  const card = makeAssistantCard(m.ts);
  if (m.thinking) {
    const thinking = el("div", "thinking collapsed done");
    const head = el("div", "thinking-head");
    head.append(span("", "Thought"), withClass(span("chev", "▾"), "chev"));
    const body = el("div", "thinking-body");
    body.textContent = m.thinking;
    thinking.append(head, body);
    head.addEventListener("click", () => thinking.classList.toggle("collapsed"));
    card.append(thinking);
  }
  if (m.error) {
    const e = el("div", "msg-error");
    e.textContent = m.error;
    card.append(e);
  }
  const ans = el("div", "assistant-text");
  ans.textContent = m.content || (m.error ? "" : "(no answer)");
  card.append(ans);
  if (m.sources && m.sources.length) {
    const sources = el("div", "sources");
    sources.append(span("sources-title", "Sources"));
    for (const s of m.sources) {
      const chip = el("span", "source-chip");
      chip.textContent = s;
      sources.append(chip);
    }
    card.append(sources);
  }
}

// ---------------- actions ----------------
function newChat() {
  state.conversation = null;
  renderConversation();
  renderChatList();
  closeSidebarOnMobile();
  $("prompt-input").focus();
}

function selectChat(id) {
  state.conversation = history.getConversation(id);
  renderConversation();
  renderChatList();
  closeSidebarOnMobile();
}

function renameChat(id) {
  const conv = history.getConversation(id);
  const next = window.prompt("Rename conversation:", conv?.title || "");
  if (next === null) return;
  history.renameConversation(id, next);
  if (state.conversation?.id === id) state.conversation = history.getConversation(id);
  renderChatList();
  renderConversation();
}

function deleteChat(id) {
  if (!window.confirm("Delete this conversation? This can't be undone.")) return;
  history.deleteConversation(id);
  if (state.conversation?.id === id) state.conversation = null;
  renderChatList();
  renderConversation();
}

async function send() {
  if (state.streaming) return;
  const input = $("prompt-input");
  const prompt = input.value.trim();
  if (!prompt) return;
  if (!state.activeModel) { setHint("No model selected."); return; }

  if (!state.conversation) state.conversation = history.createConversation(state.activeModel);
  const conv = state.conversation;
  conv.model = state.activeModel;
  if (conv.messages.length === 0) conv.title = history.titleFromPrompt(prompt);

  const userTs = Date.now();
  conv.messages.push({ role: "user", content: prompt, ts: userTs });
  history.saveConversation(conv);

  $("hero").classList.add("hidden");
  appendUserBubble(prompt, userTs);
  input.value = "";
  autoGrow(input);
  renderChatList();
  $("conversation-title").textContent = conv.title;

  const card = makeAssistantCard(Date.now());
  scrollToBottom();
  setStreaming(true);

  const controller = streamAssistantResponse({
    model: state.activeModel,
    prompt,
    params: buildParams(),
    mountEl: card,
  });
  state.streaming = controller;

  // Keep the view pinned to the bottom as tokens arrive.
  const pin = setInterval(scrollToBottom, 150);

  let result;
  try {
    result = await controller.done;
  } finally {
    clearInterval(pin);
    state.streaming = null;
    setStreaming(false);
  }

  conv.messages.push({
    role: "assistant",
    content: result.answer || "",
    thinking: result.thinking || "",
    sources: result.sources || [],
    error: result.error || null,
    ts: Date.now(),
  });
  history.saveConversation(conv);
  renderChatList();
  scrollToBottom();
  $("prompt-input").focus();
}

function stop() {
  if (state.streaming) state.streaming.abort();
}

// ---------------- settings / library / api key ----------------
function openSettings() {
  const p = state.params;
  const slider = $("set-temperature");
  const label = $("set-temperature-value");
  if (typeof p.temperature === "number") {
    slider.value = String(p.temperature);
    label.textContent = p.temperature.toFixed(2);
  } else {
    slider.value = "0.8";
    label.textContent = "default";
  }
  $("set-think").value = p.think || "";
  $("set-host").value = api.getHost();
  $("set-apikey").value = api.getApiKey();
  showModal("settings-modal");
}

function saveSettings() {
  const label = $("set-temperature-value");
  const temp = label.textContent === "default" ? null : parseFloat($("set-temperature").value);
  state.params = { temperature: Number.isNaN(temp) ? null : temp, think: $("set-think").value };
  saveParams(state.params);

  const hostChanged = api.getHost() !== $("set-host").value.trim();
  const keyChanged = api.getApiKey() !== $("set-apikey").value;
  api.setHost($("set-host").value);
  api.setApiKey($("set-apikey").value);
  hideModal("settings-modal");

  if (hostChanged || keyChanged) {
    setHint("Reconnecting…");
    init();
  }
}

function openLibraryModal() {
  const ul = $("library-list");
  ul.innerHTML = "";
  ul.append(libraryRow(null, "None", "plain chat — no document grounding"));
  for (const lib of state.libraries) {
    ul.append(libraryRow(lib.id, lib.name, lib.id));
  }
  showModal("library-modal");
}

function libraryRow(id, name, meta) {
  const li = el("li", "library-item" + ((id || null) === state.activeLibraryId ? " active" : ""));
  li.append(el("span", "library-item-radio"));
  li.append(withText(el("span", "library-item-name"), name));
  if (meta && meta !== name) li.append(withText(el("span", "library-item-meta"), meta));
  li.addEventListener("click", () => selectLibrary(id));
  return li;
}

async function selectLibrary(id) {
  try {
    const res = await api.setActiveLibrary(id);
    state.activeLibraryId = res.activeLibraryId || null;
    renderActiveLibraryName(res.activeLibrary);
    hideModal("library-modal");
  } catch (err) {
    handleApiError(err);
  }
}

function openApiKeyModal() {
  $("apikey-input").value = api.getApiKey();
  $("apikey-error").classList.add("hidden");
  showModal("apikey-modal");
}

async function saveApiKey() {
  const key = $("apikey-input").value.trim();
  api.setApiKey(key);
  try {
    await api.getModels(); // authenticated probe
    hideModal("apikey-modal");
    setConnection(true);
    await loadServerState();
  } catch (err) {
    const errEl = $("apikey-error");
    errEl.textContent = err.status === 401 ? "That key was rejected. Try again." : "Couldn't reach the host.";
    errEl.classList.remove("hidden");
  }
}

// ---------------- helpers ----------------
function buildParams() {
  const p = state.params;
  return {
    temperature: typeof p.temperature === "number" ? p.temperature : null,
    think: p.think || null,
  };
}

function loadParams() {
  try {
    const raw = localStorage.getItem(PARAMS_KEY);
    if (raw) return JSON.parse(raw);
  } catch { /* ignore */ }
  return { temperature: null, think: "" };
}
function saveParams(p) {
  localStorage.setItem(PARAMS_KEY, JSON.stringify(p));
}

function setStreaming(on) {
  $("send-btn").classList.toggle("hidden", on);
  $("stop-btn").classList.toggle("hidden", !on);
  $("prompt-input").disabled = false;
}

function setConnection(ok) {
  const dot = $("connection-dot");
  dot.classList.toggle("ok", ok);
  dot.classList.toggle("bad", !ok);
  dot.title = ok ? "Connected" : "Disconnected";
}

function setHint(text) { $("composer-hint").textContent = text || ""; }

function handleApiError(err) {
  if (err && err.status === 401) {
    setConnection(false);
    openApiKeyModal();
  } else {
    setHint(err?.message || "Request failed.");
  }
}

function scrollToBottom() {
  const m = $("messages");
  m.scrollTop = m.scrollHeight;
}

function fmtTime(ts) {
  if (!ts) return "";
  return new Date(ts).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
}

function autoGrow(ta) {
  ta.style.height = "auto";
  ta.style.height = Math.min(ta.scrollHeight, 220) + "px";
}

// sidebar (mobile)
function toggleSidebar() { document.getElementById("app").classList.toggle("sidebar-open"); }
function closeSidebarOnMobile() { document.getElementById("app").classList.remove("sidebar-open"); }

// modal helpers
function showModal(id) { $(id).classList.remove("hidden"); }
function hideModal(id) { $(id).classList.add("hidden"); }

// tiny DOM helpers
function el(tag, className) { const n = document.createElement(tag); if (className) n.className = className; return n; }
function span(className, text) { const n = el("span", className); if (text != null) n.textContent = text; return n; }
function withText(node, text) { node.textContent = text; return node; }
function withClass(node, c) { node.className = c; return node; }
function iconButton(glyph, title, onClick) {
  const b = el("button", "icon-btn");
  b.type = "button";
  b.title = title;
  b.textContent = glyph;
  b.addEventListener("click", onClick);
  return b;
}

// ---------------- event wiring ----------------
function wireStaticEvents() {
  $("new-chat").addEventListener("click", newChat);
  $("send-btn").addEventListener("click", send);
  $("stop-btn").addEventListener("click", stop);
  $("open-settings").addEventListener("click", openSettings);
  $("settings-save").addEventListener("click", saveSettings);
  $("open-library").addEventListener("click", openLibraryModal);
  $("apikey-save").addEventListener("click", saveApiKey);

  $("sidebar-open").addEventListener("click", toggleSidebar);
  $("sidebar-close").addEventListener("click", toggleSidebar);
  $("sidebar-scrim").addEventListener("click", closeSidebarOnMobile);

  $("model-select").addEventListener("change", (e) => {
    state.activeModel = e.target.value;
    localStorage.setItem(MODEL_KEY, state.activeModel);
  });

  const input = $("prompt-input");
  input.addEventListener("input", () => autoGrow(input));
  input.addEventListener("keydown", (e) => {
    if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); send(); }
  });

  // temperature slider live label
  $("set-temperature").addEventListener("input", (e) => {
    $("set-temperature-value").textContent = parseFloat(e.target.value).toFixed(2);
  });
  // click the value to reset to model default (omit the override)
  $("set-temperature-value").addEventListener("click", () => {
    $("set-temperature-value").textContent = "default";
  });

  // close buttons (data-close="modal-id")
  for (const btn of document.querySelectorAll("[data-close]")) {
    btn.addEventListener("click", () => hideModal(btn.dataset.close));
  }

  // global shortcuts
  document.addEventListener("keydown", (e) => {
    if (e.key === "b" && (e.ctrlKey || e.metaKey)) { e.preventDefault(); toggleSidebar(); }
    if (e.key === "Escape") {
      for (const m of document.querySelectorAll(".modal:not(.hidden)")) {
        if (m.id !== "apikey-modal") m.classList.add("hidden");
      }
    }
  });
}
