﻿using System;
using System.Diagnostics;
using System.IO.Ports;
using System.Threading;

namespace EdiabasLib
{
    public class EdInterfaceObd : EdInterfaceBase
    {
        public delegate bool InterfaceConnectDelegate();
        public delegate bool InterfaceDisconnectDelegate();
        public delegate bool InterfaceSetConfigDelegate(int baudRate, Parity parity);
        public delegate bool InterfaceSetDtrDelegate(bool dtr);
        public delegate bool InterfaceSetRtsDelegate(bool rts);
        public delegate bool InterfaceGetDsrDelegate(out bool dsr);
        public delegate bool SendDataDelegate(byte[] sendData, int length);
        public delegate bool ReceiveDataDelegate(byte[] receiveData, int offset, int length, int timeout, int timeoutTelEnd, bool logResponse);
        protected delegate EdiabasNet.ErrorCodes TransmitDelegate(byte[] sendData, int sendDataLength, ref byte[] receiveData, out int receiveLength);

        private bool disposed = false;
        private static readonly byte[] byteArray0 = new byte[0];
        protected static SerialPort serialPort = new SerialPort();

        protected string comPort = string.Empty;
        protected bool connected = false;
        protected const int echoTimeout = 100;
        protected InterfaceConnectDelegate interfaceConnectFunc = null;
        protected InterfaceDisconnectDelegate interfaceDisconnectFunc = null;
        protected InterfaceSetConfigDelegate interfaceSetConfigFunc = null;
        protected InterfaceSetDtrDelegate interfaceSetDtrFunc = null;
        protected InterfaceSetRtsDelegate interfaceSetRtsFunc = null;
        protected InterfaceGetDsrDelegate interfaceGetDsrFunc = null;
        protected SendDataDelegate sendDataFunc = null;
        protected ReceiveDataDelegate receiveDataFunc = null;
        protected Stopwatch stopWatch = new Stopwatch();
        protected byte[] keyBytes = byteArray0;
        protected byte[] state = new byte[2];
        protected byte[] sendBuffer = new byte[260];
        protected byte[] recBuffer = new byte[260];
        protected byte[] iso9141Buffer = new byte[256];
        protected byte[] iso9141BlockBuffer = new byte[1];
        protected bool ecuConnected;
        protected byte blockCounter;

        protected TransmitDelegate parTransmitFunc;
        protected int parTimeoutStd = 0;
        protected int parTimeoutTelEnd = 0;
        protected int parTimeoutNR = 0;
        protected int parRetryNR = 0;
        protected byte parWakeAddress = 0;

        public override EdiabasNet Ediabas
        {
            get
            {
                return base.Ediabas;
            }
            set
            {
                base.Ediabas = value;

                string prop = ediabas.GetConfigProperty("ObdComPort");
                if (prop != null)
                {
                    comPort = prop;
                }
            }
        }

