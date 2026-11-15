// Minimal DOM shim: enough to execute the prototype's inline script and prove
// it renders without throwing. Not a browser — no layout, no CSS.
const fs = require("fs");
const path = require("path");

const counts = {};
function bump(k) { counts[k] = (counts[k] || 0) + 1; }

function El(tag) {
  bump(tag);
  const e = {
    tagName: tag,
    children: [],
    style: {},
    parentNode: null,
    _cls: "",
    _text: "",
    _html: "",
    _attrs: {},
    addEventListener() {},
    removeEventListener() {},
    setAttribute(k, v) { this._attrs[k] = v; },
    getAttribute(k) { return this._attrs[k]; },
    appendChild(c) {
      // A real DOM moves a fragment's children in, it does not nest the fragment.
      if (c.tagName === "#fragment") {
        for (const child of c.children) { this.children.push(child); child.parentNode = this; }
        c.children.length = 0;
        return c;
      }
      this.children.push(c); c.parentNode = this; return c;
    },
    removeChild(c) {
      const i = this.children.indexOf(c);
      if (i >= 0) this.children.splice(i, 1);
      return c;
    },
    getBoundingClientRect() { return { width: 1200, height: 420, left: 0, top: 0, right: 1200, bottom: 420 }; },
    get firstChild() { return this.children.length ? this.children[0] : null; },
    querySelector() { return null; },
    querySelectorAll() { return []; },
  };
  Object.defineProperty(e, "className", {
    get() { return e._cls; },
    set(v) { e._cls = v; },
  });
  Object.defineProperty(e, "textContent", {
    get() { return e._text; },
    set(v) { e._text = String(v); },
  });
  Object.defineProperty(e, "innerHTML", {
    get() { return e._html; },
    set(v) { e._html = String(v); if (v === "") e.children.length = 0; },
  });
  return e;
}

const byId = {};
["driveChip", "pathBox", "selName", "sTotal", "sUsed", "sFree", "sScanned", "scanMeta",
 "uBar", "treeBody", "extBody", "extStrip", "crumbs", "map", "details", "tip", "scanBtn"]
  .forEach((id) => { byId[id] = El("#" + id); });

global.window = {
  innerWidth: 1400,
  innerHeight: 880,
  addEventListener() {},
  DISK_DATA: JSON.parse(fs.readFileSync(path.join(__dirname, "data.json"), "utf8")),
};

global.document = {
  getElementById: (id) => byId[id] || El("#" + id),
  createElement: (t) => El(t),
  createElementNS: (ns, t) => El(t),
  createDocumentFragment: () => El("#fragment"),
  addEventListener() {},
};

global.requestAnimationFrame = (cb) => { cb(); return 1; };
global.setTimeout = (cb) => { return 1; };
global.clearTimeout = () => {};

const html = fs.readFileSync(path.join(__dirname, "prototype.html"), "utf8");
const m = html.match(/<script>\s*([\s\S]*?)\s*<\/script>/);
if (!m) { console.error("inline script not found"); process.exit(1); }

try {
  (0, eval)(m[1]);
} catch (err) {
  console.error("RUNTIME ERROR:", err && err.stack ? err.stack : err);
  process.exit(1);
}

const tree = byId.treeBody.children.length;
const ext = byId.extBody.children.length;
const map = byId.map.children.length;
const strip = byId.extStrip.children.length;
const details = byId.details.children.length;

console.log("tree rows   :", tree);
console.log("ext rows    :", ext);
console.log("strip segs  :", strip);
console.log("map nodes   :", map);
console.log("details els :", details);
console.log("rects drawn :", counts["rect"] || 0);
console.log("labels      :", counts["text"] || 0);

const ok = tree > 0 && ext > 0 && map > 50 && details > 0;
console.log(ok ? "RENDER OK" : "RENDER INCOMPLETE");
process.exit(ok ? 0 : 1);
