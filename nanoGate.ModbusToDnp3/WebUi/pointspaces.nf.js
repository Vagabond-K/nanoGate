// nanoGate.ModbusToDnp3 포인트 대화상자 확장: DNP3 영역 표·레이블·프로토콜 제안.
// 코어 pointdlg가 실행 시점에 읽으므로 파일 로드 순서와 무관합니다.
window.POINT_SPACE_PROTOCOLS = {
  dnp3: [
    "DNP3",
    ["binary-input", "binary-output", "analog-input", "analog-output", "counter"],
  ],
};
window.POINT_SPACE_LABELS = {
  "binary-input": "Binary Input",
  "binary-output": "Binary Output",
  "analog-input": "Analog Input",
  "analog-output": "Analog Output",
  counter: "Counter",
};
(function () {
  const dl = document.getElementById("dlProto");
  if (dl) dl.append(new Option("dnp3"));
})();