        public override UInt32[] CommParameter
        {
            get
            {
                return base.CommParameter;
            }
            set
            {
                commParameter = value;

                this.parTransmitFunc = null;
                this.parTimeoutStd = 0;
                this.parTimeoutTelEnd = 0;
                this.parTimeoutNR = 0;
                this.parRetryNR = 0;
                this.parWakeAddress = 0;
                this.keyBytes = byteArray0;
                this.ecuConnected = false;

                if (commParameter == null)
                {   // clear parameter
                    return;
                }
                if (commParameter.Length < 1)
                {
                    ediabas.SetError(EdiabasNet.ErrorCodes.EDIABAS_IFH_0041);
                    return;
                }

                ediabas.LogData(EdiabasNet.ED_LOG_LEVEL.IFH, commParameter, 0, commParameter.Length, string.Format("{0} CommParameter Port={1}", InterfaceName, comPort));

                int baudRate;
                Parity parity;
                bool stateDtr = false;
                bool stateRts = false;
                switch (commParameter[0])
                {
                    case 0x0002:    // Concept 2 ISO 9141
                        if (commParameter.Length < 7)
                        {
                            ediabas.SetError(EdiabasNet.ErrorCodes.EDIABAS_IFH_0041);
                            return;
                        }
                        if (adapterEcho)
                        {   // only with ADS adapter
                            ediabas.SetError(EdiabasNet.ErrorCodes.EDIABAS_IFH_0041);
                            return;
                        }
                        commAnswerLen = new short[] { 1, 0 };
                        baudRate = 10400;
                        parity = Parity.None;
                        stateDtr = false;
                        stateRts = false;
                        this.parTransmitFunc = TransIso9141;
                        this.parWakeAddress = (byte)commParameter[2];
                        this.parTimeoutStd = (int)commParameter[5];
                        this.parTimeoutTelEnd = (int)commParameter[7];
                        break;

                    case 0x0001:    // Concept 1
                    case 0x0006:    // DS2
                        if (commParameter.Length < 7)
                        {
                            ediabas.SetError(EdiabasNet.ErrorCodes.EDIABAS_IFH_0041);
                            return;
                        }
                        if (commParameter.Length >= 10 && commParameter[33] != 1)
                        {   // not checksum calculated by interface
                            ediabas.SetError(EdiabasNet.ErrorCodes.EDIABAS_IFH_0041);
                            return;
                        }
                        commAnswerLen = new short[] { -1, 0 };
                        baudRate = (int)commParameter[1];
                        parity = Parity.Even;
                        stateDtr = false;
                        stateRts = false;
                        this.parTransmitFunc = TransDS2;
                        this.parTimeoutStd = (int)commParameter[5];
                        this.parTimeoutTelEnd = (int)commParameter[7];
                        break;

                    case 0x010D:    // KWP2000*
                        if (commParameter.Length < 7)
                        {
                            ediabas.SetError(EdiabasNet.ErrorCodes.EDIABAS_IFH_0041);
                            return;
                        }
                        if (commParameter.Length >= 34 && commParameter[33] != 1)
                        {   // not checksum calculated by interface
                            ediabas.SetError(EdiabasNet.ErrorCodes.EDIABAS_IFH_0041);
                            return;
                        }
                        commAnswerLen = new short[] { 0, 0 };
                        baudRate = (int)commParameter[1];
                        parity = Parity.Even;
                        stateDtr = false;
                        stateRts = false;
                        this.parTransmitFunc = TransKwp2000S;
                        this.parTimeoutStd = (int)commParameter[2];
                        this.parTimeoutTelEnd = (int)commParameter[4];
                        this.parTimeoutNR = (int)commParameter[7];
                        this.parRetryNR = (int)commParameter[6];
                        break;

                    case 0x010F:    // BMW-FAST
                        if (commParameter.Length < 7)
                        {
                            ediabas.SetError(EdiabasNet.ErrorCodes.EDIABAS_IFH_0041);
                            return;
                        }
                        if (commParameter.Length >= 8 && commParameter[7] != 1)
                        {   // not checksum calculated by interface
                            ediabas.SetError(EdiabasNet.ErrorCodes.EDIABAS_IFH_0041);
                            return;
                        }
                        commAnswerLen = new short[] { 0, 0 };
                        baudRate = (int)commParameter[1];
                        parity = Parity.None;
                        stateDtr = true;
                        stateRts = false;
                        this.parTransmitFunc = TransBmwFast;
                        this.parTimeoutStd = (int)commParameter[2];
                        this.parTimeoutTelEnd = (int)commParameter[4];
                        this.parTimeoutNR = (int)commParameter[6];
                        this.parRetryNR = (int)commParameter[5];
                        break;

                    case 0x0110:    // D-CAN
                        if (commParameter.Length < 30)
                        {
                            ediabas.SetError(EdiabasNet.ErrorCodes.EDIABAS_IFH_0041);
                            return;
                        }
                        commAnswerLen = new short[] { 0, 0 };
                        baudRate = 115200;
                        parity = Parity.None;
                        stateDtr = true;
                        stateRts = false;
                        this.parTransmitFunc = TransBmwFast;
                        this.parTimeoutStd = (int)commParameter[7];
                        this.parTimeoutTelEnd = 10;
                        this.parTimeoutNR = (int)commParameter[9];
                        this.parRetryNR = (int)commParameter[10];
                        break;

                    default:
                        ediabas.SetError(EdiabasNet.ErrorCodes.EDIABAS_IFH_0014);
                        return;
                }

                if (interfaceSetConfigFunc != null)
                {
                    if (!interfaceSetConfigFunc(baudRate, parity))
                    {
                        ediabas.SetError(EdiabasNet.ErrorCodes.EDIABAS_IFH_0041);
                        return;
                    }
                    if (interfaceSetDtrFunc != null)
                    {
                        if (!interfaceSetDtrFunc(stateDtr))
                        {
                            ediabas.SetError(EdiabasNet.ErrorCodes.EDIABAS_IFH_0041);
                            return;
                        }
                    }
                    if (interfaceSetRtsFunc != null)
                    {
                        if (!interfaceSetRtsFunc(stateRts))
                        {
                            ediabas.SetError(EdiabasNet.ErrorCodes.EDIABAS_IFH_0041);
                            return;
                        }
                    }
                }
                else
                {
                    if (serialPort.BaudRate != baudRate)
                    {
                        serialPort.BaudRate = baudRate;
                    }
                    if (serialPort.Parity != parity)
                    {
                        serialPort.Parity = parity;
                    }
                    if (serialPort.DtrEnable != stateDtr)
                    {
                        serialPort.DtrEnable = stateDtr;
                    }
                    if (serialPort.RtsEnable != stateRts)
                    {
                        serialPort.RtsEnable = stateRts;
                    }
                }
            }
        }

        public override string InterfaceType
        {
            get
            {
                return "OBD";
            }
        }

        public override UInt32 InterfaceVersion
        {
            get
            {
                return 209;
            }
        }

        public override string InterfaceName
        {
            get
            {
                return "STD:OBD";
            }
        }

        public override byte[] KeyBytes
        {
            get
            {
                return keyBytes;
            }
        }

        public override byte[] State
        {
            get
            {
                state[0] = 0x00;
                state[1] = (byte)(getDsrState() ? 0x00 : 0x30);
                return state;
            }
        }

        public override UInt32 BatteryVoltage
        {
            get
            {
                return (UInt32)(getDsrState() ? 12000 : 0);
            }
        }

        public override UInt32 IgnitionVoltage
        {
            get
            {
                return (UInt32)(getDsrState() ? 12000 : 0);
            }
        }

        public override bool Connected
        {
            get
            {
                if (interfaceConnectFunc != null)
                {
                    return connected;
                }
                return serialPort.IsOpen;
            }
        }

