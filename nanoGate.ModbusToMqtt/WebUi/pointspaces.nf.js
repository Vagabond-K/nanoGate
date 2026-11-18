// nanoGate.ModbusToMqtt 포인트 대화상자 확장: MQTT 영역 표·프로토콜 제안.
// 코어 pointdlg가 실행 시점에 읽으므로 파일 로드 순서와 무관합니다.
window.POINT_SPACE_PROTOCOLS = { mqtt: ["MQTT", ["topic"]] };
(function () {
  const dl = document.getElementById("dlProto");
  if (dl) dl.append(new Option("mqtt"));
})();
