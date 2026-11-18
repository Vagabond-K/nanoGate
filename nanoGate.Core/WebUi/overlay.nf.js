function startReboot(text, note, link) {
  rebooting = true;
  const t0 = Date.now(),
    up0 = st ? st.uptime + (t0 - seenAt) / 1000 : 1e9;
  $("ovText").textContent = text;
  $("ovSub").textContent = "";
  $("ovNote").textContent = note || "";
  $("ovNote").hidden = !note;
  $("ovLink").hidden = !link;
  if (link) {
    $("ovLink").href = link;
    $("ovLink").textContent = link;
  }
  $("ovRetry").hidden = true;
  $("overlay").hidden = false;
  const poll = async () => {
    const gone = (Date.now() - t0) / 1000;
    $("ovSub").textContent = uiText("gate.ov.elapsed", { n: Math.round(gone) });
    if (gone > 180) {
      $("ovText").textContent = uiText("gate.ov.timeout");
      $("ovSub").textContent = uiText("gate.ov.verify");
      $("ovRetry").hidden = false;
      return;
    }
    try {
      const s = await api("/api/status", null, 2500);
      if (s.uptime + 3 < up0 + gone) {
        location.reload();
        return;
      }
    } catch (e) {
      // 재부팅하면 메모리 세션 쿠키가 사라져 status가 401을 돌려줍니다 = 장치는 살아났습니다 → 새로 고침.
      if (e && e.unauth) {
        location.reload();
        return;
      }
    }
    setTimeout(poll, 2000);
  };
  setTimeout(poll, 2500);
}
$("ovRetry").onclick = () => location.reload();
async function act(path, text, note, link) {
  try {
    await post(path);
    startReboot(text, note, link);
  } catch (e) {
    toast(e.message, "bad");
  }
}
