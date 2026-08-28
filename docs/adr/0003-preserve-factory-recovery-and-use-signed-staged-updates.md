# Preserve factory recovery and use signed staged updates

Controller-bridge updates use signed packages, inactive-image staging, pending boot, health confirmation, and rollback. The project preserves the factory UF2 recovery bootloader unless hardware testing proves that it cannot support these guarantees. This costs flash space and requires one exact-board proof, but it prevents routine BLE or USB updates from turning an interrupted transfer into an unrecoverable board.
