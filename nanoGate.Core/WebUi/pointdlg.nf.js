const BO = ["ABCD", "CDAB", "BADC", "DCBA"];
let editing = -1;

function checkPoint(p, index, list) {
  const e = {};
  const tok = (i, n) => {
    const v = p[i],
      k = FIELDS[i];
    if (!v) e[k] = uiText("gate.ui.required");
    else if (v.length > n || !TOKEN.test(v))
      e[k] = uiText("gate.ui.tokenLen", { n: n });
  };
  tok(0, LIM.idMax);
  const taVis = addrShow("t").includes("a");
  if (nameOn()) {
    if (!p[1]) e.name = uiText("gate.ui.required");
    else if (p[1].length > LIM.nameMax || /[\x00-\x1f\x7f]/.test(p[1]))
      e.name = uiText("gate.pd.nameCtl", { n: LIM.nameMax });
  }
  for (const i of [2, 3, 4, 7, 8, 9]) tok(i, LIM.tokMax);
  // 숫자 장치 ID 범위는 각 쪽에 대해 features.addr[side].d = {min, max}에서 옵니다 (예: Modbus 유닛 1~247).
  for (const [i, pre] of [
    [3, "s"],
    [8, "t"],
  ]) {
    const cfg = addrCfg(pre);
    if (cfg && cfg.d && !e[FIELDS[i]] && !isInt(p[i], cfg.d.min, cfg.d.max))
      e[FIELDS[i]] = uiText("gate.pd.numRange", { a: cfg.d.min, b: cfg.d.max });
  }
  tok(5, LIM.addrMax);
  if (taVis) tok(10, LIM.addrMax);
  // 숫자 주소는 각 쪽에 대해 features.addr[side].a = {min, max}에서 옵니다 (예: Modbus 0~65535, 0기준 원시 인덱스).
  for (const [i, pre] of [
    [5, "s"],
    [10, "t"],
  ]) {
    const cfg = addrCfg(pre);
    if (cfg && cfg.a && !e[FIELDS[i]] && !isInt(p[i], cfg.a.min, cfg.a.max))
      e[FIELDS[i]] = uiText("gate.pd.numRange", { a: cfg.a.min, b: cfg.a.max });
  }
  for (const i of [6, 11]) {
    const tt = String(p[i]).split(":");
    if (
      !TYPES.includes(tt[0]) ||
      tt.length > 2 ||
      (tt.length > 1 && (i !== 6 || !BO.includes(tt[1]) || !is32Type(tt[0])))
    )
      e[FIELDS[i]] = uiText("gate.pd.badType");
  }
  if (!ACCESS[p[12]]) e.acc = uiText("gate.pd.pickAcc");
  else if (accessList() && !accessList().includes(p[12]))
    e.acc = uiText("gate.pd.accUnsup", { m: ACCESS[p[12]] });
  for (const i of [13, 14, 15])
    if (!isInt(p[i], I32[0], I32[1])) e[FIELDS[i]] = uiText("gate.pd.i32");
  if (!e.den && +p[14] === 0) e.den = uiText("gate.pd.nonZero");
  list.forEach((q, j) => {
    if (j === index) return;
    if (q[0] === p[0] && !e.id) e.id = uiText("gate.pd.dupId");
    if (taVis && target(q) === target(p) && !e.ta)
      e.ta = uiText("gate.pd.sameTgt", { id: q[0] });
  });
  return e;
}

