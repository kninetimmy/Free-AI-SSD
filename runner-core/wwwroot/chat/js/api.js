// API client for the Runner LAN HTTP host (RunnerLocalApiService).
// Same-origin by default (the SPA is served from the same Kestrel), so all URLs
// are relative unless the user pins a different host in Settings. The API key
// (when the host requires one) is stored only in this browser and sent as
// X-API-Key on every /api call. /api/health is unauthenticated.

const HOST_KEY = "freeai.chat.host";
const APIKEY_KEY = "freeai.chat.apikey";

export function getHost() {
  return (localStorage.getItem(HOST_KEY) || "").trim();
}
export function setHost(value) {
  const v = (value || "").trim().replace(/\/+$/, "");
  if (v) localStorage.setItem(HOST_KEY, v);
  else localStorage.removeItem(HOST_KEY);
}
export function getApiKey() {
  return localStorage.getItem(APIKEY_KEY) || "";
}
export function setApiKey(value) {
  if (value) localStorage.setItem(APIKEY_KEY, value);
  else localStorage.removeItem(APIKEY_KEY);
}

function url(path) {
  const host = getHost();
  return host ? `${host}${path}` : path;
}

function authHeaders(extra) {
  const headers = Object.assign({}, extra);
  const key = getApiKey();
  if (key) headers["X-API-Key"] = key;
  return headers;
}

/** Lightweight connectivity + auth probe. Returns the parsed /api/health body. */
export async function checkHealth() {
  const res = await fetch(url("/api/health"), { headers: authHeaders() });
  if (!res.ok) throw new ApiError(res.status, "Host did not respond OK to /api/health.");
  return res.json();
}

/** GET /api/models → { models: string[], embeddingModelInstalled: bool }. */
export async function getModels() {
  const res = await fetch(url("/api/models"), { headers: authHeaders() });
  await ensureOk(res);
  return res.json();
}

/** GET /api/library → library list (shape: { libraries, activeLibraryId }). */
export async function getLibraries() {
  const res = await fetch(url("/api/library"), { headers: authHeaders() });
  await ensureOk(res);
  return res.json();
}

/** PUT /api/library/active — null/empty clears the active library. */
export async function setActiveLibrary(libraryId) {
  const res = await fetch(url("/api/library/active"), {
    method: "PUT",
    headers: authHeaders({ "Content-Type": "application/json" }),
    body: JSON.stringify({ libraryId: libraryId || null }),
  });
  await ensureOk(res);
  return res.json();
}

/**
 * POST /api/chat/stream and dispatch NDJSON frames to handlers:
 *   onStart(frame) · onLoading(seconds) · onToken(text) ·
 *   onRagWarning(message) · onComplete({ responseText, sources, usedRagContext }) ·
 *   onError(message)
 * `params` carries optional per-request overrides (temperature, think, etc.);
 * null/undefined fields are omitted so the host falls back to its saved config.
 * Returns a function that aborts the in-flight request.
 */
export function streamChat({ model, prompt, params }, handlers, signal) {
  const controller = new AbortController();
  if (signal) signal.addEventListener("abort", () => controller.abort());

  const body = Object.assign({ model, prompt }, cleanParams(params));

  const promise = (async () => {
    let res;
    try {
      res = await fetch(url("/api/chat/stream"), {
        method: "POST",
        headers: authHeaders({ "Content-Type": "application/json" }),
        body: JSON.stringify(body),
        signal: controller.signal,
      });
    } catch (err) {
      if (controller.signal.aborted) return;
      handlers.onError?.(networkMessage(err));
      return;
    }

    if (!res.ok) {
      handlers.onError?.(await errorText(res));
      return;
    }

    const reader = res.body.getReader();
    const decoder = new TextDecoder();
    let buffer = "";
    try {
      while (true) {
        const { value, done } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true });
        let nl;
        while ((nl = buffer.indexOf("\n")) >= 0) {
          const line = buffer.slice(0, nl).trim();
          buffer = buffer.slice(nl + 1);
          if (line) dispatchFrame(line, handlers);
        }
      }
      const tail = buffer.trim();
      if (tail) dispatchFrame(tail, handlers);
    } catch (err) {
      if (!controller.signal.aborted) handlers.onError?.(networkMessage(err));
    }
  })();

  return { abort: () => controller.abort(), done: promise };
}

function dispatchFrame(line, handlers) {
  let frame;
  try {
    frame = JSON.parse(line);
  } catch {
    return; // ignore malformed line
  }
  switch (frame.type) {
    case "start": handlers.onStart?.(frame); break;
    case "loading": handlers.onLoading?.(frame.elapsedSeconds ?? 0); break;
    case "token": handlers.onToken?.(frame.token ?? ""); break;
    case "rag-warning": handlers.onRagWarning?.(frame.message ?? ""); break;
    case "complete":
      handlers.onComplete?.({
        responseText: frame.responseText ?? "",
        sources: frame.sources ?? [],
        usedRagContext: !!frame.usedRagContext,
      });
      break;
    case "error": handlers.onError?.(frame.message ?? "Chat failed."); break;
    default: break;
  }
}

function cleanParams(params) {
  const out = {};
  if (!params) return out;
  if (isNum(params.temperature)) out.temperature = params.temperature;
  if (isNum(params.topP)) out.topP = params.topP;
  if (isNum(params.maxOutputTokens)) out.maxOutputTokens = params.maxOutputTokens;
  if (isNum(params.contextWindow)) out.contextWindow = params.contextWindow;
  if (params.think) out.think = params.think;
  return out;
}
const isNum = (v) => typeof v === "number" && !Number.isNaN(v);

export class ApiError extends Error {
  constructor(status, message) {
    super(message);
    this.status = status;
  }
}

async function ensureOk(res) {
  if (!res.ok) throw new ApiError(res.status, await errorText(res));
}

async function errorText(res) {
  if (res.status === 401) return "Unauthorized — check your API key in Settings.";
  if (res.status === 503) return "Host is not ready (is Ollama running?).";
  try {
    const data = await res.json();
    if (data && (data.error || data.detail)) return data.error || data.detail;
  } catch { /* fall through */ }
  return `Request failed (HTTP ${res.status}).`;
}

function networkMessage(err) {
  return "Could not reach the host. Check the connection and the Host/API key in Settings.";
}
