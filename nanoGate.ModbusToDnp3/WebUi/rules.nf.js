      // DNP3 규칙 표: 직렬화·렌더링·편집 다이얼로그·서버 동기화입니다.
      const RHEADER = "rangeVersion=1";
      const RHEADER2 = "rangeVersion=2";
      const RSPACES = [
        ["coil", "Coil"],
        ["discrete-input", "Discrete Input"],
        ["input-register", "Input Register"],
        ["holding-register", "Holding Register"],
      ];
      const RKINDS = [
        ["Bool", "Bool"],
        ["Int16", "Int16"],
        ["UInt16", "UInt16"],
        ["Int32", "Int32"],
        ["UInt32", "UInt32"],
        ["Float32", "Float32"],
      ];
      const RORDERS = [
        ["ABCD", "ABCD"],
        ["CDAB", "CDAB"],
        ["BADC", "BADC"],
        ["DCBA", "DCBA"],
      ];
      const TONAMES = {
        "binary-input": "Binary Input",
        "binary-output": "Binary Output",
        "analog-input": "Analog Input",
        "analog-output": "Analog Output",
        counter: "Counter",
      };
      const RTOS = [
        ["binary-input", "Binary Input"],
        ["binary-output", uiText("gate.dnp.rtBo")],
        ["analog-input", "Analog Input"],
        ["analog-output", uiText("gate.dnp.rtAo")],
        ["counter", "Counter"],
      ];
      const is32 = (k) => k === "Int32" || k === "UInt32" || k === "Float32";
      const isBit = (s) => s === "coil" || s === "discrete-input";
      const scaled = (r) =>
        (r.num && r.num !== "1") ||
        (r.den && r.den !== "1") ||
        (r.off && r.off !== "0");
      const nz = (v, d) => (v && v !== d ? v : "");
      let rules = null,
        rulesSaved = "";
      function parseRules(text) {
        const lines = String(text).split("\n");
        if (
          !lines.length ||
          !/^rangeVersion=[12]$/.test(lines[0].replace(/\r$/, ""))
        )
          throw new Error(uiText("gate.dnp.badVer"));
        const out = [];
        for (let i = 1; i < lines.length; i++) {
          const x = lines[i].replace(/\r$/, "");
          if (!x) continue;
          if (!x.startsWith("range="))
            throw new Error(uiText("gate.dnp.badLine", { n: i + 1 }));
          const a = x.slice(6).split("|");
          if ((a.length !== 9 && a.length !== 12) || a[a.length - 1] !== "")
            throw new Error(uiText("gate.dnp.badFields", { n: i + 1 }));
          out.push({
            unit: a[0],
            space: a[1],
            from: a[2],
            count: a[3],
            kind: a[4],
            order: a[5] || "ABCD",
            to: a[6],
            index: a[7],
            num: a[8] || "",
            den: a[9] || "",
            off: a[10] || "",
          });
        }
        return out;
      }
      function serializeRules() {
        const v2 = rules.some(scaled);
        return (
          (v2 ? RHEADER2 : RHEADER) +
          "\n" +
          rules
            .map(
              (r) =>
                "range=" +
                r.unit +
                "|" +
                r.space +
                "|" +
                r.from +
                "|" +
                r.count +
                "|" +
                r.kind +
                "|" +
                (is32(r.kind) ? r.order : "") +
                "|" +
                r.to +
                "|" +
                r.index +
                "|" +
                (v2
                  ? nz(r.num, "1") +
                    "|" +
                    nz(r.den, "1") +
                    "|" +
                    nz(r.off, "0") +
                    "|"
                  : "") +
                "\n",
            )
            .join("")
        );
      }
      function rulesDirty() {
        return !!rules && serializeRules() !== rulesSaved;
      }
      function mapDirty() {
        return rulesDirty();
      }
      function ruleSummary(r) {
        const sp = (RSPACES.find((x) => x[0] === r.space) || ["", r.space])[1];
        const w = is32(r.kind) ? 2 : 1;
        const n = +r.count || 1;
        const src = r.from + (n > 1 ? "~" + ((+r.from || 0) + n * w - 1) : "");
        const dst = r.index + (n > 1 ? "~" + ((+r.index || 0) + n - 1) : "");
        return (
          "Unit " +
          (r.unit || 1) +
          " · " +
          sp +
          " " +
          src +
          " · " +
          r.kind +
          (is32(r.kind) && r.order && r.order !== "ABCD" ? ":" + r.order : "") +
          " → " +
          (TONAMES[r.to] || r.to) +
          " " +
          dst
        );
      }
      function ruleRow(i, r) {
        const tr = el("tr");
        tr.append(el("td", "muted", String(i + 1)));
        tr.append(el("td", "mono", ruleSummary(r)));
        tr.append(
          el(
            "td",
            scaled(r) ? "" : "muted",
            scaled(r)
              ? "× " +
                  (r.num || 1) +
                  " ÷ " +
                  (r.den || 1) +
                  " + " +
                  (r.off || 0)
              : "—",
          ),
        );
        const td = el("td");
        const edit = el("button", "", uiText("gate.ui.edit"));
        edit.type = "button";
        edit.onclick = () => openRule(i);
        const del = el("button", "", uiText("gate.ui.delete"));
        del.type = "button";
        del.onclick = () => {
          rules.splice(i, 1);
          renderRules();
        };
        td.append(edit, " ", del);
        tr.append(td);
        return tr;
      }
      function updateRulesState() {
        const dirty = rulesDirty();
        $("rSave").disabled = !rules || !dirty;
        $("rClear").disabled = !rules || (!dirty && !rules.length);
        $("rState").textContent = !rules
          ? uiText("gate.dnp.rsFail")
          : dirty
            ? uiText("gate.dnp.rsDirty")
            : uiText("gate.dnp.rsClean");
        $("t-points").classList.toggle("dot", pointsDirty() || dirty);
      }
      function renderRules() {
        const tb = $("rRows");
        tb.textContent = "";
        if (rules)
          for (const [i, r] of rules.entries()) tb.append(ruleRow(i, r));
        $("rEmpty").hidden = !rules || rules.length > 0;
        $("rEmpty").textContent = uiText("gate.dnp.rsEmpty");
        updateRulesState();
      }
      async function loadRules() {
        try {
          const text = await api("/api/rules", null, 15000);
          rulesSaved = text.endsWith("\n") ? text : text + "\n";
          rules = parseRules(rulesSaved);
          $("rErr").hidden = true;
        } catch (e) {
          rules = null;
          $("rErr").hidden = false;
          $("rErr").textContent = uiText("gate.dnp.loadFail", { m: e.message });
        }
        renderRules();
      }
      const fsRules = $("fsRules");
      $("v-points").querySelector(".head").after(fsRules);
      $("pEditor").hidden = true;
      let ruleEditing = -1;
      function ruleForm() {
        return $("rdf").elements;
      }
      function ruleFormula() {
        const e = ruleForm();
        const n = e.rNum.value.trim(),
          d = e.rDen.value.trim(),
          o = e.rOff.value.trim();
        $("rdFormula").textContent = uiText("gate.dnp.formula", {
          n: n || "1",
          d: d || "1",
          o: o || "0",
        });
      }
      // wui-number는 value를 같은 값으로 지정해도 input 이벤트를 올린다.
      // 아래 value="" 쓰기가 input→syncRuleDialog 재진입을 무한으로 되먹이므로 차단한다.
      let ruleSyncing = false;
      function syncRuleDialog() {
        if (ruleSyncing) return;
        ruleSyncing = true;
        try {
          const e = ruleForm();
          e.rOrder.disabled = !is32(e.rKind.value);
          const bit = isBit(e.rSpace.value);
          $("rdScale").hidden = bit;
          if (bit) {
            if (e.rNum.value) e.rNum.value = "";
            if (e.rDen.value) e.rDen.value = "";
            if (e.rOff.value) e.rOff.value = "";
          }
          ruleFormula();
        } finally {
          ruleSyncing = false;
        }
      }
      function openRule(i) {
        if (!rules) return loadRules();
        if (i < 0 && rules.length >= 64)
          return toast(uiText("gate.dnp.maxRules"), "bad");
        ruleEditing = i;
        let r;
        if (i < 0) {
          let next = 0;
          for (const x of rules) {
            const end = (+x.index || 0) + (+x.count || 0);
            if (end > next) next = end;
          }
          r = {
            unit: "1",
            space: "holding-register",
            from: "0",
            count: "10",
            kind: "Int16",
            order: "ABCD",
            to: "analog-input",
            index: String(next),
            num: "",
            den: "",
            off: "",
          };
        } else r = rules[i];
        $("rdTitle").textContent =
          i < 0
            ? uiText("gate.dnp.rdNew")
            : uiText("gate.dnp.rdEdit", { n: i + 1 });
        const e = ruleForm();
        e.rUnit.value = r.unit || 1;
        e.rSpace.value = r.space;
        e.rFrom.value = r.from;
        e.rCount.value = r.count;
        e.rKind.value = r.kind;
        e.rOrder.value = r.order || "ABCD";
        e.rTo.value = r.to;
        e.rIndex.value = r.index;
        e.rNum.value = r.num;
        e.rDen.value = r.den;
        e.rOff.value = r.off;
        delete $("rdf").dataset.tried;
        showErrors($("rdf"), {});
        syncRuleDialog();
        $("rd").showModal();
      }
      function dialogRule() {
        const e = ruleForm();
        const g = (x) => String(x.value).trim();
        return {
          unit: g(e.rUnit),
          space: e.rSpace.value,
          from: g(e.rFrom),
          count: g(e.rCount),
          kind: e.rKind.value,
          order: e.rOrder.value,
          to: e.rTo.value,
          index: g(e.rIndex),
          num: g(e.rNum),
          den: g(e.rDen),
          off: g(e.rOff),
        };
      }
      function checkRule(r) {
        const errs = {};
        const num = (v, min, max) => /^\d+$/.test(v) && +v >= min && +v <= max;
        if (!num(r.unit, 1, 247)) errs.rUnit = uiText("gate.dnp.crUnit");
        if (!num(r.from, 0, 65535))
          errs.rFrom = uiText("gate.ui.intRange", { a: 0, b: 65535 });
        if (!num(r.count, 1, 1024))
          errs.rCount = uiText("gate.ui.intRange", { a: 1, b: 1024 });
        if (!num(r.index, 0, 65535))
          errs.rIndex = uiText("gate.ui.intRange", { a: 0, b: 65535 });
        const w = is32(r.kind) ? 2 : 1;
        if (!errs.rFrom && +r.from + +r.count * w > 65536)
          errs.rFrom = uiText("gate.dnp.crFrom", { w: w });
        if (!errs.rIndex && +r.index + +r.count > 65536)
          errs.rIndex = uiText("gate.dnp.crIndex");
        for (const [k, name] of [
          ["num", "rNum"],
          ["den", "rDen"],
          ["off", "rOff"],
        ]) {
          if (r[k] && !/^-?\d+$/.test(r[k]))
            errs[name] = uiText("gate.dnp.crInt");
          else if (r[k] && Math.abs(+r[k]) > 1000000)
            errs[name] = uiText("gate.dnp.crAbs");
        }
        if (!errs.rDen && r.den === "0") errs.rDen = uiText("gate.dnp.crDen");
        if (
          !errs.rTo &&
          isBit(r.space) &&
          r.to !== "binary-input" &&
          r.to !== "binary-output"
        )
          errs.rTo = uiText("gate.dnp.crBitTo");
        if (
          !errs.rTo &&
          !isBit(r.space) &&
          (r.to === "binary-input" || r.to === "binary-output")
        )
          errs.rTo = uiText("gate.dnp.crRegTo");
        if (
          !errs.rTo &&
          r.to === "counter" &&
          r.kind !== "Int32" &&
          r.kind !== "UInt32"
        )
          errs.rTo = uiText("gate.dnp.crCnt");
        return errs;
      }
      $("rdf").addEventListener("input", syncRuleDialog);
      $("rdf").addEventListener("change", syncRuleDialog);
      $("rdCancel").onclick = () => $("rd").close();
      $("rdf").onsubmit = (ev) => {
        ev.preventDefault();
        const r = dialogRule();
        const errs = checkRule(r);
        if (Object.keys(errs).length) {
          $("rdf").dataset.tried = "1";
          showErrors($("rdf"), errs);
          return;
        }
        if (ruleEditing < 0) rules.push(r);
        else rules[ruleEditing] = r;
        $("rd").close();
        renderRules();
        toast(
          ruleEditing < 0
            ? uiText("gate.dnp.added")
            : uiText("gate.dnp.modified"),
          "ok",
        );
      };
      $("rAdd").onclick = () => openRule(-1);
      $("rReload").onclick = () => loadRules();
      $("rSave").onclick = async () => {
        if (!rules) return;
        const text = serializeRules();
        try {
          await post("/api/rules", text, "text/plain", 15000);
          rulesSaved = text;
          $("rErr").hidden = true;
          updateRulesState();
          toast(uiText("gate.dnp.saved", { n: rules.length }), "ok");
        } catch (e) {
          $("rErr").hidden = false;
          $("rErr").textContent = uiText("gate.ui.saveFail", { m: e.message });
        }
      };
      $("rClear").onclick = async () => {
        if (
          !(await ask({
            title: uiText("gate.dnp.clearTtl"),
            text: uiText("gate.dnp.clearText"),
            ok: uiText("gate.ui.delete"),
            danger: true,
          }))
        )
          return;
        try {
          await post("/api/rules/reset");
          rules = [];
          rulesSaved = RHEADER + "\n";
          $("rErr").hidden = true;
          renderRules();
          toast(uiText("gate.dnp.cleared"), "ok");
        } catch (e) {
          $("rErr").hidden = false;
          $("rErr").textContent = uiText("gate.ui.delFail", { m: e.message });
        }
      };

      // 셸 부트(DOMContentLoaded)에서 호출되는 규칙 초기 로드입니다.
      function profileBoot() {
        loadRules();
      }