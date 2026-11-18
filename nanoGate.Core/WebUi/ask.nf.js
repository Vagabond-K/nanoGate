function ask(o) {
  const d = $("ask"),
    b = $("askBody"),
    ok = $("askOk");
  $("askTitle").textContent = o.title;
  b.textContent = "";
  if (o.items) {
    const ul = el("ul");
    for (const x of o.items) ul.append(el("li", "", x));
    b.append(ul);
  }
  if (o.text) b.append(el("p", "muted", o.text));
  ok.textContent = o.ok || uiText("gate.ui.ok");
  ok.className = o.danger ? "danger" : "primary";
  d.returnValue = "";
  d.showModal();
  return new Promise((res) => {
    d.onclose = () => res(d.returnValue === "yes");
  });
}
$("askNo").onclick = () => $("ask").close("no");
$("askOk").onclick = () => $("ask").close("yes");
