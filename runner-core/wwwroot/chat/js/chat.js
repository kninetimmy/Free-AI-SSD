// Drives a single assistant turn: mounts the thinking block + answer text +
// sources into a card, streams NDJSON frames from the API, and resolves with
// the finalized message so the caller can persist it to history.
//
// All model/user text is written via textContent (never innerHTML) so a
// response can never inject markup — safe even on a trusted LAN.

import { streamChat } from "./api.js";

/**
 * @param {{ model, prompt, params, mountEl }} opts
 * @returns {{ abort: () => void, done: Promise<{answer, thinking, sources, usedRagContext, error}> }}
 */
export function streamAssistantResponse({ model, prompt, params, mountEl }) {
  const ui = buildTurnDom(mountEl);
  const startedAt = performance.now();
  let raw = "";
  let firstAnswerSeen = false;
  let coldLoad = false;
  let sawError = false;

  // Live elapsed timer for the thinking indicator until the answer starts.
  const timer = setInterval(() => {
    if (firstAnswerSeen) return;
    ui.timerEl.textContent = fmtElapsed(startedAt);
    ui.labelEl.textContent = coldLoad ? "Loading model…" : "Thinking…";
  }, 100);

  function stopTimer() {
    clearInterval(timer);
  }

  function render() {
    const { thinking, answer, thinkingOpen } = splitThink(raw);
    if (thinking) ui.thinkingBody.textContent = thinking;

    if (!firstAnswerSeen && answer.trim().length > 0 && !thinkingOpen) {
      // Reasoning (if any) is done; lock the thinking indicator and reveal answer.
      firstAnswerSeen = true;
      finalizeThinking(ui, thinking, startedAt);
    }
    ui.answerEl.textContent = answer;
  }

  const handlers = {
    onLoading: () => { coldLoad = true; },
    onToken: (t) => { raw += t; render(); },
    onRagWarning: (message) => { ui.showRagWarning(message); },
    onComplete: ({ responseText, sources, usedRagContext }) => {
      if (responseText && responseText.length >= raw.length) raw = responseText;
      const parsed = splitThink(raw);
      if (parsed.thinking) ui.thinkingBody.textContent = parsed.thinking;
      finalizeThinking(ui, parsed.thinking, startedAt);
      ui.answerEl.textContent = parsed.answer.trim().length ? parsed.answer : "(no answer)";
      ui.answerEl.classList.remove("streaming");
      ui.renderSources(sources);
      result.answer = parsed.answer.trim();
      result.thinking = parsed.thinking.trim();
      result.sources = sources || [];
      result.usedRagContext = usedRagContext;
    },
    onError: (message) => {
      sawError = true;
      ui.showError(message);
      ui.answerEl.classList.remove("streaming");
      finalizeThinking(ui, splitThink(raw).thinking, startedAt, /*aborted*/ true);
      result.error = message;
    },
  };

  ui.answerEl.classList.add("streaming");
  const stream = streamChat({ model, prompt, params }, handlers);

  const result = { answer: "", thinking: "", sources: [], usedRagContext: false, error: null };
  const done = stream.done.then(() => {
    stopTimer();
    ui.answerEl.classList.remove("streaming");
    // If no `complete` frame populated the result (stopped / interrupted stream),
    // fall back to whatever streamed so history matches what was displayed.
    if (!result.answer && !result.thinking) {
      const parsed = splitThink(raw);
      result.answer = parsed.answer.trim();
      result.thinking = parsed.thinking.trim();
    }
    if (!firstAnswerSeen && !sawError && !result.error) {
      finalizeThinking(ui, splitThink(raw).thinking, startedAt, true);
      if (!result.answer) ui.answerEl.textContent = ui.answerEl.textContent || "(no response)";
    }
    return result;
  });

  return {
    abort: () => { stream.abort(); stopTimer(); ui.showError("Stopped."); },
    done,
  };
}

// ---- DOM scaffolding for one assistant card ----
function buildTurnDom(mountEl) {
  const thinking = el("div", "thinking");
  const head = el("div", "thinking-head");
  const spinner = el("span", "thinking-spinner");
  const label = el("span", "thinking-label");
  label.textContent = "Thinking…";
  const timer = el("span", "thinking-timer");
  const chev = el("span", "chev");
  chev.textContent = "▾";
  head.append(spinner, label, timer, chev);
  const body = el("div", "thinking-body");
  thinking.append(head, body);
  head.addEventListener("click", () => thinking.classList.toggle("collapsed"));

  const warningHost = el("div", "");
  const answer = el("div", "assistant-text");
  const sources = el("div", "sources hidden");

  mountEl.append(thinking, warningHost, answer, sources);

  return {
    thinkingEl: thinking,
    thinkingBody: body,
    labelEl: label,
    timerEl: timer,
    answerEl: answer,
    showRagWarning(message) {
      const w = el("div", "rag-warning");
      w.textContent = `⚠ ${message}`;
      warningHost.append(w);
    },
    showError(message) {
      const e = el("div", "msg-error");
      e.textContent = message;
      warningHost.append(e);
    },
    renderSources(list) {
      if (!list || list.length === 0) return;
      sources.classList.remove("hidden");
      const title = el("div", "sources-title");
      title.textContent = "Sources";
      sources.append(title);
      for (const s of list) {
        const chip = el("span", "source-chip");
        chip.textContent = s;
        sources.append(chip);
      }
    },
  };
}

function finalizeThinking(ui, thinking, startedAt, aborted = false) {
  ui.thinkingEl.classList.add("done");
  if (!thinking || !thinking.trim()) {
    // No reasoning trace — it was only a pre-first-token wait indicator.
    ui.thinkingEl.remove();
    return;
  }
  ui.thinkingEl.classList.add("collapsed");
  ui.labelEl.textContent = aborted ? "Thinking (interrupted)" : "Thought";
  ui.timerEl.textContent = `for ${fmtElapsed(startedAt)}`;
}

// Split a streamed response into <think>…</think> reasoning and the answer.
// Tolerates an unclosed <think> while the model is still reasoning.
export function splitThink(raw) {
  const open = raw.indexOf("<think>");
  if (open === -1) return { thinking: "", answer: raw, thinkingOpen: false };
  const afterOpen = open + 7; // "<think>".length
  const close = raw.indexOf("</think>", afterOpen);
  if (close === -1) {
    return { thinking: raw.slice(afterOpen), answer: raw.slice(0, open), thinkingOpen: true };
  }
  const thinking = raw.slice(afterOpen, close);
  const answer = raw.slice(0, open) + raw.slice(close + 8); // "</think>".length
  return { thinking, answer, thinkingOpen: false };
}

function fmtElapsed(startedAt) {
  const secs = (performance.now() - startedAt) / 1000;
  return `${secs.toFixed(1)}s`;
}

function el(tag, className) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  return node;
}