        static EdInterfaceObd()
        {
#if WindowsCE
            interfaceMutex = new Mutex(false);
#else
            interfaceMutex = new Mutex(false, "EdiabasLib_InterfaceObd");
#endif
        }

        public EdInterfaceObd()
        {
        }

        ~EdInterfaceObd()
        {
            Dispose(false);
        }

        public override bool IsValidInterfaceName(string name)
        {
            if (string.Compare(name, "STD:OBD", StringComparison.OrdinalIgnoreCase) == 0)
            {
                return true;
            }
            if (string.Compare(name, "STD:OMITEC", StringComparison.OrdinalIgnoreCase) == 0)
            {
                return true;
            }
            return false;
        }

        public override bool InterfaceConnect()
        {
            if (!base.InterfaceConnect())
            {
                ediabas.SetError(EdiabasNet.ErrorCodes.EDIABAS_IFH_0018);
                return false;
            }
            if (interfaceConnectFunc != null)
            {
                connected = interfaceConnectFunc();
                if (!connected)
                {
                    ediabas.SetError(EdiabasNet.ErrorCodes.EDIABAS_IFH_0018);
                }
                return connected;
            }

            if (comPort.Length == 0)
            {
                ediabas.SetError(EdiabasNet.ErrorCodes.EDIABAS_IFH_0018);
                return false;
            }
            if (serialPort.IsOpen)
            {
                return true;
            }
            try
            {
                serialPort.PortName = comPort;
                serialPort.BaudRate = 9600;
                serialPort.DataBits = 8;
                serialPort.Parity = Parity.None;
                serialPort.StopBits = StopBits.One;
                serialPort.Handshake = Handshake.None;
                serialPort.DtrEnable = false;
                serialPort.RtsEnable = false;
                serialPort.ReadTimeout = 1;
                serialPort.Open();
            }
            catch (Exception)
            {
                ediabas.SetError(EdiabasNet.ErrorCodes.EDIABAS_IFH_0018);
                return false;
            }
            return true;
        }

        public override bool InterfaceDisconnect()
        {
            base.InterfaceDisconnect();
            connected = false;
            if (interfaceDisconnectFunc != null)
            {
                return interfaceDisconnectFunc();
            }

            if (serialPort.IsOpen)
            {
                serialPort.Close();
            }
            return true;
        }

        public override bool TransmitData(byte[] sendData, out byte[] receiveData)
        {
            receiveData = null;
            if (sendData.Length > sendBuffer.Length)
            {
                ediabas.SetError(EdiabasNet.ErrorCodes.EDIABAS_IFH_0031);
                return false;
            }
            sendData.CopyTo(sendBuffer, 0);
            int receiveLength;
            if (!OBDTrans(sendBuffer, sendData.Length, ref recBuffer, out receiveLength))
            {
                return false;
            }
            receiveData = new byte[receiveLength];
            Array.Copy(recBuffer, receiveData, receiveLength);
            return true;
        }

        public string ComPort
        {
            get
            {
                return comPort;
            }
            set
            {
                comPort = value;
            }
        }

        public InterfaceConnectDelegate InterfaceConnectFunc
        {
            get
            {
                return interfaceConnectFunc;
            }
            set
            {
                interfaceConnectFunc = value;
            }
        }

        public InterfaceDisconnectDelegate InterfaceDisconnectFunc
        {
            get
            {
                return interfaceDisconnectFunc;
            }
            set
            {
                interfaceDisconnectFunc = value;
            }
        }

        public InterfaceSetConfigDelegate InterfaceSetConfigFunc
        {
            get
            {
                return interfaceSetConfigFunc;
            }
            set
            {
                interfaceSetConfigFunc = value;
            }
        }

        public InterfaceSetDtrDelegate InterfaceSetDtrFunc
        {
            get
            {
                return interfaceSetDtrFunc;
            }
            set
            {
                interfaceSetDtrFunc = value;
            }
        }

        public InterfaceSetRtsDelegate InterfaceSetRtsFunc
        {
            get
            {
                return interfaceSetRtsFunc;
            }
            set
            {
                interfaceSetRtsFunc = value;
            }
        }

        public InterfaceGetDsrDelegate InterfaceGetDsrFunc
        {
            get
            {
                return interfaceGetDsrFunc;
            }
            set
            {
                interfaceGetDsrFunc = value;
            }
        }

        public SendDataDelegate SendDataFunc
        {
            get
            {
                return sendDataFunc;
            }
            set
            {
                sendDataFunc = value;
            }
        }

        public ReceiveDataDelegate ReceiveDataFunc
        {
            get
            {
                return receiveDataFunc;
            }
            set
            {
                receiveDataFunc = value;
            }
        }

        protected virtual bool adapterEcho
        {
            get
            {
                return true;
            }
        }

        protected bool getDsrState()
        {
            if (interfaceConnectFunc == null)
            {
                if (!serialPort.IsOpen)
                {
                    ediabas.SetError(EdiabasNet.ErrorCodes.EDIABAS_IFH_0019);
                    return false;
                }
                return serialPort.DsrHolding;
            }

            if (interfaceGetDsrFunc == null)
            {
                ediabas.SetError(EdiabasNet.ErrorCodes.EDIABAS_IFH_0019);
                return false;
            }
            bool dsrState = false;
            if (!interfaceGetDsrFunc(out dsrState))
            {
                ediabas.SetError(EdiabasNet.ErrorCodes.EDIABAS_IFH_0019);
                return false;
            }
            return dsrState;
        }

