const FIELDS = [
  "id",
  "name",
  "sp",
  "sd",
  "ss",
  "sa",
  "st",
  "tp",
  "td",
  "ts",
  "ta",
  "tt",
  "acc",
  "num",
  "den",
  "off",
];
const TYPES = ["Bool", "Int16", "UInt16", "Int32", "UInt32", "Float32"];
const ACCESS = {
  RO: uiText("gate.pt.ro"),
  WO: uiText("gate.pt.wo"),
  RW: uiText("gate.pt.rw"),
};
const TOKEN = /^[A-Za-z0-9_.:\/-]+$/;
const I32 = [-2147483648, 2147483647];

const enc = (v) =>
  Array.from(new TextEncoder().encode(v), (b) => {
    const c = String.fromCharCode(b);
    return TOKEN.test(c)
      ? c
      : "%" + b.toString(16).toUpperCase().padStart(2, "0");
  }).join("");
const serialize = (list) =>
  HEADER +
  "\n" +
  list.map((p) => "point=" + p.map(enc).join("|") + "|\n").join("");
const pointsDirty = () => loaded && serialize(points) !== serialize(saved);
const clone = (list) => list.map((p) => p.slice());
const target = (p) => p.slice(7, 11).join("|");

function parsePoints(text) {
  const lines = text
    .split("\n")
    .map((x) => x.replace(/\r$/, ""))
    .filter((x) => x);
  if (lines.shift() !== HEADER) throw new Error(uiText("gate.pt.badVer"));
  return lines.map((x) => {
    if (!x.startsWith("point=")) throw new Error(uiText("gate.pt.unknown"));
    const a = x.slice(6).split("|");
    if (a.pop() !== "" || a.length !== 16)
      throw new Error(uiText("gate.pt.badItem"));
    return a.map(decodeURIComponent);
  });
}

function normalize(p) {
  for (let i = 0; i < 16; i++) p[i] = String(p[i] == null ? "" : p[i]).trim();
  for (const i of [13, 14, 15])
    if (isInt(p[i], I32[0], I32[1])) p[i] = String(+p[i] || 0);
  return p;
}

function scaleText(p) {
  const n = +p[13],
    d = +p[14],
    o = +p[15];
  if (n === 1 && d === 1 && o === 0) return "—";
  return "× " + (n || 1) + " ÷ " + (d || 1) + " + " + o;
}

function renderPoints() {
  const q = $("pq").value.trim().toLowerCase(),
    node = WebUi.node,
    rows = [];
  $("pq").placeholder = nameOn()
    ? uiText("gate.pt.searchName")
    : uiText("gate.pt.search");
  $("pTable").classList.toggle("no-tgt", !addrShow("t").length);
  $("pTable").classList.toggle("no-scale", !scaleOn());
  $("pScaleNote").hidden = !scaleOn();
  let shown = 0;
  points.forEach((p, i) => {
    if (q && !p.join(" ").toLowerCase().includes(q)) return;
    shown++;
    const sc = scaleText(p),
      old = saved.find((x) => x[0] === p[0]);
    rows.push(
      node(
        "tr",
        {
          key: p[0],
          className: !old || old.join("|") !== p.join("|") ? "chg" : "",
        },
        node(
          "td",
          {},
          node("b", { text: p[0] }),
          nameOn() ? node("div", { className: "muted", text: p[1] }) : null,
        ),
        addrTd(
          addrShow("s"),
          { p: p[2], d: p[3], s: p[4], a: p[5], t: p[6] },
          addrCfg("s"),
        ),
        node("td", { className: "muted c-arrow", text: "→" }),
        addrTd(
          addrShow("t"),
          { p: p[7], d: p[8], s: p[9], a: p[10], t: p[11] },
          addrCfg("t"),
          "c-tgt",
        ),
        node("td", {}, node("span", { className: "tag", text: ACCESS[p[12]] })),
        node("td", {
          className: "mono c-scale" + (sc === "—" ? " muted" : ""),
          text: sc,
        }),
        node(
          "td",
          { className: "acts" },
          [
            [uiText("gate.pt.edit"), () => openPoint(i, false)],
            [uiText("gate.pt.dup"), () => openPoint(i, true)],
            [uiText("gate.pt.del"), () => removePoint(i)],
          ].map((x) =>
            node("button", {
              className: "sm",
              type: "button",
              onclick: x[1],
              text: x[0],
            }),
          ),
        ),
      ),
    );
  });
  WebUi.render($("pRows"), rows);
  const empty = $("pEmpty");
  empty.hidden = shown > 0 || !loaded;
  empty.textContent = points.length
    ? uiText("gate.ui.noSearch")
    : uiText("gate.pt.emptyNone");
  const bytes = serialize(points).length,
    dirty = pointsDirty();
  $("pCount").textContent = points.length + " / " + MAX_POINTS;
  $("pMeter").style.width = (points.length / MAX_POINTS) * 100 + "%";
  $("pMeter").className = points.length >= MAX_POINTS ? "warn" : "ok";
  $("pBytes").textContent = kb(bytes) + " / 32 KB";
  $("pAdd").disabled = !loaded || points.length >= MAX_POINTS;
  $("pSave").disabled = !dirty;
  $("pUndo").disabled = !dirty;
  $("pState").textContent = !loaded
    ? ""
    : dirty
      ? uiText("gate.pt.dirty")
      : uiText("gate.pt.clean");
  $("t-points").classList.toggle(
    "dot",
    dirty || (typeof mapDirty === "function" && mapDirty()),
  );
}
$("pq").addEventListener("input", renderPoints);

