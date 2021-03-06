# Replace ELM327 BT V1.5 HW: V01_M_V2.3 adapter firmware (or equivalent)

This chapter describes how to replace the ELM327 BT V1.5 HW: V01_M_V2.3 adapter PIC18F25K80 firmware and YC1021 BT settings.  

### Requirements:

* [ELM327 BT V1.5 HW: V01_M_V2.3 adapter](https://www.aliexpress.com/item/New-OBDII-Diagnostic-Interface-Super-ELM327-Bluetooth-V1-5-Hardware-PIC18F25K80-Chip-1PCB-Board-ELM-327/32846998449.html) (or equivalent)
* EZP2010 Eeprom programmer with SOIC8 (150mil) programming clip
* PicKit 3/4 (to program the PIC18F25K80)

### ELM327 BT V1.5 HW: V01_M_V2.3 board connections:

[![ELM327 BT V1.5 HW: V01_M_V2.3 board programming connections big](elm327_BT_annotated_24c32_and_pic18f25k80_prog_connections_Small.png "ELM327 BT V1.5 HW: V01_M_V2.3 board programming connections")](elm327_BT_annotated_24c32_and_pic18f25k80_prog_connections_Big.png)

## Step1: Program the YC1021 BT settings
* Connect your EZP2010 Eeprom programmer clip on to the SOIC8 (150mil) 24C32 eeprom chip, take note of orientation (red wire of clipon goes to red annotated 24C32 pin1)
* Do a full read with eeprom powered from programmer (do not apply power from obd side), it make take a few read tries to get the full dump correctly, if the first 0x80 bytes do not contain any 0xff your read is correct.
* Edit the dump to your liking
  * BT address: 0xF93 (6 bytes, normally dont touch)
  * BLE address: 0xF99 (6 bytes, normally dont touch)
  * PinCode: 0xF9F, size + pinchars (max 15 chars)
  * BT 2.x name: 0xFAF, size + string (max 32 chars)
  * BLE name: 0xFD0, size + string (max 24 chars)
  * BaudRate: 0xFF0 (uint16_t, 2 bytes); default value: 0x04E2 is 38400 baud (for `default` PIC firmware), change to 0x01A1 for 115200 baud (for `yc1021` firmware)
* Write the changed binary back to the 24C32 eeprom (again powered from programmer, not from obd side).  

## Step2: Program the PIC18F25K80
* Connect your PicKit 3/4 to MCLR, PGD, PGC, GND (Vss) and 5V (Vcc) (take care, do not apply power from PicKit 3/4)
* Power the Elm327 adapter (from obd side)
* From subdirectory `CanAdapterElm` select either `default` firmware when using baudrate 38400 (take care slightly misallocated led usage) or `yc1021` (recommended) when using 115200 baudrate, always use `CanAdapterElm.X.production.unified.hex` for this first upload
* Flash the selected firmware to the PIC18F25K80

## Step3: Testing
* Power the Elm327 adapter
* Connect to XXYYZZ BT device and pair with it (standard pincode: 1234)
* Connect to the COM port assigned to your BT device
* When sending strings to the adapter you should at least get an echo from the adapter, otherwise there is a problem with the connections.  
You could test reading the ignition pin with the following command (hex values):  
`82 F1 F1 FE FE 60`  
The response is (additionally to the echo):  
`82 F1 F1 FE <state> <checksum>` with state bit 0 set to 1 if ignition is on.  
