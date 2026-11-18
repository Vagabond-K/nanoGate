let sysBusy = false;
async function loadSystem() {
  if (sysBusy) return;
  sysBusy = true;
  $("sRefresh").disabled = true;
  for (const id of ["sDev", "sNet"])
    if (!$(id).childElementCount)
      $(id).append(el("dd", "muted", uiText("gate.sys.loadingShort")));
  try {
    const s = await api("/api/system"),
      setup = s.mode === "setup";
    sys = s;
    loadAuth();
    fillDl("sDev", [
      [uiText("gate.lbl.firmware"), s.firmware],
      ["nanoFramework", s.nanoFramework],
      [uiText("gate.lbl.targetFw"), s.target],
      [uiText("gate.lbl.platform"), s.platform],
      ["OEM", s.oem],
      [uiText("gate.lbl.freeMem"), kb(s.freeMemory)],
      [uiText("gate.lbl.uptime"), dur(s.uptime)],
    ]);
    fillDl("sNet", [
      [
        uiText("gate.lbl.mode"),
        setup ? uiText("gate.sys.modeSetup") : uiText("gate.sys.modeNormal"),
      ],
      ["Wi-Fi SSID", s.ssid],
      [
        uiText("gate.lbl.wifiSec"),
        s.ssid
          ? s.open
            ? uiText("gate.sys.open")
            : uiText("gate.sys.secured")
          : "",
      ],
      [uiText("gate.lbl.ip"), s.ip],
      [uiText("gate.lbl.subnet"), s.subnet],
      [uiText("gate.lbl.gw"), s.gateway],
      ["DNS", (s.dns || []).join(", ")],
      [
        uiText("gate.lbl.alloc"),
        s.dhcp == null ? "" : s.dhcp ? "DHCP" : uiText("gate.sys.staticIp"),
      ],
      [uiText("gate.lbl.mac"), s.mac],
      [
        uiText("gate.lbl.ap"),
        s.hasAp
          ? s.apSsid +
            (setup
              ? " · " + s.apIp + " " + uiText("gate.sys.apOn")
              : " " + uiText("gate.sys.apOff"))
          : uiText("gate.sys.notSupported"),
      ],
      [uiText("gate.lbl.mgmt"), "HTTP:" + mgmt],
    ]);
    $("apHint").textContent = uiText("gate.sys.apHint", { ssid: s.apSsid });
    $("nNote").hidden = !s.hasAp;
    $("nNote").textContent = uiText("gate.sys.nNote", {
      ssid: s.apSsid,
      url: apUrl(),
    });
    if (!nfDirty()) nfFill(s);
  } catch (e) {
    for (const id of ["sDev", "sNet"])
      if ($(id).querySelector(".muted"))
        $(id).firstChild.textContent = uiText("gate.sys.failLoad");
    toast(uiText("gate.sys.loadFail", { m: e.message }), "bad");
  }
  sysBusy = false;
  $("sRefresh").disabled = false;
  syncSetup();
}
$("sRefresh").onclick = loadSystem;
async function loadAuth() {
  try {
    const a = await api("/api/auth/status", null, 4000);
    $("aText").textContent = a.authEnabled
      ? uiText("gate.sys.authOn")
      : uiText("gate.sys.authOff");
    $("aOff").hidden = !a.authEnabled;
    $("aOut").hidden = !a.loggedIn;
  } catch (e) {
    $("aText").textContent = uiText("gate.sys.authFail");
  }
}
$("aSave").onclick = async () => {
  const p = $("aPass").value,
    k = $("aKey").value.trim();
  if (!p && !k) return toast(uiText("gate.sys.enterCred"), "bad");
  try {
    if (p)
      await post("/api/auth/password", "password=" + encodeURIComponent(p));
    if (k) await post("/api/auth/apikey", "apikey=" + encodeURIComponent(k));
    $("aPass").value = "";
    toast(uiText("gate.sys.savedAcl"), "ok");
    loadAuth();
  } catch (e) {
    toast(uiText("gate.sys.saveFail", { m: e.message }), "bad");
  }
};
$("aOff").onclick = async () => {
  if (
    !(await ask({
      title: uiText("gate.sys.disableTtl"),
      text: uiText("gate.sys.disableQ"),
      ok: uiText("gate.sys.disableBtn"),
      danger: true,
    }))
  )
    return;
  try {
    await post("/api/auth/password", "password=");
    $("aPass").value = "";
    toast(uiText("gate.sys.disabled"), "ok");
    loadAuth();
  } catch (e) {
    toast(uiText("gate.sys.disableFail", { m: e.message }), "bad");
  }
};
$("aOut").onclick = async () => {
  try {
    await post("/api/auth/logout", "");
  } catch (e) {}
  location.replace("/login"); // 로그아웃은 로그인 페이지로
};
const apUrl = () =>
  "http://" +
  ((sys && sys.apIp) || "192.168.4.1") +
  (mgmt === 80 ? "" : ":" + mgmt) +
  "/";