const MODBUS_SPACES = [
  "coil",
  "discrete-input",
  "input-register",
  "holding-register",
];
// 시리얼 소스는 모든 게이트웨이의 공통 입력이므로 Modbus 표만 코어에 남습니다.
// 대상 프로토콜의 영역 표는 각 게이트웨이가 POINT_SPACE_* 레지스트리로 등록합니다
// (예: WebUi\pointspaces.nf.js). 프로파일 capabilities.addr.spaces가 있으면 그것이 우선합니다.
// 프로토콜 영역 이름은 번역하지 않습니다. 표시는 표준 영어 용어를 쓰고, 저장 값은 소글자-하이픈 형태를 유지합니다.
const SPACE_LABELS = {
  coil: "Coil",
  "discrete-input": "Discrete Input",
  "input-register": "Input Register",
  "holding-register": "Holding Register",
};
const spaceLabel = (s) => (window.POINT_SPACE_LABELS || {})[s] || SPACE_LABELS[s] || s;
const SPACE_GROUPS = [["Modbus", MODBUS_SPACES]];
const extGroups = () => Object.values(window.POINT_SPACE_PROTOCOLS || {});
const PROTO_SPACES = {
  "modbus-rtu": ["Modbus", MODBUS_SPACES],
  "modbus-ascii": ["Modbus", MODBUS_SPACES],
  "modbus-tcp": ["Modbus", MODBUS_SPACES],
};
const protoSpaces = (p) => PROTO_SPACES[p] || (window.POINT_SPACE_PROTOCOLS || {})[p];
function fillSpaces(sel, groups) {
  const cur = sel.value;
  sel.textContent = "";
  for (const [g, opts] of groups) {
    if (g) {
      const og = el("optgroup");
      og.label = g;
      for (const o of opts) og.append(new Option(spaceLabel(o), o));
      sel.append(og);
    } else for (const o of opts) sel.append(new Option(spaceLabel(o), o));
  }
  if (cur && ![...sel.options].some((o) => o.value === cur))
    sel.append(new Option(cur, cur));
  sel.value = cur;
}
function syncSpaces(pre) {
  const e = $("pf").elements,
    sel = e[pre + "s"];
  const cfg = addrCfg(pre);
  if (cfg && cfg.spaces) {
    fillSpaces(sel, [[null, cfg.spaces]]);
    return;
  }
  const m = protoSpaces((e[pre + "p"].value || "").toLowerCase());
  fillSpaces(sel, m ? [m] : SPACE_GROUPS.concat(extGroups()));
}
function applyAddr(pre) {
  const show = addrShow(pre),
    cfg = addrCfg(pre);
  $("pd" + (pre === "s" ? "Src" : "Tgt")).hidden = !show.length;
  $("pdAddr").classList.toggle("one", $("pdSrc").hidden || $("pdTgt").hidden);
  for (const k of ["p", "d", "s", "a", "t"])
    $("pf").elements[pre + k].closest(".field").hidden = !show.includes(k);
  const di = $("pf").elements[pre + "d"];
  if (cfg && cfg.d) {
    di.type = "number";
    di.inputMode = "numeric";
    di.min = cfg.d.min;
    di.max = cfg.d.max;
  } else di.type = "text";
  const dl = di.closest(".field").firstElementChild;
  if (dl && dl.tagName === "SPAN")
    dl.textContent = cfg && cfg.d ? "Unit ID" : uiText("gate.lbl.deviceId");
  const ai = $("pf").elements[pre + "a"];
  if (cfg && cfg.a) {
    ai.type = "number";
    ai.inputMode = "numeric";
    ai.min = cfg.a.min;
    ai.max = cfg.a.max;
  } else ai.type = "text";
}
// 폴링할 수 없는 프로파일(예: MQTT)은 features.access=["RO","RW"]로 설정해 지원하지 않는 접근 모드를 비활성화 표시합니다.
function applyAccess() {
  const list = accessList();
  for (const o of $("pf").elements.acc.options)
    o.disabled = !!(list && !list.includes(o.value));
}
function buildAddress(id, pre) {
  const box = $(id);
  for (const [k, lab, mk, list] of [
    ["p", uiText("gate.pd.protocol"), "tok", "dlProto"],
    ["d", uiText("gate.lbl.deviceId"), "tok"],
    ["s", uiText("gate.pd.space"), "tok"],
    ["a", uiText("gate.pd.address"), "addr"],
  ]) {
    const l = el("label", "field");
    let i;
    if (k === "s") {
      i = el("select");
      fillSpaces(i, SPACE_GROUPS.concat(extGroups()));
    } else {
      i = el("input");
      i.dataset.max = mk;
      i.autocomplete = "off";
      if (list) i.setAttribute("list", list);
    }
    i.name = pre + k;
    l.append(el("span", "", lab), i);
    box.append(l);
  }
  const l = el("label", "field"),
    s = el("select");
  s.name = pre + "t";
  for (const t of TYPES) s.append(new Option(t));
  l.append(el("span", "", uiText("gate.pd.type")), s);
  box.append(l);
}
buildAddress("pdSrc", "s");
buildAddress("pdTgt", "t");

// 32-bit byte-order select; only shown when features.byteOrder=true and the type is 32-bit.
const is32Type = (t) => t === "Int32" || t === "UInt32" || t === "Float32";
const boOn = () => feat("byteOrder", false);
const boLab = el("label", "field"),
  boSel = el("select");
