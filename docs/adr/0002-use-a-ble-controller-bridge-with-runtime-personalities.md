# Use a BLE controller bridge with runtime personalities

The stock NanoKVM-USB cannot present a Switch controller, and the nRF52840 has only one USB connection. ConsoleControl therefore connects the nRF52840 to the console over USB and sends canonical controller state from the daemon over Bluetooth LE. The daemon uploads bounded controller personalities into bridge RAM so simple HID controllers can change without firmware writes. Protocols that require active handshakes still require a compiled firmware handler.