        protected bool SendData(byte[] sendData, int length)
        {
            if (sendDataFunc != null)
            {
                return sendDataFunc(sendData, length);
            }
            try
            {
                serialPort.DiscardInBuffer();
                serialPort.Write(sendData, 0, length);
                while (serialPort.BytesToWrite > 0)
                {
                    Thread.Sleep(10);
                }
            }
            catch (Exception)
            {
                return false;
            }
            return true;
        }

        protected bool ReceiveData(byte[] receiveData, int offset, int length, int timeout, int timeoutTelEnd, bool logResponse)
        {
            if (receiveDataFunc != null)
            {
                return receiveDataFunc(receiveData, offset, length, timeout, timeoutTelEnd, logResponse);
            }
            try
            {
                // wait for first byte
                int lastBytesToRead = 0;
                stopWatch.Reset();
                stopWatch.Start();
                for (; ; )
                {
                    lastBytesToRead = serialPort.BytesToRead;
                    if (lastBytesToRead > 0)
                    {
                        break;
                    }
                    if (stopWatch.ElapsedMilliseconds > timeout)
                    {
                        stopWatch.Stop();
                        return false;
                    }
                    Thread.Sleep(10);
                }

                int recLen = 0;
                stopWatch.Reset();
                stopWatch.Start();
                for (; ; )
                {
                    int bytesToRead = serialPort.BytesToRead;
                    if (bytesToRead >= length)
                    {
                        recLen += serialPort.Read(receiveData, offset + recLen, length - recLen);
                    }
                    if (recLen >= length)
                    {
                        break;
                    }
                    if (lastBytesToRead != bytesToRead)
                    {   // bytes received
                        stopWatch.Reset();
                        stopWatch.Start();
                        lastBytesToRead = bytesToRead;
                    }
                    else
                    {
                        if (stopWatch.ElapsedMilliseconds > timeoutTelEnd)
                        {
                            break;
                        }
                    }
                    Thread.Sleep(10);
                }
                stopWatch.Stop();
                if (logResponse)
                {
                    ediabas.LogData(EdiabasNet.ED_LOG_LEVEL.IFH, receiveData, offset, recLen, "Rec ");
                }
                if (recLen < length)
                {
                    return false;
                }
            }
            catch (Exception)
            {
                return false;
            }
            return true;
        }

        protected bool ReceiveData(byte[] receiveData, int offset, int length, int timeout, int timeoutTelEnd)
        {
            return ReceiveData(receiveData, offset, length, timeout, timeoutTelEnd, false);
        }

        protected bool SendWakeAddress5Baud(byte value)
        {
            if (sendDataFunc != null)
            {
                return false;
            }
            try
            {
                serialPort.DiscardInBuffer();
                serialPort.BreakState = true;  // start bit
                Thread.Sleep(200);
                for (int i = 0; i < 8; i++)
                {
                    serialPort.BreakState = (value & (1 << i)) == 0;
                    Thread.Sleep(200);
                }
                serialPort.BreakState = false; // stop bit
                Thread.Sleep(200);
            }
            catch (Exception)
            {
                return false;
            }
            return true;
        }

        protected bool OBDTrans(byte[] sendData, int sendDataLength, ref byte[] receiveData, out int receiveLength)
        {
            receiveLength = 0;
            if (interfaceConnectFunc == null)
            {
                if (!serialPort.IsOpen)
                {
                    ediabas.SetError(EdiabasNet.ErrorCodes.EDIABAS_IFH_0019);
                    return false;
                }
            }
            if (this.parTransmitFunc == null)
            {
                ediabas.SetError(EdiabasNet.ErrorCodes.EDIABAS_IFH_0014);
                return false;
            }

            EdiabasNet.ErrorCodes errorCode = EdiabasNet.ErrorCodes.EDIABAS_ERR_NONE;
            UInt32 retries = commRepeats;
            string retryComm = ediabas.GetConfigProperty("RetryComm");
            if (retryComm != null)
            {
                if (EdiabasNet.StringToValue(retryComm) == 0)
                {
                    retries = 0;
                }
            }
            for (int i = 0; i < retries + 1; i++)
            {
                errorCode = this.parTransmitFunc(sendData, sendDataLength, ref receiveData, out receiveLength);
                if (errorCode == EdiabasNet.ErrorCodes.EDIABAS_ERR_NONE)
                {
                    return true;
                }
                if (errorCode == EdiabasNet.ErrorCodes.EDIABAS_IFH_0003)
                {   // interface error
                    break;
                }
            }
            ediabas.SetError(errorCode);
            return false;
        }

