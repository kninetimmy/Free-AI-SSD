// Per-device conversation history, persisted to localStorage. No server round
// trip — each browser keeps its own chat log (owner decision: localStorage,
// per-device). Shape is intentionally small and forward-compatible.
//
// Conversation: { id, title, model, messages: Message[], createdAt, updatedAt }
// Message:      { role: "user"|"assistant", content, thinking?, sources?, error?, ts }

const STORE_KEY = "freeai.chat.conversations.v1";

function load() {
  try {
    const raw = localStorage.getItem(STORE_KEY);
    if (!raw) return [];
    const data = JSON.parse(raw);
    return Array.isArray(data) ? data : [];
  } catch {
    return [];
  }
}

function persist(list) {
  try {
    localStorage.setItem(STORE_KEY, JSON.stringify(list));
  } catch (err) {
    // Quota or private-mode failure: keep running in-memory rather than crash.
    console.warn("Could not persist chat history:", err);
  }
}

/** Conversations, most-recently-updated first. */
export function listConversations() {
  return load().sort((a, b) => (b.updatedAt || 0) - (a.updatedAt || 0));
}

export function getConversation(id) {
  return load().find((c) => c.id === id) || null;
}

export function createConversation(model) {
  const now = Date.now();
  const conv = {
    id: `c_${now}_${Math.random().toString(36).slice(2, 8)}`,
    title: "New Chat",
    model: model || "",
    messages: [],
    createdAt: now,
    updatedAt: now,
  };
  const list = load();
  list.push(conv);
  persist(list);
  return conv;
}

/** Upsert a full conversation object (caller mutates messages then saves). */
export function saveConversation(conv) {
  if (!conv || !conv.id) return;
  conv.updatedAt = Date.now();
  const list = load();
  const idx = list.findIndex((c) => c.id === conv.id);
  if (idx >= 0) list[idx] = conv;
  else list.push(conv);
  persist(list);
}

export function renameConversation(id, title) {
  const list = load();
  const conv = list.find((c) => c.id === id);
  if (!conv) return;
  conv.title = (title || "").trim() || conv.title;
  conv.updatedAt = Date.now();
  persist(list);
}

export function deleteConversation(id) {
  persist(load().filter((c) => c.id !== id));
}

/** Derive a short title from the first user message. */
export function titleFromPrompt(prompt) {
  const oneLine = (prompt || "").replace(/\s+/g, " ").trim();
  if (!oneLine) return "New Chat";
  return oneLine.length > 42 ? oneLine.slice(0, 42) + "…" : oneLine;
}
