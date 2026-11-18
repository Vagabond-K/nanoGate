const NKEYS = [
  "ssid",
  "open",
  "dhcp",
  "ip",
  "subnet",
  "gateway",
  "dns1",
  "dns2",
];
const NLABELS = {
  ssid: "Wi-Fi SSID",
  open: uiText("gate.lbl.wifiSec"),
  dhcp: uiText("gate.lbl.alloc"),
  ip: uiText("gate.lbl.ip"),
  subnet: uiText("gate.lbl.subnet"),
  gateway: uiText("gate.lbl.gw"),
  dns1: uiText("gate.lbl.dns1"),
  dns2: uiText("gate.lbl.dns2"),
  password: uiText("gate.lbl.wifiPw"),
  apPassword: uiText("gate.lbl.apPw"),
};
const PW = /^[\x20-\x7e]{8,63}$/;
let nBase = null;

const ip4 = (t) => {
  const m = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(t);
  return m && m.slice(1).every((x) => +x < 256)
    ? m.slice(1).reduce((a, x) => a * 256 + +x, 0)
    : null;
};
const unicast = (a) => {
  const f = Math.floor(a / 16777216);
  return f > 0 && f !== 127 && f < 224;
};
const z = (v) => (!v || v === "0.0.0.0" ? "" : v);
function nfValues() {
  const f = $("nf").elements,
    v = {
      ssid: f.ssid.value,
      password: f.password.value,
      apPassword: f.apPassword.value,
      open: f.open.checked ? "1" : "0",
      dhcp: f.dhcp.value || "1",
    };
  for (const k of ["ip", "subnet", "gateway", "dns1", "dns2"])
    v[k] = f[k].value.trim();
  return v;
}
function nfChanged(v) {
  if (!nBase) return [];
  const keys = v.dhcp === "0" ? NKEYS : NKEYS.slice(0, 3),
    c = keys.filter((k) => v[k] !== nBase[k]);
  if (v.password && v.open === "0") c.push("password");
  if (v.apPassword) c.push("apPassword");
  return c;
}
const nfDirty = () => nfChanged(nfValues()).length > 0;
function checkNetwork(v) {
  const e = {},
    n = new TextEncoder().encode(v.ssid).length;
  if (n < 1 || n > LIM.ssidMax)
    e.ssid = uiText("gate.net.ssidLen", { n: LIM.ssidMax });
  else if (/[\x00-\x1f\x7f]/.test(v.ssid)) e.ssid = uiText("gate.net.noCtl");
  if (v.open === "0") {
    if (!v.password) {
      if (v.ssid !== nBase.ssid || nBase.open === "1" || !nBase.ssid)
        e.password = uiText("gate.net.enterPw");
    } else if (!PW.test(v.password)) e.password = uiText("gate.net.pwLen");
  }
  if (v.apPassword && !PW.test(v.apPassword))
    e.apPassword = uiText("gate.net.pwLen");
  if (v.dhcp !== "0") return e;
  const a = ip4(v.ip),
    m = ip4(v.subnet),
    h = m == null ? 0 : 4294967295 - m;
  if (a == null) e.ip = uiText("gate.net.ipForm");
  if (m == null) e.subnet = uiText("gate.net.maskForm");
  else if (!m || (h & (h + 1)) !== 0 || h < 3)
    e.subnet = uiText("gate.net.maskRange");
  const host = (x) => x % (h + 1),
    isHost = (x) => unicast(x) && host(x) !== 0 && host(x) !== h;
  if (!e.ip && !e.subnet && !isHost(a)) e.ip = uiText("gate.net.notHost");
  if (v.gateway) {
    const g = ip4(v.gateway);
    if (g == null) e.gateway = uiText("gate.net.gwForm");
    else if (
      !e.ip &&
      !e.subnet &&
      (!isHost(g) || g - host(g) !== a - host(a) || g === a)
    )
      e.gateway = uiText("gate.net.gwSame");
  }
  for (const k of ["dns1", "dns2"])
    if (v[k]) {
      const d = ip4(v[k]);
      if (d == null || !unicast(d)) e[k] = uiText("gate.net.dnsBad");
    }
  return e;
}
function nfFill(s) {
  const f = $("nf").elements,
    dns = (s.dns || []).filter(z);
  nBase = {
    ssid: s.ssid || "",
    open: s.open ? "1" : "0",
    dhcp: s.dhcp === false ? "0" : "1",
    ip: z(s.ip),
    subnet: z(s.subnet),
    gateway: z(s.gateway),
    dns1: dns[0] || "",
    dns2: dns[1] || "",
  };
  f.ssid.value = nBase.ssid;
  f.open.checked = !!s.open;
  f.dhcp.value = nBase.dhcp;
  for (const k of NKEYS.slice(3)) f[k].value = nBase[k];
  f.password.value = "";
  f.apPassword.value = "";
  $("apBox").hidden = !s.hasAp;
  nfSync();
}
function nfSync() {
  const f = $("nf").elements,
    v = nfValues(),
    setup = !!st && st.mode === "setup";
  for (const k of NKEYS.slice(3)) f[k].disabled = v.dhcp !== "0";
  f.password.disabled = v.open === "1";
  const errs = nBase ? checkNetwork(v) : {},
    changed = nfChanged(v),
    bad = Object.keys(errs).length;
  showErrors($("nf"), errs);
  for (const k of NKEYS.concat("password", "apPassword")) {
    const i = f[k];
    (i.tagName ? i : i[0])
      .closest(".field,.check")
      .classList.toggle("chg", changed.includes(k));
  }
  $("pwHint").textContent =
    v.open === "1"
      ? uiText("gate.net.openHint")
      : nBase && nBase.ssid && v.ssid === nBase.ssid && nBase.open === "0"
        ? uiText("gate.net.keepHint")
        : uiText("gate.net.pwLenHint");
  $("nfSave").disabled = !nBase || bad > 0 || (!changed.length && !setup);
  $("nfUndo").disabled = !changed.length;
  $("nfState").textContent = !nBase
    ? uiText("gate.net.loading")
    : bad
      ? uiText("form.invalid")
      : changed.length
        ? uiText("form.pending", { n: changed.length })
        : setup
          ? uiText("gate.net.setupState")
          : uiText("gate.net.clean");
  $("t-network").classList.toggle("dot", changed.length > 0);
}
$("nf").addEventListener("input", nfSync);
$("nf").addEventListener("change", nfSync);
$("nfUndo").onclick = () => {
  if (sys) nfFill(sys);
};
$("nf").onsubmit = async (e) => {
  e.preventDefault();
  const v = nfValues(),
    setup = !!st && st.mode === "setup";
  if (!nBase || Object.keys(checkNetwork(v)).length)
    return toast(uiText("form.invalid"), "bad");
  const changed = nfChanged(v);
  if (!changed.length && !setup) return;
  const show = (k, x) =>
    k === "open"
      ? x === "1"
        ? uiText("gate.sys.open")
        : uiText("gate.sys.secured")
      : k === "dhcp"
        ? x === "1"
          ? "DHCP"
          : uiText("gate.sys.staticIp")
        : x || uiText("gate.net.none");
  const items = changed.map((k) =>
    k.endsWith("assword")
      ? uiText("gate.net.changedPw", { label: NLABELS[k] })
      : NLABELS[k] + ": " + show(k, nBase[k]) + " → " + show(k, v[k]),
  );
  if (setup) items.push(uiText("gate.net.joinAp", { ssid: v.ssid }));
  const next =
      v.dhcp === "0"
        ? "http://" + v.ip + (mgmt === 80 ? "" : ":" + mgmt) + "/"
        : null,
    moved = !!next && v.ip !== location.hostname;
  let note =
    setup || changed.includes("ssid") || changed.includes("password")
      ? uiText("gate.net.wrongNote")
      : "";
  if (setup)
    note += uiText("gate.net.setupNote", {
      ssid: v.ssid,
      how: next ? uiText("gate.net.belowAddr") : uiText("gate.net.dhcpAddr"),
    });
  else if (moved) note += uiText("gate.net.movedNote", { ip: v.ip });
  else if (v.dhcp === "1" && nBase.dhcp === "0")
    note += uiText("gate.net.dhcpNote");
  if (
    !(await ask({
      title: uiText("gate.net.saveTtl"),
      items,
      text: uiText("gate.net.saveText") + note,
      ok: uiText("gate.net.saveReboot"),
      danger: true,
    }))
  )
    return;
  const body = new URLSearchParams({
    ssid: v.ssid,
    open: v.open,
    dhcp: v.dhcp,
  });
  if (v.open === "0" && v.password) body.set("password", v.password);
  if (v.apPassword) body.set("apPassword", v.apPassword);
  if (v.dhcp === "0")
    for (const k of NKEYS.slice(3)) if (v[k]) body.set(k, v[k]);
  try {
    await post("/api/network", body.toString());
    startReboot(
      uiText("gate.net.savedReboot"),
      note,
      setup || moved ? next : null,
    );
  } catch (err) {
    toast(uiText("gate.ui.saveFail", { m: err.message }), "bad");
  }
};