async function loadPoints() {
  if (!pointsOn()) return;
  try {
    saved = parsePoints(await api("/api/points", null, 15000));
    points = clone(saved);
    loaded = true;
    $("pErr").hidden = true;
  } catch (e) {
    if (e.unauth) {
      $("pEmpty").textContent = uiText("gate.pt.loginWait");
    } else {
      $("pErr").hidden = false;
      $("pErr").textContent = uiText("gate.pt.loadFail", { m: e.message });
      $("pEmpty").textContent = "";
    }
  }
  renderPoints();
}
function removePoint(i) {
  const p = points.splice(i, 1)[0];
  renderPoints();
  toast(uiText("gate.pt.deleted", { id: p[0] }));
}
$("pAdd").onclick = () => openPoint(-1, false);
$("pUndo").onclick = () => {
  points = clone(saved);
  renderPoints();
};
$("pSave").onclick = async () => {
  const list = clone(points);
  $("pSave").disabled = true;
  $("pState").textContent = uiText("gate.ui.saving");
  try {
    const r = await post(
      "/api/points",
      serialize(list),
      "text/plain; charset=utf-8",
      30000,
    );
    saved = list;
    toast(uiText("gate.pt.saved", { n: r.count }), "ok");
  } catch (e) {
    toast(uiText("gate.ui.saveFail", { m: e.message }), "bad");
  }
  renderPoints();
};

// system.nf.js(백업/복원 화면)가 호출하는 포인트 훅. 포인트 탭이 있는 제품에만
// 이 파일이 번들되므로 /api/points 코드가 포인트 없는 제품 빌드에 남지 않습니다.
const addrOut = (p, i) => ({
  protocol: p[i],
  device: p[i + 1],
  space: p[i + 2],
  address: p[i + 3],
  type: p[i + 4],
});
const addrIn = (a) =>
  a ? [a.protocol, a.device, a.space, a.address, a.type] : ["", "", "", "", ""];
window.POINTS_BACKUP = {
  export: () =>
    saved.map((p) => ({
      id: p[0],
      name: p[1],
      source: addrOut(p, 2),
      target: addrOut(p, 7),
      access: p[12],
      scale: [+p[13], +p[14]],
      offset: +p[15],
    })),
  validate: (data) => {
    if (!Array.isArray(data.points))
      throw new Error(uiText("gate.sys.noPointList"));
    if (data.points.length > MAX_POINTS)
      throw new Error(uiText("gate.sys.tooManyPts", { n: MAX_POINTS }));
    const list = data.points.map((o) => {
      o = o || {};
      const sc = Array.isArray(o.scale) ? o.scale : [];
      return normalize([
        o.id,
        o.name,
        ...addrIn(o.source),
        ...addrIn(o.target),
        o.access,
        sc[0],
        sc[1],
        o.offset,
      ]);
    });
    list.forEach((p, i) => {
      const pe = checkPoint(p, i, list),
        pk = Object.keys(pe);
      if (pk.length)
        throw new Error(
          uiText("gate.sys.pointAt", {
            n: i + 1,
            id: p[0] || uiText("gate.sys.noId"),
            msg: pe[pk[0]],
          }),
        );
    });
    if (serialize(list).length > MAX_BYTES)
      throw new Error(uiText("gate.pd.maxBytes", { n: MAX_BYTES }));
    return list;
  },
  apply: async (list) => {
    await post(
      "/api/points",
      serialize(list),
      "text/plain; charset=utf-8",
      30000,
    );
    saved = clone(list);
    points = clone(list);
    loaded = true;
    $("pErr").hidden = true;
    renderPoints();
  },
};