        private EdiabasNet.ErrorCodes TransBmwFast(byte[] sendData, int sendDataLength, ref byte[] receiveData, out int receiveLength)
        {
            receiveLength = 0;

            bool broadcast = false;
            if (sendData[1] == 0xEF)
            {
                broadcast = true;
            }
            if (sendDataLength == 0)
            {
                broadcast = true;
            }

            if (sendDataLength > 0)
            {
                int sendLength = TelLengthBmwFast(sendData);
                sendData[sendLength] = CalcChecksumBmwFast(sendData, sendLength);
                sendLength++;
                ediabas.LogData(EdiabasNet.ED_LOG_LEVEL.IFH, sendData, 0, sendLength, "Send");
                if (!SendData(sendData, sendLength))
                {
                    ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** Sending failed");
                    return EdiabasNet.ErrorCodes.EDIABAS_IFH_0003;
                }
                if (adapterEcho)
                {
                    // remove remote echo
                    if (!ReceiveData(receiveData, 0, sendLength, echoTimeout, this.parTimeoutTelEnd))
                    {
                        ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** No echo received");
                        ReceiveData(receiveData, 0, receiveData.Length, echoTimeout, this.parTimeoutTelEnd, true);
                        return EdiabasNet.ErrorCodes.EDIABAS_IFH_0003;
                    }
                    ediabas.LogData(EdiabasNet.ED_LOG_LEVEL.IFH, receiveData, 0, sendLength, "Echo");
                    for (int i = 0; i < sendLength; i++)
                    {
                        if (receiveData[i] != sendData[i])
                        {
                            ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** Echo incorrect");
                            ReceiveData(receiveData, 0, receiveData.Length, this.parTimeoutStd, this.parTimeoutTelEnd, true);
                            return EdiabasNet.ErrorCodes.EDIABAS_IFH_0003;
                        }
                    }
                }
            }

            int timeout = this.parTimeoutStd;
            for (int retry = 0; retry <= this.parRetryNR; retry++)
            {
                // header byte
                if (!ReceiveData(receiveData, 0, 4, timeout, this.parTimeoutTelEnd))
                {
                    ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** No header received");
                    return EdiabasNet.ErrorCodes.EDIABAS_IFH_0009;
                }
                if ((receiveData[0] & 0xC0) != 0x80)
                {
                    ediabas.LogData(EdiabasNet.ED_LOG_LEVEL.IFH, receiveData, 0, 4, "Head");
                    ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** Invalid header");
                    ReceiveData(receiveData, 0, receiveData.Length, timeout, this.parTimeoutTelEnd, true);
                    return EdiabasNet.ErrorCodes.EDIABAS_IFH_0009;
                }

                int recLength = TelLengthBmwFast(receiveData);
                if (!ReceiveData(receiveData, 4, recLength - 3, this.parTimeoutTelEnd, this.parTimeoutTelEnd))
                {
                    ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** No tail received");
                    return EdiabasNet.ErrorCodes.EDIABAS_IFH_0009;
                }
                ediabas.LogData(EdiabasNet.ED_LOG_LEVEL.IFH, receiveData, 0, recLength + 1, "Resp");
                if (CalcChecksumBmwFast(receiveData, recLength) != receiveData[recLength])
                {
                    ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** Checksum incorrect");
                    ReceiveData(receiveData, 0, receiveData.Length, timeout, this.parTimeoutTelEnd, true);
                    return EdiabasNet.ErrorCodes.EDIABAS_IFH_0009;
                }
                if (!broadcast)
                {
                    if ((receiveData[1] != sendData[2]) ||
                        (receiveData[2] != sendData[1]))
                    {
                        ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** Address incorrect");
                        ReceiveData(receiveData, 0, receiveData.Length, timeout, this.parTimeoutTelEnd, true);
                        return EdiabasNet.ErrorCodes.EDIABAS_IFH_0009;
                    }
                }

                int dataLen = receiveData[0] & 0x3F;
                int dataStart = 3;
                if (dataLen == 0)
                {   // with length byte
                    dataLen = receiveData[3];
                    dataStart++;
                }
                if ((dataLen == 3) && (receiveData[dataStart] == 0x7F) && (receiveData[dataStart + 2] == 0x78))
                {   // negative response 0x78
                    ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** NR 0x78");
                    timeout = this.parTimeoutNR;
                }
                else
                {
                    break;
                }
            }
            receiveLength = TelLengthBmwFast(receiveData) + 1;
            return EdiabasNet.ErrorCodes.EDIABAS_ERR_NONE;
        }

        // telegram length without checksum
        private int TelLengthBmwFast(byte[] dataBuffer)
        {
            int telLength = dataBuffer[0] & 0x3F;
            if (telLength == 0)
            {   // with length byte
                telLength = dataBuffer[3] + 4;
            }
            else
            {
                telLength += 3;
            }
            return telLength;
        }

        private byte CalcChecksumBmwFast(byte[] data, int length)
        {
            byte sum = 0;
            for (int i = 0; i < length; i++)
            {
                sum += data[i];
            }
            return sum;
        }

