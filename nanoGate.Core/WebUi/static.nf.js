// 정적 영역 로커라이어: 기본 언어(영어) 외의 언어에서 HTML 마크업에 박힌 영어 텍스트를
// __wuiTx.static[uiLang] 사전(en 원문 -> 번역)으로 한 번에 치환합니다.
// 런타임 문자열(toast/ask 등)은 각 호출부의 uiText()가 담당하고, 이 코드는
// index.html/nf.html의 정적 노드·placeholder·title·manifest 레이블만 건드립니다.
function localizeStatic() {
  if (uiLang === "en" || typeof document === "undefined" || !document.body)
    return;
  var d = (window.__wuiTx || {}).static;
  var s = d && d[uiLang];
  if (!s) return;
  var w = document.createTreeWalker(
      document.body,
      NodeFilter.SHOW_TEXT,
      null,
      false,
    ),
    n;
  while ((n = w.nextNode())) {
    var p = n.parentNode;
    if (!p || p.nodeName === "SCRIPT" || p.nodeName === "STYLE") continue;
    var raw = n.nodeValue,
      cut = raw.trim();
    if (!cut) continue;
    // 개행/다중 공백이 섞인 마크업도 en 원문(한 줄)과 짝지어지도록 공백 정규화.
    var v = s[cut] != null ? s[cut] : s[cut.replace(/\s+/g, " ")];
    if (v != null) n.nodeValue = raw.replace(cut, v);
  }
  var at = document.querySelectorAll("[placeholder]");
  for (var i = 0; i < at.length; i++) {
    var t2 = s[at[i].getAttribute("placeholder")];
    if (t2 != null) at[i].setAttribute("placeholder", t2);
  }
  var t3 = s[document.title.trim()];
  if (t3 != null) document.title = t3;
  var L = WebUi.LABELS;
  for (var k in L) {
    var v2 = s[L[k]];
    if (v2 != null) L[k] = v2;
  }
}
if (typeof document !== "undefined") {
  if (document.readyState === "loading")
    document.addEventListener("DOMContentLoaded", localizeStatic);
  else localizeStatic();
}
