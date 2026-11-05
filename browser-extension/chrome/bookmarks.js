// Keeps the browser's bookmarks in step with the user's bookmark files (ADR 1062). Each file is its own folder here.
//
// The host holds nothing between calls and merges nothing it was not handed: what a file last agreed with the server
// (its base) and which browser node is which bookmark (its map) live in this extension's own storage, and each sync
// takes the merged tree home. A browser node's id means nothing outside this profile; the file's id is what travels.
const BOOKMARK_SYNC_EVERY_MINUTES = 15;
const BOOKMARK_SETTLE_MINUTES = 0.5; // how long after a change, so a burst of edits is one save
const BOOKMARK_SYNC_TIMEOUT_MS = 120 * 1000;
const EVERY_ALARM = "bookmarks-every";
const SETTLE_ALARM = "bookmarks-settle";
const STATE_PREFIX = "bookmarks:";

let bookmarkSyncRun = null;
let bookmarkSyncAgain = false;
let applyingBookmarks = false;

// One sync at a time: asking during one runs it once more afterwards, and answers when that is done too.
function syncBookmarks() {
  if (bookmarkSyncRun) {
    bookmarkSyncAgain = true;
    return bookmarkSyncRun;
  }
  bookmarkSyncRun = (async () => {
    try {
      do {
        bookmarkSyncAgain = false;
        await syncEveryFile();
      } while (bookmarkSyncAgain);
    } finally {
      bookmarkSyncRun = null;
    }
  })();
  return bookmarkSyncRun;
}

async function syncEveryFile() {
  const { files } = await request({ command: "bookmarks-list" });
  for (const file of files) {
    try {
      await syncFile(file);
    } catch (error) {
      console.warn(`The bookmark file ${file.id} did not sync: ${error.message}`);
    }
  }

  // A file no longer listed was deleted, or its folder's rights went: its copy here goes with it.
  const stored = await chrome.storage.local.get(null);
  for (const key of Object.keys(stored).filter((k) => k.startsWith(STATE_PREFIX))) {
    if (!files.some((file) => STATE_PREFIX + file.id === key)) {
      await removeFolder(stored[key].folderId);
      await chrome.storage.local.remove(key);
    }
  }
}

async function syncFile(file) {
  const key = STATE_PREFIX + file.id;
  const state = (await chrome.storage.local.get(key))[key] || {};
  const map = state.map || {};
  const held = state.held || [];

  // A folder removed by hand is not an order to empty the file: it comes back from the server, as on a new browser.
  let folder = await folderOf(state.folderId);
  const local = folder ? readLocal(folder.children || [], map, held) : null;
  const answer = await request(
    { command: "bookmarks-sync", fileId: file.id, base: folder ? state.base : null, local },
    BOOKMARK_SYNC_TIMEOUT_MS);

  applyingBookmarks = true;
  try {
    if (!folder) {
      folder = await chrome.bookmarks.create({ parentId: await otherBookmarksId(), title: file.name });
      folder.children = [];
    } else if (folder.title !== file.name) {
      await chrome.bookmarks.update(folder.id, { title: file.name });
    }
    const nowHeld = await applyTree(answer.tree, folder, map);
    await chrome.storage.local.set({ [key]: { folderId: folder.id, base: answer.tree, map: mapOfTree(map, answer.tree), held: nowHeld } });
  } finally {
    applyingBookmarks = false;
  }
}

async function folderOf(id) {
  if (!id) {
    return null;
  }
  try {
    return (await chrome.bookmarks.getSubTree(id))[0];
  } catch (_) {
    return null;
  }
}

async function removeFolder(id) {
  applyingBookmarks = true;
  try {
    if (await folderOf(id)) {
      await chrome.bookmarks.removeTree(id);
    }
  } finally {
    applyingBookmarks = false;
  }
}

async function otherBookmarksId() {
  const roots = (await chrome.bookmarks.getTree())[0].children;
  return (roots.find((root) => root.folderType === "other") || roots[1] || roots[0]).id;
}

function readLocal(children, map, held) {
  const tree = children.map((child) => readNode(child, map));
  const byId = new Map();
  (function index(nodes) {
    for (const node of nodes) {
      if (node.children) {
        byId.set(node.id, node);
        index(node.children);
      }
    }
  })(tree);

  // What this browser refused to hold stays in the file: leaving it out would read as this browser deleting it.
  for (const { parent, node } of held) {
    const list = parent && byId.has(parent) ? byId.get(parent).children : tree;
    list.push(node);
  }
  return tree;
}

