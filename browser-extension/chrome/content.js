// The page's half: finds login fields, offers the matching entries, fills what the user picks, and offers to keep a
// submitted login.
//
// It never reads anything but the fields it fills, and it sends only the SITE to the host — the names it lists and
// the secret it fills never pass through the page's storage.
(() => {
  const SITE = location.hostname.replace(/^www\./, "").toLowerCase();
  let menu = null;
  let chip = null;
  let target = null;

  const ask = (message) => new Promise((resolve, reject) => {
    chrome.runtime.sendMessage(message, (response) => {
      if (!response) {
        reject(new Error(chrome.runtime.lastError?.message || "The extension did not answer."));
      } else if (response.ok) {
        resolve(response.answer);
      } else {
        reject(new Error(response.error));
      }
    });
  });

  // React and friends listen to the native setter's input event, not to a plain assignment.
  function setValue(input, value) {
    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value").set;
    setter.call(input, value);
    input.dispatchEvent(new Event("input", { bubbles: true }));
    input.dispatchEvent(new Event("change", { bubbles: true }));
  }

  function clear(element) {
    if (element && element.parentNode) {
      element.parentNode.removeChild(element);
    }
  }

  function position(element, input) {
    const rect = input.getBoundingClientRect();
    element.style.top = `${rect.bottom + window.scrollY + 4}px`;
    element.style.left = `${rect.left + window.scrollX}px`;
  }

  function showChip(input, label, onClick) {
    closeAll();
    chip = document.createElement("button");
    chip.className = "sa-chip";
    chip.type = "button";
    chip.textContent = label;
    chip.addEventListener("mousedown", (event) => {
      event.preventDefault();
      onClick();
    });
    document.body.appendChild(chip);
    position(chip, input);
  }

  function closeAll() {
    clear(chip);
    clear(menu);
    chip = null;
    menu = null;
  }

  function usernameField(password) {
    const form = password.form || document;
    return form.querySelector('input[autocomplete="username"], input[type="email"], input[type="text"]');
  }

  function fill(credentials) {
    if (!target) {
      return;
    }
    const username = usernameField(target);
    if (username && credentials.userName) {
      setValue(username, credentials.userName);
    }
    setValue(target, credentials.password);
    closeAll();
  }

  function showMenu(input, entries) {
    closeAll();
    menu = document.createElement("div");
    menu.className = "sa-menu";

    for (const entry of entries) {
      const item = document.createElement("button");
      item.type = "button";
      item.textContent = entry.name;
      const sub = document.createElement("span");
      sub.className = "sa-sub";
      sub.textContent = [entry.userName, entry.path].filter(Boolean).join(" · ");
      item.appendChild(sub);
      item.addEventListener("mousedown", async (event) => {
        event.preventDefault();
        try {
          fill(await ask({ type: "host", command: { command: "open", entryId: entry.id } }));
        } catch (error) {
          note(error.message);
        }
      });
      menu.appendChild(item);
    }

    const generate = document.createElement("button");
    generate.type = "button";
    generate.textContent = "Generate a strong password";
    generate.addEventListener("mousedown", async (event) => {
      event.preventDefault();
      try {
        const answer = await ask({ type: "host", command: { command: "generate", length: 24 } });
        fill({ password: answer.password, userName: null });
      } catch (error) {
        note(error.message);
      }
    });
    menu.appendChild(generate);

    document.body.appendChild(menu);
    position(menu, input);
  }

  function note(message) {
    closeAll();
    menu = document.createElement("div");
    menu.className = "sa-menu";
    const line = document.createElement("div");
    line.className = "sa-note";
    line.textContent = message;
    menu.appendChild(line);
    document.body.appendChild(menu);
  }

  async function offer(input) {
    target = input;
    try {
      const answer = await ask({ type: "host", command: { command: "match", site: SITE } });
      if (!answer.entries || answer.entries.length === 0) {
        showChip(input, "SimplArchive: no match", () => showMenu(input, []));
        return;
      }
      showChip(input, `SimplArchive (${answer.entries.length})`, () => showMenu(input, answer.entries));
    } catch (error) {
      showChip(input, "SimplArchive: unavailable", () => note(error.message));
    }
  }

  function offerSave(save) {
    const bridge = document.createElement("div");
    bridge.className = "sa-menu";
    const head = document.createElement("div");
    head.className = "sa-note";
    head.textContent = "Keep this login in SimplArchive?";
    bridge.appendChild(head);

    const keep = document.createElement("button");
    keep.type = "button";
    keep.textContent = "Save to My Passwords";
    keep.addEventListener("mousedown", async () => {
      try {
        await ask({ type: "host", command: { command: "save", site: save.site, userName: save.userName, password: save.password } });
      } finally {
        await ask({ type: "forget-save", site: save.site });
        clear(bridge);
      }
    });
    bridge.appendChild(keep);

    const no = document.createElement("button");
    no.type = "button";
    no.textContent = "Not now";
    no.addEventListener("mousedown", async () => {
      await ask({ type: "forget-save", site: save.site });
      clear(bridge);
    });
    bridge.appendChild(no);

    bridge.style.top = "12px";
    bridge.style.right = "12px";
    bridge.style.left = "auto";
    document.body.appendChild(bridge);
  }

  document.addEventListener("focusin", (event) => {
    const field = event.target;
    if (field instanceof HTMLInputElement && field.type === "password") {
      offer(field);
    }
  }, true);

  document.addEventListener("submit", (event) => {
    const form = event.target;
    if (!(form instanceof HTMLFormElement)) {
      return;
    }
    const password = form.querySelector('input[type="password"]');
    const username = form.querySelector('input[autocomplete="username"], input[type="email"], input[type="text"]');
    if (password && password.value) {
      ask({
        type: "remember-save",
        save: { site: SITE, userName: username ? username.value : "", password: password.value },
      });
    }
  }, true);

  // On the page after a submit: if a login was just typed here, offer to keep it.
  if (!document.querySelector('input[type="password"]')) {
    ask({ type: "pending-save", site: SITE })
      .then((save) => { if (save) { offerSave(save); } })
      .catch(() => { /* the host or the session is missing: no prompt, no error */ });
  }
})();
