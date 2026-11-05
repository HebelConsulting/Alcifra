// The bridge between the page and the native host, and the bookmark sync over the same host (bookmarks.js).
//
// Chrome starts a native-messaging host by NAME; the host is whichever client registered that name. The desktop's
// host is tried first, then sapass's (owner, 2026-10-25), so installing one client never repoints the other.
// A request opens its own port: a host process lives only while its port does, and one process per user action is
// simpler and more robust than a long-lived port that has to be re-established after every browser sleep.
const HOSTS = ["com.alcifra.autofill.desktop", "com.alcifra.autofill.sapass"];

// A submitted login waits here until the user decides on the page that follows. chrome.storage.session is the
// extension's own store: page scripts cannot read it, and it is cleared when the browser closes.
const SAVE_TTL_MS = 5 * 60 * 1000;
// A frame in either direction stays under the protocol's 1 MB; what is larger travels in base64 chunks, each carrying
// the request's id. Only a bookmark file ever is.
const WHOLE_FRAME_BYTES = 900 * 1024;
const CHUNK_BYTES = 512 * 1024;
let nextId = 1;

async function request(command, timeoutMs = 30000) {
  const id = nextId++;
  const message = { id, ...command };
  let lastError = null;

  for (const host of HOSTS) {
    try {
      return await once(host, message, timeoutMs);
    } catch (error) {
      lastError = error;
    }
  }

  throw new Error(lastError ? lastError.message : "No Alcifra autofill host is installed.");
}

function toBase64(bytes) {
  let binary = "";
  for (let at = 0; at < bytes.length; at += 8192) {
    binary += String.fromCharCode.apply(null, bytes.subarray(at, at + 8192));
  }
  return btoa(binary);
}

function fromChunks(chunks) {
  const parts = chunks.map((chunk) => Uint8Array.from(atob(chunk), (c) => c.charCodeAt(0)));
  const whole = new Uint8Array(parts.reduce((sum, part) => sum + part.length, 0));
  let at = 0;
  for (const part of parts) {
    whole.set(part, at);
    at += part.length;
  }
  return JSON.parse(new TextDecoder().decode(whole));
}

function send(port, message) {
  const body = new TextEncoder().encode(JSON.stringify(message));
  if (body.length <= WHOLE_FRAME_BYTES) {
    port.postMessage(message);
    return;
  }
  for (let at = 0; at < body.length; at += CHUNK_BYTES) {
    const end = Math.min(at + CHUNK_BYTES, body.length);
    port.postMessage({ id: message.id, chunk: toBase64(body.subarray(at, end)), last: end === body.length });
  }
}

function once(host, message, timeoutMs) {
  return new Promise((resolve, reject) => {
    let port;
    try {
      port = chrome.runtime.connectNative(host);
    } catch (error) {
      reject(error);
      return;
    }

    const timer = setTimeout(() => fail(new Error("The host did not answer.")), timeoutMs);
    const chunks = [];
    function cleanup() {
      clearTimeout(timer);
      try { port.disconnect(); } catch (_) { /* already gone */ }
    }
    function fail(error) {
      cleanup();
      reject(error);
    }

    port.onMessage.addListener((frame) => {
      let answer = frame;
      if (frame && frame.id === message.id && frame.chunk !== undefined) {
        chunks.push(frame.chunk);
        if (!frame.last) {
          return;
        }
        answer = fromChunks(chunks);
      }
      if (answer && answer.id === message.id) {
        cleanup();
        if (answer.ok) {
          resolve(answer);
        } else {
          reject(new Error(answer.error || "The host refused the request."));
        }
      }
    });
    port.onDisconnect.addListener(() => {
      const error = chrome.runtime.lastError;
      fail(new Error(error ? error.message : "The host closed the port."));
    });

    send(port, message);
  });
}

async function remember(save) {
  await chrome.storage.session.set({ ["save:" + save.site]: { ...save, at: Date.now() } });
}

async function pending(site) {
  const key = "save:" + site;
  const stored = await chrome.storage.session.get(key);
  const save = stored[key];
  if (!save) {
    return null;
  }
  if (Date.now() - save.at > SAVE_TTL_MS) {
    await chrome.storage.session.remove(key);
    return null;
  }
  return save;
}

async function forget(site) {
  await chrome.storage.session.remove("save:" + site);
}

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  const answer = (promise) => {
    promise
      .then((value) => sendResponse({ ok: true, answer: value }))
      .catch((error) => sendResponse({ ok: false, error: error.message }));
    return true; // asynchronous
  };

  switch (message.type) {
    case "host": return answer(request(message.command));
    case "remember-save": return answer(remember(message.save));
    case "pending-save": return answer(pending(message.site));
    case "forget-save": return answer(forget(message.site));
    case "sync-bookmarks": return answer(syncBookmarks());
    // The page's keep-alive (ADR 0980): receiving it is the whole point, as it resets the worker's idle timer.
    case "keep-alive": sendResponse({ ok: true }); return false;
    default: sendResponse({ ok: false, error: `Unknown message type '${message.type}'.` }); return false;
  }
});

importScripts("bookmarks.js");