        private EdiabasNet.ErrorCodes TransKwp2000S(byte[] sendData, int sendDataLength, ref byte[] receiveData, out int receiveLength)
        {
            receiveLength = 0;

            bool broadcast = false;
            if (sendData[1] == 0xEF)
            {
                broadcast = true;
            }
            if (sendDataLength == 0)
            {
                broadcast = true;
            }

            if (sendDataLength > 0)
            {
                int sendLength = TelLengthKwp2000S(sendData);
                sendData[sendLength] = CalcChecksumKWP2000S(sendData, sendLength);
                sendLength++;
                ediabas.LogData(EdiabasNet.ED_LOG_LEVEL.IFH, sendData, 0, sendLength, "Send");
                if (!SendData(sendData, sendLength))
                {
                    ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** Sending failed");
                    return EdiabasNet.ErrorCodes.EDIABAS_IFH_0003;
                }
                if (adapterEcho)
                {
                    // remove remote echo
                    if (!ReceiveData(receiveData, 0, sendLength, echoTimeout, this.parTimeoutTelEnd))
                    {
                        ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** No echo received");
                        ReceiveData(receiveData, 0, receiveData.Length, echoTimeout, this.parTimeoutTelEnd, true);
                        return EdiabasNet.ErrorCodes.EDIABAS_IFH_0003;
                    }
                    ediabas.LogData(EdiabasNet.ED_LOG_LEVEL.IFH, receiveData, 0, sendLength, "Echo");
                    for (int i = 0; i < sendLength; i++)
                    {
                        if (receiveData[i] != sendData[i])
                        {
                            ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** Echo incorrect");
                            ReceiveData(receiveData, 0, receiveData.Length, this.parTimeoutStd, this.parTimeoutTelEnd, true);
                            return EdiabasNet.ErrorCodes.EDIABAS_IFH_0003;
                        }
                    }
                }
            }

            int timeout = this.parTimeoutStd;
            for (int retry = 0; retry <= this.parRetryNR; retry++)
            {
                // header byte
                if (!ReceiveData(receiveData, 0, 4, timeout, this.parTimeoutTelEnd))
                {
                    ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** No header received");
                    return EdiabasNet.ErrorCodes.EDIABAS_IFH_0009;
                }

                int recLength = TelLengthKwp2000S(receiveData);
                if (!ReceiveData(receiveData, 4, recLength - 3, this.parTimeoutTelEnd, this.parTimeoutTelEnd))
                {
                    ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** No tail received");
                    return EdiabasNet.ErrorCodes.EDIABAS_IFH_0009;
                }
                ediabas.LogData(EdiabasNet.ED_LOG_LEVEL.IFH, receiveData, 0, recLength + 1, "Resp");
                if (CalcChecksumKWP2000S(receiveData, recLength) != receiveData[recLength])
                {
                    ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** Checksum incorrect");
                    ReceiveData(receiveData, 0, receiveData.Length, timeout, this.parTimeoutTelEnd, true);
                    return EdiabasNet.ErrorCodes.EDIABAS_IFH_0009;
                }
                if (!broadcast)
                {
                    if ((receiveData[1] != sendData[2]) ||
                        (receiveData[2] != sendData[1]))
                    {
                        ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** Address incorrect");
                        ReceiveData(receiveData, 0, receiveData.Length, timeout, this.parTimeoutTelEnd, true);
                        return EdiabasNet.ErrorCodes.EDIABAS_IFH_0009;
                    }
                }

                int dataLen = receiveData[3];
                int dataStart = 4;
                if ((dataLen == 3) && (receiveData[dataStart] == 0x7F) && (receiveData[dataStart + 2] == 0x78))
                {   // negative response 0x78
                    ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** NR 0x78");
                    timeout = this.parTimeoutNR;
                }
                else
                {
                    break;
                }
            }
            receiveLength = TelLengthKwp2000S(receiveData) + 1;
            return EdiabasNet.ErrorCodes.EDIABAS_ERR_NONE;
        }

        // telegram length without checksum
        private int TelLengthKwp2000S(byte[] dataBuffer)
        {
            int telLength = dataBuffer[3] + 4;
            return telLength;
        }

        private byte CalcChecksumKWP2000S(byte[] data, int length)
        {
            byte sum = 0;
            for (int i = 0; i < length; i++)
            {
                sum ^= data[i];
            }
            return sum;
        }

        private EdiabasNet.ErrorCodes TransDS2(byte[] sendData, int sendDataLength, ref byte[] receiveData, out int receiveLength)
        {
            receiveLength = 0;

            if (sendDataLength > 0)
            {
                int sendLength = sendDataLength;
                sendData[sendLength] = CalcChecksumDS2(sendData, sendLength);
                sendLength++;
                ediabas.LogData(EdiabasNet.ED_LOG_LEVEL.IFH, sendData, 0, sendLength, "Send");
                if (!SendData(sendData, sendLength))
                {
                    ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** Sending failed");
                    return EdiabasNet.ErrorCodes.EDIABAS_IFH_0003;
                }
                if (adapterEcho)
                {
                    // remove remote echo
                    if (!ReceiveData(receiveData, 0, sendLength, echoTimeout, this.parTimeoutTelEnd))
                    {
                        ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** No echo received");
                        ReceiveData(receiveData, 0, receiveData.Length, echoTimeout, this.parTimeoutTelEnd, true);
                        return EdiabasNet.ErrorCodes.EDIABAS_IFH_0003;
                    }
                    ediabas.LogData(EdiabasNet.ED_LOG_LEVEL.IFH, receiveData, 0, sendLength, "Echo");
                    for (int i = 0; i < sendLength; i++)
                    {
                        if (receiveData[i] != sendData[i])
                        {
                            ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** Echo incorrect");
                            ReceiveData(receiveData, 0, receiveData.Length, this.parTimeoutStd, this.parTimeoutTelEnd, true);
                            return EdiabasNet.ErrorCodes.EDIABAS_IFH_0003;
                        }
                    }
                }
            }

            // header byte
            int headerLen = 0;
            if (commAnswerLen != null && commAnswerLen.Length >= 2)
            {
                headerLen = commAnswerLen[0];
                if (headerLen < 0)
                {
                    headerLen = (-headerLen) + 1;
                }
            }
            if (headerLen == 0)
            {
                ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** Header lenght zero");
                return EdiabasNet.ErrorCodes.EDIABAS_IFH_0041;
            }
            if (!ReceiveData(receiveData, 0, headerLen, this.parTimeoutStd, this.parTimeoutTelEnd))
            {
                ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** No header received");
                return EdiabasNet.ErrorCodes.EDIABAS_IFH_0009;
            }

            int recLength = TelLengthDS2(receiveData);
            if (recLength == 0)
            {
                ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** Receive lenght zero");
                return EdiabasNet.ErrorCodes.EDIABAS_IFH_0009;
            }
            if (!ReceiveData(receiveData, headerLen, recLength - headerLen, this.parTimeoutTelEnd, this.parTimeoutTelEnd))
            {
                ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** No tail received");
                return EdiabasNet.ErrorCodes.EDIABAS_IFH_0009;
            }
            ediabas.LogData(EdiabasNet.ED_LOG_LEVEL.IFH, receiveData, 0, recLength, "Resp");
            if (CalcChecksumDS2(receiveData, recLength - 1) != receiveData[recLength - 1])
            {
                ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** Checksum incorrect");
                ReceiveData(receiveData, 0, receiveData.Length, this.parTimeoutStd, this.parTimeoutTelEnd, true);
                return EdiabasNet.ErrorCodes.EDIABAS_IFH_0009;
            }

            receiveLength = recLength;
            return EdiabasNet.ErrorCodes.EDIABAS_ERR_NONE;
        }

