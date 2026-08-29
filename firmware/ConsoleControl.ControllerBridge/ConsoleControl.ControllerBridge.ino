// SPDX-License-Identifier: GPL-3.0-or-later
//
// Additional permission under GNU GPL version 3 section 7:
// If you modify this Program, or any covered work, by linking or combining it
// with the ARM CryptoCell CC310 archive version 0.9.13 covered by the ARM Object
// Code and Header Files License version 1.0, the licensors of this Program grant
// you additional permission to convey the resulting work. This permission does
// not alter the license terms of the CryptoCell components.

#include <Adafruit_TinyUSB.h>
#include <bluefruit.h>

namespace {

constexpr uint16_t NintendoVendorId = 0x057e;
constexpr uint16_t ProControllerProductId = 0x2009;
constexpr uint32_t StateTimeoutMs = 250;
constexpr uint32_t ReportIntervalMs = 4;
constexpr uint8_t BleNeutralState[8] = {0, 0, 8, 128, 128, 128, 128, 0};
constexpr uint8_t ControllerMac[6] = {0x7c, 0xbb, 0x8a, 0xea, 0x30, 0x57};
constexpr size_t SwitchProPayloadLength = 63;

// Captured from a genuine 057e:2009 Pro Controller. TinyUSB carries the
// report ID separately, so each declared report has a 63-byte payload.
constexpr uint8_t SwitchProReportDescriptor[] = {
    0x05, 0x01, 0x15, 0x00, 0x09, 0x04, 0xa1, 0x01, 0x85, 0x30, 0x05, 0x01,
    0x05, 0x09, 0x19, 0x01, 0x29, 0x0a, 0x15, 0x00, 0x25, 0x01, 0x75, 0x01,
    0x95, 0x0a, 0x55, 0x00, 0x65, 0x00, 0x81, 0x02, 0x05, 0x09, 0x19, 0x0b,
    0x29, 0x0e, 0x15, 0x00, 0x25, 0x01, 0x75, 0x01, 0x95, 0x04, 0x81, 0x02,
    0x75, 0x01, 0x95, 0x02, 0x81, 0x03, 0x0b, 0x01, 0x00, 0x01, 0x00, 0xa1,
    0x00, 0x0b, 0x30, 0x00, 0x01, 0x00, 0x0b, 0x31, 0x00, 0x01, 0x00, 0x0b,
    0x32, 0x00, 0x01, 0x00, 0x0b, 0x35, 0x00, 0x01, 0x00, 0x15, 0x00, 0x27,
    0xff, 0xff, 0x00, 0x00, 0x75, 0x10, 0x95, 0x04, 0x81, 0x02, 0xc0, 0x0b,
    0x39, 0x00, 0x01, 0x00, 0x15, 0x00, 0x25, 0x07, 0x35, 0x00, 0x46, 0x3b,
    0x01, 0x65, 0x14, 0x75, 0x04, 0x95, 0x01, 0x81, 0x42, 0x05, 0x09, 0x19,
    0x0f, 0x29, 0x12, 0x15, 0x00, 0x25, 0x01, 0x75, 0x01, 0x95, 0x04, 0x81,
    0x02, 0x75, 0x08, 0x95, 0x34, 0x81, 0x03, 0x06, 0x00, 0xff, 0x85, 0x21,
    0x09, 0x01, 0x75, 0x08, 0x95, 0x3f, 0x81, 0x03, 0x85, 0x81, 0x09, 0x02,
    0x75, 0x08, 0x95, 0x3f, 0x81, 0x03, 0x85, 0x01, 0x09, 0x03, 0x75, 0x08,
    0x95, 0x3f, 0x91, 0x83, 0x85, 0x10, 0x09, 0x04, 0x75, 0x08, 0x95, 0x3f,
    0x91, 0x83, 0x85, 0x80, 0x09, 0x05, 0x75, 0x08, 0x95, 0x3f, 0x91, 0x83,
    0x85, 0x82, 0x09, 0x06, 0x75, 0x08, 0x95, 0x3f, 0x91, 0x83, 0xc0,
};

struct BleState {
  uint8_t bytes[sizeof BleNeutralState];
  uint32_t receivedAtMs;
  bool valid;
};

struct UsbOutputCommand {
  uint8_t reportId;
  uint8_t length;
  uint8_t payload[SwitchProPayloadLength];
};

struct PendingReply {
  uint8_t reportId;
  uint8_t payload[SwitchProPayloadLength];
  bool ready;
};

constexpr uint8_t CommandQueueCapacity = 8;
constexpr uint8_t DiagnosticQueueCapacity = 128;
constexpr uint8_t DiagnosticLength = 20;
Adafruit_USBD_HID switchPro;
BLEService bridgeService("cc7c0001-8f5d-4b8a-9f6e-4f5f4343544c");
BLECharacteristic controllerState("cc7c0002-8f5d-4b8a-9f6e-4f5f4343544c");
BLECharacteristic controllerDiagnostics("cc7c0003-8f5d-4b8a-9f6e-4f5f4343544c");
uint8_t configurationDescriptor[512];
BleState bleState = {{0, 0, 8, 128, 128, 128, 128, 0}, 0, false};
UsbOutputCommand commandQueue[CommandQueueCapacity];
volatile uint8_t commandHead = 0;
volatile uint8_t commandTail = 0;
volatile uint32_t droppedCommandCount = 0;
uint32_t unsupportedCommandCount = 0;
uint8_t diagnosticQueue[DiagnosticQueueCapacity][DiagnosticLength];
volatile uint8_t diagnosticHead = 0;
volatile uint8_t diagnosticTail = 0;
volatile uint16_t diagnosticSequence = 0;
volatile uint32_t droppedDiagnosticCount = 0;
PendingReply pendingReply = {0, {0}, false};
uint8_t selectedReportMode = 0;
uint8_t inputTimer = 0;
uint32_t lastReportAt = 0;
uint32_t lastDiagnosticAt = 0;

void neutralize() {
  taskENTER_CRITICAL();
  memcpy(bleState.bytes, BleNeutralState, sizeof bleState.bytes);
  bleState.valid = false;
  taskEXIT_CRITICAL();
}

BleState readBleState(uint32_t now) {
  BleState snapshot;
  taskENTER_CRITICAL();
  snapshot = bleState;
  taskEXIT_CRITICAL();
  if (!snapshot.valid || now - snapshot.receivedAtMs > StateTimeoutMs) {
    memcpy(snapshot.bytes, BleNeutralState, sizeof snapshot.bytes);
    snapshot.valid = false;
  }
  return snapshot;
}

void stateWritten(uint16_t connectionHandle, BLECharacteristic *characteristic,
                  uint8_t *data, uint16_t length) {
  (void)connectionHandle;
  (void)characteristic;
  if (length != sizeof bleState.bytes) return;
  taskENTER_CRITICAL();
  memcpy(bleState.bytes, data, sizeof bleState.bytes);
  bleState.receivedAtMs = millis();
  bleState.valid = true;
  taskEXIT_CRITICAL();
}

void disconnected(uint16_t connectionHandle, uint8_t reason) {
  (void)connectionHandle;
  (void)reason;
  neutralize();
}

void startBluetooth() {
  Bluefruit.begin(1, 0);
  Bluefruit.setName("ConsoleControl Proof");
  Bluefruit.Periph.setDisconnectCallback(disconnected);
  bridgeService.begin();
  controllerState.setProperties(CHR_PROPS_WRITE | CHR_PROPS_WRITE_WO_RESP);
  controllerState.setPermission(SECMODE_NO_ACCESS, SECMODE_OPEN);
  controllerState.setFixedLen(sizeof bleState.bytes);
  controllerState.setWriteCallback(stateWritten);
  controllerState.begin();
  controllerDiagnostics.setProperties(CHR_PROPS_READ | CHR_PROPS_NOTIFY);
  controllerDiagnostics.setPermission(SECMODE_OPEN, SECMODE_NO_ACCESS);
  controllerDiagnostics.setFixedLen(DiagnosticLength);
  controllerDiagnostics.begin();
  uint8_t initialDiagnostic[DiagnosticLength] = {1};
  controllerDiagnostics.write(initialDiagnostic, sizeof initialDiagnostic);
  Bluefruit.Advertising.addFlags(BLE_GAP_ADV_FLAGS_LE_ONLY_GENERAL_DISC_MODE);
  Bluefruit.Advertising.addTxPower();
  Bluefruit.Advertising.addService(bridgeService);
  Bluefruit.ScanResponse.addName();
  Bluefruit.Advertising.restartOnDisconnect(true);
  Bluefruit.Advertising.setInterval(32, 244);
  Bluefruit.Advertising.setFastTimeout(30);
  Bluefruit.Advertising.start(0);
}

void recordDiagnostic(uint8_t kind, uint8_t reportId, uint8_t opcode,
                      uint8_t const *data, uint8_t length) {
  taskENTER_CRITICAL();
  uint8_t nextTail = (uint8_t)((diagnosticTail + 1) % DiagnosticQueueCapacity);
  if (nextTail == diagnosticHead) {
    droppedDiagnosticCount++;
    taskEXIT_CRITICAL();
    return;
  }
  uint8_t *event = diagnosticQueue[diagnosticTail];
  memset(event, 0, DiagnosticLength);
  uint16_t sequence = diagnosticSequence++;
  event[0] = 1;
  event[1] = kind;
  event[2] = (uint8_t)sequence;
  event[3] = (uint8_t)(sequence >> 8);
  event[4] = reportId;
  event[5] = opcode;
  event[6] = length;
  event[7] = selectedReportMode;
  uint8_t copied = length > 8 ? 8 : length;
  if (data != nullptr) memcpy(&event[8], data, copied);
  event[16] = (uint8_t)droppedCommandCount;
  event[17] = (uint8_t)droppedDiagnosticCount;
  event[18] = (uint8_t)unsupportedCommandCount;
  event[19] = pendingReply.ready ? 1 : 0;
  diagnosticTail = nextTail;
  taskEXIT_CRITICAL();
}

void drainDiagnostics() {
  uint32_t now = millis();
  if (now - lastDiagnosticAt < 10) return;
  if (!controllerDiagnostics.notifyEnabled()) return;
  taskENTER_CRITICAL();
  if (diagnosticHead == diagnosticTail) {
    taskEXIT_CRITICAL();
    return;
  }
  uint8_t event[DiagnosticLength];
  memcpy(event, diagnosticQueue[diagnosticHead], sizeof event);
  taskEXIT_CRITICAL();
  controllerDiagnostics.write(event, sizeof event);
  if (!controllerDiagnostics.notify(event, sizeof event)) return;
  lastDiagnosticAt = now;
  taskENTER_CRITICAL();
  diagnosticHead = (uint8_t)((diagnosticHead + 1) % DiagnosticQueueCapacity);
  taskEXIT_CRITICAL();
}

void enqueueOutput(uint8_t reportId, hid_report_type_t reportType,
                   uint8_t const *buffer, uint16_t length) {
  if (reportType != HID_REPORT_TYPE_OUTPUT || length == 0) return;
  uint8_t normalizedId = reportId;
  uint8_t const *payload = buffer;
  uint16_t payloadLength = length;
  if (normalizedId == 0) {
    normalizedId = buffer[0];
    payload = buffer + 1;
    payloadLength--;
  }
  if (payloadLength > SwitchProPayloadLength) payloadLength = SwitchProPayloadLength;

  taskENTER_CRITICAL();
  uint8_t nextTail = (uint8_t)((commandTail + 1) % CommandQueueCapacity);
  if (nextTail == commandHead) {
    droppedCommandCount++;
    taskEXIT_CRITICAL();
    return;
  }
  UsbOutputCommand &command = commandQueue[commandTail];
  command.reportId = normalizedId;
  command.length = (uint8_t)payloadLength;
  memcpy(command.payload, payload, payloadLength);
  commandTail = nextTail;
  taskEXIT_CRITICAL();
  uint8_t opcode = normalizedId == 0x01 && payloadLength > 9
                       ? payload[9]
                       : (payloadLength > 0 ? payload[0] : 0);
  uint8_t const *arguments = normalizedId == 0x01 && payloadLength > 10
                                 ? payload + 10
                                 : (payloadLength > 1 ? payload + 1 : nullptr);
  uint8_t argumentLength = normalizedId == 0x01 && payloadLength > 10
                               ? (uint8_t)(payloadLength - 10)
                               : (payloadLength > 1 ? (uint8_t)(payloadLength - 1) : 0);
  if (normalizedId == 0x80 || normalizedId == 0x01)
    recordDiagnostic(1, normalizedId, opcode, arguments, argumentLength);
}

bool dequeueOutput(UsbOutputCommand &command) {
  taskENTER_CRITICAL();
  if (commandHead == commandTail) {
    taskEXIT_CRITICAL();
    return false;
  }
  command = commandQueue[commandHead];
  commandHead = (uint8_t)((commandHead + 1) % CommandQueueCapacity);
  taskEXIT_CRITICAL();
  return true;
}

void resetUsbSession() {
  taskENTER_CRITICAL();
  commandHead = commandTail = 0;
  taskEXIT_CRITICAL();
  pendingReply.ready = false;
  selectedReportMode = 0;
  inputTimer = 0;
}

void beginReply(uint8_t reportId) {
  pendingReply.reportId = reportId;
  memset(pendingReply.payload, 0, sizeof pendingReply.payload);
  pendingReply.ready = true;
}

uint16_t expandAxis(uint8_t value) {
  if (value == 128) return 2048;
  if (value < 128) return (uint16_t)(((uint32_t)value * 2048) / 128);
  return (uint16_t)(2048 + ((uint32_t)(value - 128) * 2047) / 127);
}

void packStick(uint8_t *output, uint8_t x, uint8_t y) {
  uint16_t packedX = expandAxis(x);
  uint16_t packedY = expandAxis(y);
  output[0] = (uint8_t)packedX;
  output[1] = (uint8_t)((packedX >> 8) | (packedY << 4));
  output[2] = (uint8_t)(packedY >> 4);
}

void fillInputPrefix(uint8_t *output, BleState const &state) {
  memset(output, 0, SwitchProPayloadLength);
  output[0] = inputTimer++;
  output[1] = 0x91;
  uint16_t buttons = (uint16_t)state.bytes[0] | ((uint16_t)state.bytes[1] << 8);
  if (buttons & 0x0001) output[2] |= 0x01;
  if (buttons & 0x0008) output[2] |= 0x02;
  if (buttons & 0x0002) output[2] |= 0x04;
  if (buttons & 0x0004) output[2] |= 0x08;
  if (buttons & 0x0020) output[2] |= 0x40;
  if (buttons & 0x0080) output[2] |= 0x80;
  if (buttons & 0x0100) output[3] |= 0x01;
  if (buttons & 0x0200) output[3] |= 0x02;
  if (buttons & 0x0800) output[3] |= 0x04;
  if (buttons & 0x0400) output[3] |= 0x08;
  if (buttons & 0x1000) output[3] |= 0x10;
  if (buttons & 0x2000) output[3] |= 0x20;
  if (buttons & 0x0010) output[4] |= 0x40;
  if (buttons & 0x0040) output[4] |= 0x80;
  switch (state.bytes[2] & 0x0f) {
  case 0: output[4] |= 0x02; break;
  case 1: output[4] |= 0x02 | 0x04; break;
  case 2: output[4] |= 0x04; break;
  case 3: output[4] |= 0x04 | 0x01; break;
  case 4: output[4] |= 0x01; break;
  case 5: output[4] |= 0x01 | 0x08; break;
  case 6: output[4] |= 0x08; break;
  case 7: output[4] |= 0x08 | 0x02; break;
  default: break;
  }
  packStick(&output[5], state.bytes[3], state.bytes[4]);
  packStick(&output[8], state.bytes[5], state.bytes[6]);
  output[11] = 0x09;
}

void packCalibration(uint8_t *output, uint16_t const values[6]) {
  output[0] = (uint8_t)values[0];
  output[1] = (uint8_t)(((values[1] & 0x0f) << 4) | (values[0] >> 8));
  output[2] = (uint8_t)(values[1] >> 4);
  output[3] = (uint8_t)values[2];
  output[4] = (uint8_t)(((values[3] & 0x0f) << 4) | (values[2] >> 8));
  output[5] = (uint8_t)(values[3] >> 4);
  output[6] = (uint8_t)values[4];
  output[7] = (uint8_t)(((values[5] & 0x0f) << 4) | (values[4] >> 8));
  output[8] = (uint8_t)(values[5] >> 4);
}

uint8_t virtualSpiByte(uint32_t address) {
  static const uint8_t ImuCalibration[24] = {
      0, 0, 0, 0, 0, 0, 0, 0x40, 0, 0x40, 0, 0x40,
      0, 0, 0, 0, 0, 0, 0x3b, 0x34, 0x3b, 0x34, 0x3b, 0x34};
  static const uint8_t Colors[13] = {
      0x32, 0x32, 0x32, 0xe6, 0xe6, 0xe6, 0x32,
      0x32, 0x32, 0x32, 0x32, 0x32, 0xff};
  static const uint8_t Parameters[24] = {
      0x50, 0xfd, 0, 0, 0xc6, 0x0f, 0x0f, 0x30, 0x61, 0x96, 0x30, 0xf3,
      0xd4, 0x14, 0x54, 0x41, 0x15, 0x54, 0xc7, 0x79, 0x9c, 0x33, 0x36, 0x63};
  static uint8_t stickCalibration[18];
  static bool ready = false;
  if (!ready) {
    const uint16_t left[6] = {1400, 1400, 2048, 2048, 1400, 1400};
    const uint16_t right[6] = {2048, 2048, 1400, 1400, 1400, 1400};
    packCalibration(&stickCalibration[0], left);
    packCalibration(&stickCalibration[9], right);
    ready = true;
  }
  if (address >= 0x6020 && address < 0x6020 + sizeof ImuCalibration)
    return ImuCalibration[address - 0x6020];
  if (address >= 0x603d && address < 0x603d + sizeof stickCalibration)
    return stickCalibration[address - 0x603d];
  if (address >= 0x6050 && address < 0x6050 + sizeof Colors)
    return Colors[address - 0x6050];
  if (address >= 0x6080 && address < 0x6080 + sizeof Parameters)
    return Parameters[address - 0x6080];
  if (address >= 0x6098 && address < 0x6098 + 18)
    return Parameters[6 + address - 0x6098];
  return 0xff;
}

void buildUsbReply(uint8_t command) {
  if (command == 0x01) {
    beginReply(0x81);
    pendingReply.payload[0] = 0x01;
    pendingReply.payload[1] = 0x00;
    pendingReply.payload[2] = 0x03;
    for (size_t i = 0; i < sizeof ControllerMac; i++)
      pendingReply.payload[3 + i] = ControllerMac[sizeof ControllerMac - 1 - i];
  } else if (command == 0x02 || command == 0x03) {
    beginReply(0x81);
    pendingReply.payload[0] = command;
  } else if (command == 0x04 || command == 0x05) {
    // No reply in the documented USB protocol.
  } else if (command == 0x06) {
    resetUsbSession();
  } else {
    unsupportedCommandCount++;
  }
}

void buildPairingReply(uint8_t const *arguments, uint8_t length) {
  uint8_t type = length > 0 ? arguments[0] : 0;
  pendingReply.payload[12] = 0x81;
  pendingReply.payload[14] = type;
  if (type == 0x01) {
    for (size_t i = 0; i < sizeof ControllerMac; i++)
      pendingReply.payload[15 + i] = ControllerMac[sizeof ControllerMac - 1 - i];
    pendingReply.payload[21] = 0x00;
    pendingReply.payload[22] = 0x25;
    pendingReply.payload[23] = 0x08;
    static const uint8_t Name[] = {'P', 'r', 'o', ' ', 'C', 'o', 'n',
                                   't', 'r', 'o', 'l', 'l', 'e', 'r'};
    memcpy(&pendingReply.payload[24], Name, sizeof Name);
    pendingReply.payload[43] = 0x68;
  } else if (type == 0x02) {
    memset(&pendingReply.payload[15], 0xaa, 16);
  } else if (type == 0x03 || type == 0x04) {
    // Switch 2 uses type 0x04 to finalize an existing wired pairing.
    // A Pro Controller answers it with the same type-0x03 completion body.
    pendingReply.payload[14] = 0x03;
  } else {
    pendingReply.payload[12] = 0x00;
  }
}

void buildSubcommandReply(uint8_t subcommand, uint8_t const *arguments,
                          uint8_t length, BleState const &state) {
  beginReply(0x21);
  fillInputPrefix(pendingReply.payload, state);
  pendingReply.payload[13] = subcommand;
  switch (subcommand) {
  case 0x00: pendingReply.payload[12] = 0x80; break;
  case 0x01: buildPairingReply(arguments, length); break;
  case 0x02:
    pendingReply.payload[12] = 0x82;
    pendingReply.payload[14] = 0x03;
    pendingReply.payload[15] = 0x48;
    pendingReply.payload[16] = 0x03;
    pendingReply.payload[17] = 0x02;
    memcpy(&pendingReply.payload[18], ControllerMac, sizeof ControllerMac);
    pendingReply.payload[24] = 0x01;
    pendingReply.payload[25] = 0x01;
    break;
  case 0x03:
    pendingReply.payload[12] = 0x80;
    if (length > 0 && arguments[0] == 0x30) selectedReportMode = 0x30;
    break;
  case 0x04: pendingReply.payload[12] = 0x83; break;
  case 0x05:
    pendingReply.payload[12] = 0x80;
    pendingReply.payload[14] = 0x00;
    break;
  case 0x10: {
    if (length < 5) { pendingReply.payload[12] = 0; break; }
    uint32_t address = (uint32_t)arguments[0] | ((uint32_t)arguments[1] << 8) |
                       ((uint32_t)arguments[2] << 16) | ((uint32_t)arguments[3] << 24);
    uint8_t count = arguments[4] > 0x1d ? 0x1d : arguments[4];
    pendingReply.payload[12] = 0x90;
    memcpy(&pendingReply.payload[14], arguments, 4);
    pendingReply.payload[18] = count;
    for (uint8_t i = 0; i < count; i++)
      pendingReply.payload[19 + i] = virtualSpiByte(address + i);
    break;
  }
  case 0x11:
  case 0x12:
    pendingReply.payload[12] = 0x80;
    pendingReply.payload[14] = 0x01;
    break;
  case 0x21: pendingReply.payload[12] = 0xa0; break;
  case 0x06: case 0x07: case 0x08: case 0x20: case 0x22: case 0x24:
  case 0x25: case 0x28: case 0x30: case 0x31: case 0x38: case 0x40:
  case 0x41: case 0x42: case 0x43: case 0x48:
    pendingReply.payload[12] = 0x80;
    break;
  case 0x50:
    pendingReply.payload[12] = 0xd0;
    pendingReply.payload[14] = 0x50;
    pendingReply.payload[15] = 0x00;
    break;
  default:
    pendingReply.ready = false;
    unsupportedCommandCount++;
    break;
  }
}

void processOutput(UsbOutputCommand const &command, BleState const &state) {
  if (command.reportId == 0x80 && command.length >= 1) {
    buildUsbReply(command.payload[0]);
  } else if (command.reportId == 0x01 && command.length >= 10) {
    buildSubcommandReply(command.payload[9], &command.payload[10],
                         (uint8_t)(command.length - 10), state);
  } else if (command.reportId != 0x10 && command.reportId != 0x82) {
    unsupportedCommandCount++;
  }
}

void pollUsb(uint32_t now) {
  if (!switchPro.ready()) return;
  if (pendingReply.ready) {
    if (switchPro.sendReport(pendingReply.reportId, pendingReply.payload,
                             sizeof pendingReply.payload)) {
      recordDiagnostic(2, pendingReply.reportId,
                       pendingReply.reportId == 0x21 ? pendingReply.payload[13]
                                                     : pendingReply.payload[0],
                       nullptr, 0);
      pendingReply.ready = false;
    }
    return;
  }
  BleState state = readBleState(now);
  UsbOutputCommand command;
  if (dequeueOutput(command)) {
    processOutput(command, state);
    return;
  }
  if (selectedReportMode == 0x30 && now - lastReportAt >= ReportIntervalMs) {
    uint8_t report[SwitchProPayloadLength];
    fillInputPrefix(report, state);
    if (switchPro.sendReport(0x30, report, sizeof report)) lastReportAt = now;
  }
}

} // namespace

void setup() {
  if (!TinyUSBDevice.isInitialized()) TinyUSBDevice.begin(0);
  delay(100);
  startBluetooth();
  USBDevice.detach();
  delay(30);
  USBDevice.clearConfiguration();
  USBDevice.setConfigurationBuffer(configurationDescriptor, sizeof configurationDescriptor);
  USBDevice.setID(NintendoVendorId, ProControllerProductId);
  USBDevice.setVersion(0x0200);
  USBDevice.setDeviceVersion(0x0210);
  USBDevice.setManufacturerDescriptor("Nintendo Co., Ltd.");
  USBDevice.setProductDescriptor("Pro Controller");
  USBDevice.setSerialDescriptor("000000000001");
  USBDevice.setConfigurationAttribute(0x80 | 0x20);
  switchPro.enableOutEndpoint(true);
  switchPro.setReportCallback(nullptr, enqueueOutput);
  switchPro.setReportDescriptor(SwitchProReportDescriptor, sizeof SwitchProReportDescriptor);
  switchPro.setPollInterval(1);
  switchPro.begin();
  resetUsbSession();
  USBDevice.attach();
}

void loop() {
  pollUsb(millis());
  drainDiagnostics();
  delay(1);
}