$("bExport").onclick = () => {
  if (!st || (pointsOn() && !loaded))
    return toast(uiText("gate.sys.loadFirst"), "bad");
  const data = {
    format: "nanogate-backup",
    version: 1,
    profile: PROFILE,
    exportedAt: new Date().toISOString(),
    device: { ip: st.ip, target: st.target },
    settings: profileExport(st),
    // 포인트 백업 논리는 points.nf.js의 훅입니다. 없는 제품에서는 빈 배열로 남습니다.
    points: window.POINTS_BACKUP ? POINTS_BACKUP.export() : [],
  };
  const a = el("a");
  a.href = URL.createObjectURL(
    new Blob([JSON.stringify(data, null, 2)], { type: "application/json" }),
  );
  a.download =
    "nanogate-" +
    st.ip.replace(/\./g, "-") +
    "-" +
    new Date().toISOString().slice(0, 10) +
    ".json";
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(a.href), 5000);
  toast(
    pointsOn() && pointsDirty()
      ? uiText("gate.sys.exportedDirty")
      : uiText("gate.sys.exported"),
    "ok",
  );
};
$("bImport").onclick = () => $("bFile").click();
$("bFile").onchange = async (e) => {
  const file = e.target.files[0];
  e.target.value = "";
  if (!file) return;
  try {
    if (!st) throw new Error(uiText("gate.sys.stateFirst"));
    let data;
    try {
      data = JSON.parse(await file.text());
    } catch (x) {
      throw new Error(uiText("gate.sys.badJson"));
    }
    if (!data || data.format !== "nanogate-backup" || data.version !== 1)
      throw new Error(uiText("gate.sys.notBackup"));
    if (data.profile !== PROFILE)
      throw new Error(uiText("gate.sys.otherProfile", { p: data.profile }));
    const r = profileImport(data.settings);
    // 포인트 검증·직렬화 훅도 points.nf.js에 있습니다. 없는 제품은 건너뜁니다.
    let list = null;
    if (window.POINTS_BACKUP) list = POINTS_BACKUP.validate(data);
    const items = [];
    if (list)
      items.push(uiText("gate.sys.replacePts", { n: list.length }));
    items.push(
      r.changed.length
        ? uiText("gate.sys.changeReboot", { n: r.changed.length })
        : uiText("gate.sys.noChange"),
    );
    if (
      !(await ask({
        title: uiText("gate.sys.restoreTtl"),
        items,
        text: list ? uiText("gate.sys.overwrite") : "",
        ok: uiText("gate.sys.restoreBtn"),
      }))
    )
      return;
    if (list) await POINTS_BACKUP.apply(list);
    if (r.changed.length) {
      await post("/api/settings", new URLSearchParams(r.s).toString());
      startReboot(uiText("gate.sys.restoredReboot"));
    } else toast(uiText("gate.sys.restored"), "ok");
  } catch (err) {
    toast(uiText("gate.sys.restoreFail", { m: err.message }), "bad");
  }
};

function syncSetup() {
  const b = $("mSetup"),
    setup = !!st && st.mode === "setup";
  b.textContent = setup
    ? uiText("gate.sys.exitSetup")
    : uiText("gate.sys.enterSetupBtn");
  b.disabled = !sys || (setup ? !sys.ssid : !sys.hasAp);
}
$("mReboot").onclick = async () => {
  if (
    await ask({
      title: uiText("gate.sys.rebootTtl"),
      text: uiText("gate.sys.rebootText"),
      ok: uiText("gate.ui.reboot"),
      danger: true,
    })
  )
    act("/api/reboot", uiText("gate.sys.rebooting"));
};
$("mReset").onclick = async () => {
  if (
    await ask({
      title: uiText("gate.sys.resetTtl"),
      items: [
        uiText("gate.sys.resetItem1"),
        pointsOn()
          ? uiText("gate.sys.resetItem2a")
          : uiText("gate.sys.resetItem2b"),
      ],
      text: uiText("gate.sys.rebootAfter"),
      ok: uiText("gate.sys.resetBtn"),
      danger: true,
    })
  )
    act("/api/settings/reset", uiText("gate.sys.resetRebooting"));
};
$("mSetup").onclick = async () => {
  if (!sys) return;
  if (st && st.mode === "setup") {
    if (
      await ask({
        title: uiText("gate.sys.exitTtl"),
        items: [uiText("gate.sys.exitItem", { ssid: sys.ssid })],
        text: uiText("gate.sys.disconnect"),
        ok: uiText("gate.sys.exitSetup"),
      })
    )
      act(
        "/api/setup-mode",
        uiText("gate.sys.rejoining"),
        uiText("gate.sys.rejoinNote", { ssid: sys.ssid }),
      );
    return;
  }
  if (
    await ask({
      title: uiText("gate.sys.enterTtl"),
      items: [
        uiText("gate.sys.enterItem1", { ssid: sys.apSsid }),
        uiText("gate.sys.enterItem2"),
        uiText("gate.sys.addrItem", { url: apUrl() }),
        uiText("gate.sys.enterItem4"),
      ],
      text: uiText("gate.sys.disconnect"),
      ok: uiText("gate.sys.enterBtn"),
      danger: true,
    })
  )
    act(
      "/api/setup-mode",
      uiText("gate.sys.enterRebooting"),
      uiText("gate.sys.apConnectNote", { ssid: sys.apSsid }),
      apUrl(),
    );
};
$("mFactory").onclick = async () => {
  const ap = sys ? sys.apSsid : "nanoGate-XXXX";
  if (
    await ask({
      title: uiText("gate.sys.factTtl"),
      items: [
        pointsOn()
          ? uiText("gate.sys.factItem1a")
          : uiText("gate.sys.factItem1b"),
        uiText("gate.sys.factItem2", { ap: ap }),
        uiText("gate.sys.addrItem", { url: apUrl() }),
      ],
      text: uiText("gate.sys.factText"),
      ok: uiText("gate.sys.factBtn"),
      danger: true,
    })
  )
    act(
      "/api/factory-reset",
      uiText("gate.sys.factRebooting"),
      uiText("gate.sys.factNote", { ap: ap }),
      apUrl(),
    );
};