        // telegram length with checksum
        private int TelLengthDS2(byte[] dataBuffer)
        {
            int telLength = 0;
            if (commAnswerLen != null && commAnswerLen.Length >= 2)
            {
                telLength = commAnswerLen[0];   // >0 fix length
                if (telLength < 0)
                {   // offset in buffer
                    int offset = (-telLength);
                    if (dataBuffer.Length < offset)
                    {
                        return 0;
                    }
                    telLength = dataBuffer[offset] + commAnswerLen[1];  // + answer offset
                }
            }
            return telLength;
        }

        private byte CalcChecksumDS2(byte[] data, int length)
        {
            byte sum = 0;
            for (int i = 0; i < length; i++)
            {
                sum ^= data[i];
            }
            return sum;
        }

        private EdiabasNet.ErrorCodes TransIso9141(byte[] sendData, int sendDataLength, ref byte[] receiveData, out int receiveLength)
        {
            receiveLength = 0;
            keyBytes = byteArray0;
            EdiabasNet.ErrorCodes errorCode;

            if (sendDataLength > iso9141Buffer.Length)
            {
                ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** Invalid send data length");
                return EdiabasNet.ErrorCodes.EDIABAS_IFH_0041;
            }

            if (!this.ecuConnected)
            {
                ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "Establish connection");
                if (!SendWakeAddress5Baud(this.parWakeAddress))
                {
                    ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** Sending wake address failed");
                    return EdiabasNet.ErrorCodes.EDIABAS_IFH_0003;
                }

                if (!ReceiveData(iso9141Buffer, 0, 1, 300, 300))
                {
                    ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** No wake response");
                    return EdiabasNet.ErrorCodes.EDIABAS_IFH_0009;
                }
                if (iso9141Buffer[0] != 0x55)
                {
                    ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** Invalid baud rate");
                    return EdiabasNet.ErrorCodes.EDIABAS_IFH_0009;
                }

                byte[] keyBytesBuffer = new byte[2];
                if (!ReceiveData(keyBytesBuffer, 0, 2, this.parTimeoutTelEnd, this.parTimeoutTelEnd))
                {
                    ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** No key bytes received");
                    return EdiabasNet.ErrorCodes.EDIABAS_IFH_0009;
                }
                this.keyBytes = keyBytesBuffer;

                iso9141Buffer[0] = (byte)(~keyBytesBuffer[1]);
                Thread.Sleep(10);
                if (!SendData(iso9141Buffer, 1))
                {
                    ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** Sending key byte response failed");
                    return EdiabasNet.ErrorCodes.EDIABAS_IFH_0003;
                }
                ediabas.LogFormat(EdiabasNet.ED_LOG_LEVEL.IFH, "Key bytes: {0:X02} {1:X02}", keyBytesBuffer[0], keyBytesBuffer[1]);
                this.blockCounter = 1;
            }

            ediabas.LogData(EdiabasNet.ED_LOG_LEVEL.IFH, sendData, 0, sendDataLength, "Request");
            int recLength = 0;
            int recBlocks = 0;
            int maxRecBlocks = 1;
            if (commAnswerLen != null && commAnswerLen.Length >= 1)
            {
                maxRecBlocks = commAnswerLen[0];
            }