function readNode(child, map) {
  map[child.id] = map[child.id] || crypto.randomUUID();
  return child.url
    ? { id: map[child.id], title: child.title, url: child.url }
    : { id: map[child.id], title: child.title, children: (child.children || []).map((c) => readNode(c, map)) };
}

async function applyTree(tree, folder, map) {
  const browserIdOf = new Map(Object.entries(map).map(([browserId, id]) => [id, browserId]));
  const known = new Map();
  (function index(nodes) {
    for (const node of nodes) {
      known.set(node.id, node);
      index(node.children || []);
    }
  })(folder.children || []);

  const placed = new Set();
  const held = [];
  await placeList(tree, folder.id, null, { browserIdOf, known, map, placed, held });

  // After everything has moved, so a removed folder no longer holds what survives it. Only what the sync read goes: a
  // node made since is not in the map, and the next sync picks it up.
  for (const [browserId, node] of known) {
    if (!placed.has(browserId) && map[browserId] !== undefined) {
      try {
        await (node.children ? chrome.bookmarks.removeTree(browserId) : chrome.bookmarks.remove(browserId));
      } catch (_) {
        // Already gone with a removed parent.
      }
    }
  }
  return held;
}

async function placeList(nodes, parentBrowserId, parentId, context) {
  const kids = (await chrome.bookmarks.getChildren(parentBrowserId)).map((kid) => kid.id);
  let index = 0;
  for (const node of nodes) {
    const browserId = await placeNode(node, parentBrowserId, parentId, index, kids, context);
    if (browserId === null) {
      continue;
    }
    index++;
    if (node.children) {
      await placeList(node.children, browserId, node.id, context);
    }
  }
}

async function placeNode(node, parentBrowserId, parentId, index, kids, { browserIdOf, known, map, placed, held }) {
  let browserId = browserIdOf.get(node.id);
  try {
    if (browserId && known.has(browserId)) {
      const current = known.get(browserId);
      if (current.title !== node.title || (node.url && current.url !== node.url)) {
        await chrome.bookmarks.update(browserId, node.url ? { title: node.title, url: node.url } : { title: node.title });
      }
      if (kids[index] !== browserId) {
        await chrome.bookmarks.move(browserId, { parentId: parentBrowserId, index });
        const at = kids.indexOf(browserId);
        if (at >= 0) {
          kids.splice(at, 1);
        }
        kids.splice(index, 0, browserId);
      }
    } else {
      const created = await chrome.bookmarks.create({ parentId: parentBrowserId, index, title: node.title, url: node.url });
      browserId = created.id;
      map[browserId] = node.id;
      kids.splice(index, 0, browserId);
    }
  } catch (error) {
    // An address this browser will not hold (another browser's own scheme): kept in the file, absent here.
    console.warn(`A bookmark could not be placed: ${error.message}`);
    held.push({ parent: parentId, node });
    return null;
  }
  placed.add(browserId);
  return browserId;
}

function mapOfTree(map, tree) {
  const ids = new Set();
  (function index(nodes) {
    for (const node of nodes) {
      ids.add(node.id);
      index(node.children || []);
    }
  })(tree);
  return Object.fromEntries(Object.entries(map).filter(([, id]) => ids.has(id)));
}

function bookmarksChanged() {
  if (!applyingBookmarks) {
    chrome.alarms.create(SETTLE_ALARM, { delayInMinutes: BOOKMARK_SETTLE_MINUTES });
  }
}

function syncQuietly() {
  // No host installed, or nobody signed in: the next alarm asks again.
  syncBookmarks().catch((error) => console.warn(`Bookmarks did not sync: ${error.message}`));
}

async function startBookmarkSync() {
  if (!(await chrome.alarms.get(EVERY_ALARM))) {
    await chrome.alarms.create(EVERY_ALARM, { periodInMinutes: BOOKMARK_SYNC_EVERY_MINUTES });
  }
  syncQuietly();
}

chrome.runtime.onStartup.addListener(startBookmarkSync);
chrome.runtime.onInstalled.addListener(startBookmarkSync);
chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === EVERY_ALARM || alarm.name === SETTLE_ALARM) {
    syncQuietly();
  }
});
chrome.bookmarks.onCreated.addListener(bookmarksChanged);
chrome.bookmarks.onChanged.addListener(bookmarksChanged);
chrome.bookmarks.onMoved.addListener(bookmarksChanged);
chrome.bookmarks.onRemoved.addListener(bookmarksChanged);
chrome.bookmarks.onChildrenReordered.addListener(bookmarksChanged);
