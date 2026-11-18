let cache = null;

async function loadCache() {
  try {
    cache = await api("/api/cache", null, 15000);
    $("cErr").hidden = true;
  } catch (e) {
    $("cErr").hidden = false;
    $("cErr").textContent = uiText("gate.cc.readFail", { m: e.message });
  }
  renderCache();
}

// vtext는 vnode(또는 그 배열)를 텍스트로 평탄화합니다. 캐시 검색 매칭에 사용합니다.
function vtext(n) {
  if (n == null) return "";
  if (Object.prototype.toString.call(n) === "[object Array]") {
    var s = "";
    for (var i = 0; i < n.length; i++) s += vtext(n[i]) + " ";
    return s;
  }
  if (n.t === "#") return n.v;
  if (n.p && n.p.text != null) return String(n.p.text) + " ";
  if (n.k) {
    var t = "";
    for (var j = 0; j < n.k.length; j++) t += vtext(n.k[j]);
    return t;
  }
  return "";
}
// 기본 cacheRow 컴포넌트: 레거시 profileCacheRow(p) 문자열 배열 훅(또는 단순 ID 셀)을
// td vnode으로 연결합니다. 프로파일은 WebUi.component('cacheRow', ...)로 이를 재정의해
// 마크업 전체를 직접 제어할 수 있습니다.
WebUi.component("cacheRow", function (a) {
  // profileCacheRow는 레거시 훅(예: DNP3)입니다. 프로필이 component로
  // 재정의하지 않았는데 훅도 없으면 ID 셀로 강등(블록 순서 사고 방지).
  const cells =
    (a.cc && typeof profileCacheRow === "function"
      ? profileCacheRow(a.p)
      : null) || [a.p.id];
  return cells.map(function (c) {
    return WebUi.node("td", { className: "mono", text: c });
  });
});

function renderCache() {
  const q = $("cq").value.trim().toLowerCase();
  const pts = cache ? cache.points : [];
  // 선행 열은 cacheRow 컴포넌트(프로파일 재정의 가능)에서 오고, 값/품질은 셸이 소유합니다.
  const cc = feat("cacheCols"),
    node = WebUi.node,
    rows = [];
  // head 항목이 "@"로 시작하면 예약 번역 키로 해석한다(프로필 JSON에 언어를 굳히지 않기 위한 규칙).
  // uiText 호출은 정적으로 적어야 빌드 때 en 원문이 주입되므로 사전에 사전 등록된 토큰만 지원한다.
  const H = {
    rule: uiText("gate.cc.rule"),
    topic: uiText("gate.cc.topic"),
  };
  const heads = cc ? cc.head.map(h => (h.charAt(0) === "@" ? H[h.substring(1)] || h : h)) : null;
  $("cq").placeholder = heads
    ? uiText("gate.cc.searchCols", { h: heads.join("·") })
    : uiText("gate.cc.searchId");
  const head = $("cHead");
  head.textContent = "";
  for (const x of (heads || [uiText("gate.cc.id")]).concat([
    uiText("gate.cc.val"),
    uiText("gate.cc.qual"),
  ]))
    head.append(el("th", "", x));
  let shown = 0;
  for (const p of pts) {
    const lead = WebUi.slot("cacheRow", { p: p, cc: cc });
    const text = vtext(lead);
    if (
      q &&
      text.toLowerCase().indexOf(q) < 0 &&
      p.id.toLowerCase().indexOf(q) < 0
    )
      continue;
    shown++;
    rows.push(
      node(
        "tr",
        { key: p.id },
        lead,
        node("td", { className: "mono", text: p.v }),
        node(
          "td",
          {},
          node("span", { className: "pill " + (p.ok ? "ok" : "bad"), text: p.q }),
        ),
      ),
    );
  }
  WebUi.render($("cRows"), rows);
  $("cEmpty").hidden = shown > 0;
  $("cEmpty").textContent = pts.length
    ? uiText("gate.ui.noSearch")
    : pointsOn()
      ? uiText("gate.cc.emptyPts")
      : uiText("gate.cc.empty");
  $("cCount").textContent =
    shown === pts.length
      ? uiText("gate.cc.count", { n: pts.length })
      : uiText("gate.cc.countOf", { a: shown, b: pts.length });
}
WebUi.on("wui:search", renderCache);
$("cReload").onclick = loadCache;