            int waitToSendCount = 0;
            bool waitToSend = true;
            bool transmitDone = false;
            for (; ; )
            {
                errorCode = ReceiveIso9141Block(iso9141Buffer);
                if (errorCode != EdiabasNet.ErrorCodes.EDIABAS_ERR_NONE)
                {
                    return errorCode;
                }
                this.blockCounter++;

                byte command = iso9141Buffer[2];
                if (!waitToSend)
                {   // store received data
                    if ((recBlocks == 0) || (command != 0x09))
                    {
                        int blockLen = iso9141Buffer[0];
                        if (recLength + blockLen > receiveData.Length)
                        {
                            ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** Receive buffer overflow, ignore data");
                            transmitDone = true;
                        }
                        Array.Copy(iso9141Buffer, 0, receiveData, recLength, blockLen);
                        recLength += blockLen;
                        recBlocks++;
                        if (recBlocks >= maxRecBlocks)
                        {   // all blocks received
                            ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "All blocks received");
                            transmitDone = true;
                        }
                    }
                }

                bool sendDataValid = false;
                if (command == 0x09)
                {   // ack
                    if (waitToSend)
                    {
                        waitToSend = false;
                        Array.Copy(sendData, iso9141Buffer, sendDataLength);
                        sendDataValid = true;
                    }
                    else
                    {
                        if (recBlocks > 0)
                        {
                            // at least one block received
                            ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "Transmission finished");
                            transmitDone = true;
                        }
                    }
                }
                else
                {   // data received
                }

                if (waitToSend)
                {
                    waitToSendCount++;
                    if (waitToSendCount > 1000)
                    {
                        ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** Wait for first ACK failed");
                        return EdiabasNet.ErrorCodes.EDIABAS_IFH_0009;
                    }
                }
                if (!sendDataValid)
                {
                    iso9141Buffer[0] = 0x03;    // block length
                    iso9141Buffer[2] = 0x09;    // ACK
                }

                iso9141Buffer[1] = this.blockCounter++;
                errorCode = SendIso9141Block(iso9141Buffer);
                if (errorCode != EdiabasNet.ErrorCodes.EDIABAS_ERR_NONE)
                {
                    return errorCode;
                }
                if (transmitDone)
                {
                    break;
                }
            }

            this.ecuConnected = true;
            receiveLength = recLength;
            ediabas.LogData(EdiabasNet.ED_LOG_LEVEL.IFH, receiveData, 0, receiveLength, "Answer");
            return EdiabasNet.ErrorCodes.EDIABAS_ERR_NONE;
        }

        private EdiabasNet.ErrorCodes SendIso9141Block(byte[] sendData)
        {
            int blockLen = sendData[0];
            ediabas.LogData(EdiabasNet.ED_LOG_LEVEL.IFH, sendData, 0, blockLen, "Send");
            for (int i = 0; i < blockLen; i++)
            {
                iso9141BlockBuffer[0] = sendData[i];
                if (!SendData(iso9141BlockBuffer, 1))
                {
                    ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** Sending failed");
                    return EdiabasNet.ErrorCodes.EDIABAS_IFH_0003;
                }
                if (!ReceiveData(iso9141BlockBuffer, 0, 1, this.parTimeoutTelEnd, this.parTimeoutTelEnd))
                {
                    ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** No block response");
                    return EdiabasNet.ErrorCodes.EDIABAS_IFH_0009;
                }
                if ((byte)(~iso9141BlockBuffer[0]) != sendData[i])
                {
                    ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** Response invalid");
                    return EdiabasNet.ErrorCodes.EDIABAS_IFH_0009;
                }
            }
            iso9141BlockBuffer[0] = 0x03;   // block end
            if (!SendData(iso9141BlockBuffer, 1))
            {
                ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** Sending failed");
                return EdiabasNet.ErrorCodes.EDIABAS_IFH_0003;
            }
            return EdiabasNet.ErrorCodes.EDIABAS_ERR_NONE;
        }

        private EdiabasNet.ErrorCodes ReceiveIso9141Block(byte[] recData)
        {
            // block length
            if (!ReceiveData(recData, 0, 1, this.parTimeoutTelEnd, this.parTimeoutTelEnd))
            {
                ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** No block length received");
                return EdiabasNet.ErrorCodes.EDIABAS_IFH_0009;
            }

            int blockLen = recData[0];
            for (int i = 0; i < blockLen; i++)
            {
                iso9141BlockBuffer[0] = (byte)(~recData[i]);
                if (!SendData(iso9141BlockBuffer, 1))
                {
                    ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** Sending failed");
                    return EdiabasNet.ErrorCodes.EDIABAS_IFH_0003;
                }
                if (!ReceiveData(recData, i + 1, 1, this.parTimeoutTelEnd, this.parTimeoutTelEnd))
                {
                    ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** No block data received");
                    return EdiabasNet.ErrorCodes.EDIABAS_IFH_0009;
                }
            }
            ediabas.LogData(EdiabasNet.ED_LOG_LEVEL.IFH, recData, 0, blockLen, "Resp");
            if (recData[blockLen] != 0x03)
            {
                ediabas.LogString(EdiabasNet.ED_LOG_LEVEL.IFH, "*** Block end invalid");
                return EdiabasNet.ErrorCodes.EDIABAS_IFH_0009;
            }
            return EdiabasNet.ErrorCodes.EDIABAS_ERR_NONE;
        }

        protected override void Dispose(bool disposing)
        {
            // Check to see if Dispose has already been called.
            if (!this.disposed)
            {
                // If disposing equals true, dispose all managed
                // and unmanaged resources.
                if (disposing)
                {
                    // Dispose managed resources.
                    serialPort.Dispose();
                }
                InterfaceUnlock();

                // Note disposing has been done.
                disposed = true;
            }
        }
    }
}