boLab.id = "pboBox";
boSel.name = "sbo";
for (const o of BO) boSel.append(new Option(o));
boLab.append(el("span", "", uiText("gate.pd.byteOrder")), boSel);
$("pdSrc").append(boLab);
function syncBo() {
  const e = $("pf").elements;
  $("pboBox").hidden = !(boOn() && is32Type(e.st.value));
}
$("pf").addEventListener("change", syncBo);

const bump = (v) => (/^\d+$/.test(v) ? String(+v + 1) : v);
function freeTarget(p) {
  for (
    let n = 0;
    n < 1000 &&
    /^\d+$/.test(p[10]) &&
    points.some((q) => target(q) === target(p));
    n++
  )
    p[10] = bump(p[10]);
}
function openPoint(i, copy) {
  let p;
  if (i >= 0) {
    p = points[i].slice();
    if (copy) {
      let id = p[0].slice(0, 19) + "-copy",
        n = 2;
      while (points.some((q) => q[0] === id))
        id = p[0].slice(0, 19) + "-" + n++;
      p[0] = id;
      p[5] = bump(p[5]);
      freeTarget(p);
    }
  } else if (points.length) {
    p = points[points.length - 1].slice();
    p[0] = "";
    p[1] = "";
    p[5] = bump(p[5]);
    p[10] = bump(p[10]);
    freeTarget(p);
  } else {
    p = profilePoint();
  }
  editing = copy ? -1 : i;
  $("pdTitle").textContent =
    i < 0
      ? uiText("gate.pd.new")
      : copy
        ? uiText("gate.pd.copy")
        : uiText("gate.pd.editTtl", { id: points[i][0] });
  const f = $("pf");
  FIELDS.forEach((k, n) => {
    const c = f.elements[k];
    if (
      (k === "ss" || k === "ts") &&
      c.tagName === "SELECT" &&
      p[n] &&
      !c.querySelector('option[value="' + p[n] + '"]')
    )
      c.append(new Option(p[n], p[n]));
    c.value = p[n];
  });
  applyAddr("s");
  applyAddr("t");
  applyAccess();
  $("pf").elements.name.closest(".field").hidden = !nameOn();
  $("pdScale").hidden = !scaleOn();
  syncSpaces("s");
  syncSpaces("t");
  const bt = String(p[6]).split(":");
  f.elements.st.value = bt[0];
  f.elements.sbo.value = bt[1] && BO.includes(bt[1]) ? bt[1] : "ABCD";
  syncBo();
  delete f.dataset.tried;
  showErrors(f, {});
  formula();
  $("pd").showModal();
  f.elements[i >= 0 && !copy && nameOn() ? "name" : "id"].focus();
}
const dialogPoint = () => {
  const e = $("pf").elements;
  const p = normalize(FIELDS.map((k) => e[k].value));
  if (!nameOn()) p[1] = p[0];
  if (boOn() && is32Type(p[6]) && e.sbo.value !== "ABCD")
    p[6] = p[6] + ":" + e.sbo.value;
  return p;
};
function formula() {
  const p = dialogPoint(),
    n = +p[13],
    d = +p[14],
    o = +p[15];
  if (![13, 14, 15].every((i) => isInt(p[i], I32[0], I32[1])) || !d) {
    $("pdFormula").textContent = uiText("gate.pd.formulaInfo");
    return;
  }
  $("pdFormula").textContent = uiText("gate.pd.formula", { n: n, d: d, o: o });
}
$("pf").addEventListener("input", (ev) => {
  const f = $("pf"),
    errs = checkPoint(dialogPoint(), editing, points);
  if (ev && ev.target && /^(s|t)p$/.test(ev.target.name))
    syncSpaces(ev.target.name[0]);
  formula();
  if (!f.dataset.tried)
    for (const k of Object.keys(errs)) if (!f.elements[k].value) delete errs[k];
  showErrors(f, errs);
});
$("pdCancel").onclick = () => $("pd").close();
$("pf").onsubmit = (e) => {
  e.preventDefault();
  const f = $("pf"),
    p = dialogPoint(),
    errs = checkPoint(p, editing, points);
  f.dataset.tried = "1";
  showErrors(f, errs);
  if (Object.keys(errs).length) return;
  const next = clone(points);
  if (editing < 0) next.push(p);
  else next[editing] = p;
  if (next.length > MAX_POINTS)
    return toast(uiText("gate.pd.maxPts", { n: MAX_POINTS }), "bad");
  if (serialize(next).length > MAX_BYTES)
    return toast(uiText("gate.pd.maxBytes", { n: MAX_BYTES }), "bad");
  points = next;
  $("pd").close();
  renderPoints();
  toast(uiText("gate.pd.applied", { id: p[0] }));
};
