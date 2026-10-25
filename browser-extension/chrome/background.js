// The bridge between the page and the native host.
//
// Chrome starts a native-messaging host by NAME; the host is whichever client registered that name. The desktop's
// host is tried first, then sapass's (owner, 2026-10-25), so installing one client never repoints the other.
// A request opens its own port: a host process lives only while its port does, and one process per user action is
// simpler and more robust than a long-lived port that has to be re-established after every browser sleep.
const HOSTS = ["chat.simplarchive.autofill.desktop", "chat.simplarchive.autofill.sapass"];

// A submitted login waits here until the user decides on the page that follows. chrome.storage.session is the
// extension's own store: page scripts cannot read it, and it is cleared when the browser closes.
const SAVE_TTL_MS = 5 * 60 * 1000;
let nextId = 1;

async function request(command) {
  const id = nextId++;
  const message = { id, ...command };
  let lastError = null;

  for (const host of HOSTS) {
    try {
      return await once(host, message);
    } catch (error) {
      lastError = error;
    }
  }

  throw new Error(lastError ? lastError.message : "No SimplArchive autofill host is installed.");
}

function once(host, message) {
  return new Promise((resolve, reject) => {
    let port;
    try {
      port = chrome.runtime.connectNative(host);
    } catch (error) {
      reject(error);
      return;
    }

    const timer = setTimeout(() => fail(new Error("The host did not answer.")), 30000);
    function cleanup() {
      clearTimeout(timer);
      try { port.disconnect(); } catch (_) { /* already gone */ }
    }
    function fail(error) {
      cleanup();
      reject(error);
    }

    port.onMessage.addListener((answer) => {
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

    port.postMessage(message);
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
    // The page's keep-alive (ADR 0980): receiving it is the whole point, as it resets the worker's idle timer.
    case "keep-alive": sendResponse({ ok: true }); return false;
    default: sendResponse({ ok: false, error: `Unknown message type '${message.type}'.` }); return false;
  }
});
