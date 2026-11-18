// 공용 셸 스크립트입니다. 상태·API·폼·라우팅·설정 배선과 부트스트랩을 담당하며,
// 게이트웨이 페이지는 훅(profileRender/profileTick/checkSettings/syncForm 등)과
// 고유 기능만 남깁니다. 페이지가 나중에 실행되므로 훅 참조는 모두 실행 시점에 해결됩니다.
      "use strict";
var CFG = {
  saveText: uiText("gate.iot.saveNote"),
  resetText: uiText("gate.iot.resetNote"),
};
      const $ = (id) => document.getElementById(id);
      const el = (tag, cls, text) => {
        const e = document.createElement(tag);
        if (cls) e.className = cls;
        if (text != null) e.textContent = text;
        return e;
      };
      const VIEWS = [
        "dashboard",
        "settings",
        "points",
        "cache",
        "network",
        "system",
      ];
      // 탭은 페이지가 실제로 포함한 뷰(<points /> 등)로만 구성합니다.
      // 참조되지 않은 조각은 빌드 산출물에도 없으므로 동적 숨김 대신 탭을 제거합니다.
      function syncTabs() {
        for (let i = VIEWS.length - 1; i >= 0; i--) {
          const v = VIEWS[i];
          if ($("v-" + v)) continue;
          VIEWS.splice(i, 1);
          const t = $("t-" + v);
          if (t) t.remove();
        }
        // 탭 라벨·순서는 각 프로젝트의 tabs.nf.html 컴포넌트가 선언합니다(셸의 <tabs />).
      }
      const SOURCES = {
        file: uiText("gate.ui.srcFile"),
        default: uiText("gate.ui.srcDefault"),
        "default-invalid": uiText("gate.ui.srcInvalid"),
        "default-storage": uiText("gate.ui.srcStorage"),
      };
      let HEADER = null,
        MAX_POINTS = 0,
        MAX_BYTES = 0,
        LIM = null;
      function applyLimits(s) {
        if (!s.limits) return;
        LIM = s.limits;
        MAX_POINTS = LIM.points;
        MAX_BYTES = LIM.bytes;
        HEADER = "schemaVersion=" + LIM.pointsSchema;
        const map = {
          tok: LIM.tokMax,
          addr: LIM.addrMax,
          name: LIM.nameMax,
          id: LIM.idMax,
          ssid: LIM.ssidMax,
        };
        for (const i of document.querySelectorAll("[data-max]"))
          i.maxLength = map[i.dataset.max];
      }
      let st = null,
        seenAt = 0,
        failures = 0,
        rebooting = false,
        mgmt = 80;
      let sys = null;
      let points = [],
        saved = [],
        loaded = false;
      const feat = WebUi.feat,
        MAN = WebUi.M;
      let base = null;
      // Provide PROFILE/KEYS/LABELS etc. as global object properties rather than declarations.
      self.PROFILE = MAN.id;
      self.KEYS = WebUi.KEYS;
      self.LABELS = WebUi.LABELS;
      self.formValues = () => WebUi.values($("sf"));
      self.fillForm = function (s) {
        base = WebUi.fill($("sf"), s);
        syncForm();
      };
      self.profileDirty = () =>
        !!base && WebUi.changed(base, formValues()).length > 0;
      const settingsBody = (v) =>
        new URLSearchParams(
          Object.assign(
            {
              schemaVersion: String(st.schemaVersion),
              gatewayProfile: PROFILE,
            },
            v,
          ),
        );
      const accessList = () => feat("access", null);
      self.profileExport = function (s) {
        const o = {};
        for (const k of KEYS) o[k] = s[k];
        return o;
      };
      self.profileImport = function (o) {
        const s = {};
        for (const k of KEYS) {
          if (!o || o[k] == null)
            throw new Error(uiText("gate.ui.noItem", { k: k }));
          s[k] = String(o[k]);
        }
        s.schemaVersion = String(st.schemaVersion);
        s.gatewayProfile = PROFILE;
        const se = checkSettings(s),
          sk = Object.keys(se);
        if (sk.length)
          throw new Error(
            uiText("gate.ui.badValue", {
              label: LABELS[sk[0]],
              msg: se[sk[0]],
            }),
          );
        return { s, changed: KEYS.filter((k) => s[k] !== String(st[k])) };
      };
      function formState(errs) {
        const f = $("sf").elements,
          v = formValues(),
          chg = base ? KEYS.filter((k) => v[k] !== base[k]) : [];
        showErrors($("sf"), errs);
        for (const k of KEYS) {
          const i = f[k];
          (i.tagName ? i : i[0])
            .closest(".field,.check")
            .classList.toggle("chg", chg.includes(k));
        }
        const bad = Object.keys(errs).length;
        $("sfSave").disabled = !base || !chg.length || bad > 0;
        $("sfUndo").disabled = !chg.length;
        $("sfState").textContent = !base
          ? uiText("gate.ui.loading")
          : bad
            ? uiText("gate.ui.badInputs")
            : chg.length
              ? uiText("gate.ui.changedN", { n: chg.length })
              : uiText("gate.net.clean");
        $("t-settings").classList.toggle("dot", chg.length > 0);
        return { v, changed: chg, bad };
      }
      function formChanges(v) {
        return KEYS.filter((k) => v[k] !== base[k]).map(
          (k) =>
            LABELS[k] +
            ": " +
            WebUi.showValue($("sf"), k, base[k]) +
            " → " +
            WebUi.showValue($("sf"), k, v[k]),
        );
      }
      async function api(path, opt, ms) {
        const mut = !!(opt && opt.method && opt.method !== "GET");
        if (mut) WebUi.busy(true);
        const ac = new AbortController(),
          timer = setTimeout(() => ac.abort(), ms || 8000);
        try {
          const r = await fetch(
            path,
            Object.assign({ cache: "no-store", signal: ac.signal }, opt),
          );
          const json = (r.headers.get("Content-Type") || "").includes("json");
          const body = json ? await r.json().catch(() => ({})) : await r.text();
          if (!r.ok) {
            const err = new Error((json && body.error) || "HTTP " + r.status);
            if (r.status === 401) {
              err.unauth = true;
              location.replace("/login" + location.hash);
            }
            throw err;
          }
          return body;
        } catch (e) {
          if (e.name === "AbortError")
            throw new Error(uiText("gate.ui.timeout"));
          if (e instanceof TypeError)
            throw new Error(uiText("gate.ui.noConnect"));
          throw e;
        } finally {
          clearTimeout(timer);
          if (mut) WebUi.busy(false);
        }
      }
      let csrfH = "";
      async function csrf() {
        if (!csrfH) {
          try {
            csrfH =
              (await api("/api/auth/status", null, 4000)).csrfHeader || "";
          } catch (e) {}
        }
        return csrfH;
      }
      const post = async (path, body, type, ms) =>
        api(
          path,
          {
            method: "POST",
            headers: {
              [await csrf()]: "1",
              "Content-Type": type || "application/x-www-form-urlencoded",
            },
            body: body || "",
          },
          ms,
        );
      function toast(text, kind) {
        const t = el("div", "toast " + (kind || ""), text),
          life = kind === "bad" ? 7000 : 4000;
        $("toasts").append(t);
        setTimeout(() => t.classList.add("out"), life - 500);
        setTimeout(() => t.remove(), life);
      }
      function dur(sec) {
        sec = Math.max(0, Math.floor(sec));
        const d = Math.floor(sec / 86400),
          h = Math.floor((sec % 86400) / 3600),
          m = Math.floor((sec % 3600) / 60),
          s = sec % 60;
        return d
          ? uiText("gate.ui.durD", { d: d, h: h })
          : h
            ? uiText("gate.ui.durH", { h: h, m: m })
            : m
              ? uiText("gate.ui.durM", { m: m, s: s })
              : uiText("gate.ui.durS", { s: s });
      }
      const num = (v) => Number(v).toLocaleString("ko-KR");
      const kb = (v) => (v / 1024).toFixed(1) + " KB";
      const isInt = (v, lo, hi) => /^-?\d+$/.test(v) && +v >= lo && +v <= hi;
      function fillDl(id, rows) {
        const d = $(id);
        d.textContent = "";
        for (const [k, v] of rows)
          d.append(
            el("dt", "", k),
            el(
              "dd",
              "",
              v == null || v === "" || v === "0.0.0.0" ? "-" : String(v),
            ),
          );
      }
      function showErrors(form, errs) {
        const boxes = [...form.querySelectorAll(".field")];
        for (const i of form.elements) {
          const b = i.closest ? i.closest(".field") : null;
          if (b && !boxes.includes(b)) boxes.push(b);
        }
        for (const box of boxes) {
          const i = box.querySelector(
            "input,select,wui-number,wui-password,wui-search",
          );
          const msg = (i && errs[i.getAttribute("name")]) || "";
          let m = box.querySelector(".ferr");
          if (!m) {
            m = el("em", "ferr");
            box.append(m);
          }
          m.textContent = msg;
          box.classList.toggle("invalid", !!msg);
          if (i && i.tagName.indexOf("WUI-") === 0)
            i.classList.toggle("invalid", !!msg);
        }
      }
      function route() {
        let v = location.hash.slice(1);
        if (!VIEWS.includes(v)) v = "dashboard";
        for (const x of VIEWS) {
          $("v-" + x).hidden = x !== v;
          $("t-" + x).classList.toggle("on", x === v);
          if (x === v) $("t-" + x).setAttribute("aria-current", "page");
          else $("t-" + x).removeAttribute("aria-current");
        }
        $("t-" + v).scrollIntoView({ block: "nearest", inline: "nearest" });
        if (v === "system" || v === "network") loadSystem();
        if (v === "cache") loadCache();
      }
      addEventListener("hashchange", route);
      async function refresh() {
        if (rebooting) return;
        try {
          st = await api("/api/status", null, 5000);
          seenAt = Date.now();
          failures = 0;
          WebUi.caps(st.capabilities); // feat()가 capabilities를 보게 하는 연결(features→capabilities 마이그레이션 후 누락분)
          if (st.limits && !LIM) applyLimits(st);
          $("offline").hidden = true;
          render();
          if (location.hash === "#cache" && VIEWS.includes("cache")) loadCache();
        } catch (e) {
          if (e.unauth) return;
          if (++failures >= 2) {
            $("offline").hidden = false;
            $("hState").textContent = uiText("gate.ui.disconnected");
            $("hState").className = "pill bad";
          }
        }
      }
      function tick() {
        if (!st) return;
        const age = (Date.now() - seenAt) / 1000;
        const ageEl = $("dAge");
        if (ageEl)
          ageEl.textContent = uiText("gate.ui.ago", { d: dur(age) });
        profileTick(st.uptime + age);
      }
      function render() {
        const s = st,
          setup = s.mode === "setup";
        mgmt = s.managementPort || 80;
        $("hIp").textContent = s.ip;
        $("hState").textContent = setup
          ? uiText("gate.ui.setupMode")
          : s.gatewayReady
            ? uiText("gate.ui.running")
            : uiText("gate.ui.gwError");
        $("hState").className =
          "pill " + (setup ? "warn" : s.gatewayReady ? "ok" : "bad");
        $("setup").hidden = !setup;
        if (setup && !location.hash) location.hash = "network";
        profileRender(s);
        tick();
        syncSetup();
      }
      const addrCfg = (pre) => {
        const a = feat("addr");
        return a ? a[pre] : null;
      };
      const addrTd = (show, f, cfg, cls) => {
        const [l1, l2] = addrCell(show, f, cfg);
        return WebUi.node(
          "td",
          cls ? { className: cls } : {},
          l1
            ? [
                WebUi.node("div", { text: l1 }),
                WebUi.node("div", { className: "muted mono", text: l2 }),
              ]
            : WebUi.node("div", { className: "mono", text: l2 }),
        );
      };
      const addrShow = (pre) => {
        const cfg = addrCfg(pre);
        return cfg && cfg.show ? cfg.show : ["p", "d", "s", "a", "t"];
      };
      const scaleOn = () => feat("scale", true);
      // 포인트 기능은 caps가 아니라 페이지가 포인트 뷰를 포함하는지로 결정합니다.
      const pointsOn = () => !!$("v-points");
      const nameOn = () => feat("name", true);
      const addrCell = (show, f, cfg) => {
        const dv =
          cfg && cfg.d && cfg.d.label && f.d ? cfg.d.label + " " + f.d : f.d;
        const l1 = [show.includes("p") ? f.p : "", show.includes("d") ? dv : ""]
          .filter((x) => x)
          .join(" · ");
        let l2 =
          (show.includes("s") ? spaceLabel(f.s) + " " : "") +
          (show.includes("a") ? f.a : "");
        if (show.includes("t")) l2 += (l2 ? " · " : "") + f.t;
        return [l1, l2];
      };
      const BAUDS = [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200];
      const PARITY = {
        None: ["N", uiText("gate.ui.parityN")],
        Even: ["E", uiText("gate.ui.parityE")],
        Odd: ["O", uiText("gate.ui.parityO")],
      };
      const frame = (s) =>
        s.dataBits + "-" + PARITY[s.parity][0] + "-" + s.stopBits;
      let pins = [],
        pinsText = "";
      function setPins(text) {
        pinsText = text;
        pins = [];
        for (const part of text.split(",")) {
          const [a, b] = part.trim().split("-").map(Number);
          for (let i = a; i <= (isNaN(b) ? a : b); i++) pins.push(i);
        }
        const f = $("sf").elements;
        for (const k of ["txPin", "rxPin", "rs485DePin"]) {
          const sel = f[k];
          if (!sel) continue;
          const v = sel.value;
          sel.textContent = "";
          for (const p of pins)
            sel.append(new Option(uiText("gate.ui.gpio", { p: p }), String(p)));
          sel.value = v;
        }
        let hint = uiText("gate.iot.pinList", { b: st.target, p: text });
        if (/C3/i.test(st.target)) hint += uiText("gate.iot.pinMini");
        $("pinHint").textContent = hint;
      }
      for (const b of BAUDS)
        $("sf").elements.baudRate.append(new Option(num(b), String(b)));
      $("sf").addEventListener("input", syncForm);
      $("sf").addEventListener("change", syncForm);
      $("sfUndo").onclick = () => {
        if (st) fillForm(st);
      };
      $("sf").onsubmit = async (e) => {
        e.preventDefault();
        const v = formValues();
        if (!base || Object.keys(checkSettings(v)).length)
          return toast(uiText("gate.ui.badInputs"), "bad");
        const changes = formChanges(v);
        if (!changes.length) return;
        if (
          !(await ask({
            title: uiText("gate.iot.saveTtl"),
            items: changes,
            text: CFG.saveText,
            ok: uiText("gate.net.saveReboot"),
          }))
        )
          return;
        try {
          await post("/api/settings", settingsBody(v).toString());
          startReboot(uiText("gate.iot.savedReboot"));
        } catch (err) {
          toast(uiText("gate.ui.saveFail", { m: err.message }), "bad");
        }
      };
      $("dReset").onclick = async () => {
        if (
          !(await ask({
            title: uiText("gate.iot.resetStatTtl"),
            text: CFG.resetText,
            ok: uiText("gate.ui.reset"),
          }))
        )
          return;
        try {
          await post("/api/statistics/reset");
          toast(uiText("gate.iot.statReset"), "ok");
          refresh();
        } catch (e) {
          toast(e.message, "bad");
        }
      };
      addEventListener("DOMContentLoaded", () => {
        syncForm();
        // 제품 전용 초기화(예: Dnp3 규칙 로드)가 있으면 여기서 실행한다.
        if (typeof profileBoot === "function") profileBoot();
        addEventListener("beforeunload", (e) => {
          // 포인트 조각이 없는 페이지에서는 pointsDirty 식별자 자체가 없으므로 먼저 가립니다.
          if (
            !rebooting &&
            (profileDirty() || (pointsOn() && pointsDirty()) || nfDirty())
          ) {
            e.preventDefault();
            e.returnValue = "";
          }
        });
        nfSync();
        syncTabs();
        if (!pointsOn()) $("bText").textContent = uiText("gate.iot.bText");
        route();
        setInterval(() => {
          if (!document.hidden) refresh();
        }, 5000);
        setInterval(tick, 1000);
        refresh().then(pointsOn() ? loadPoints : null);
      });
