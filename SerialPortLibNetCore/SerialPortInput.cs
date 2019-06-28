using System;
using System.Threading;
using Microsoft.Extensions.Logging;
using RJCP.IO.Ports;

namespace SerialPortLibNetCore
{
    /// <summary>
    /// Serial port I/O
    /// </summary>
    public class SerialPortInput
    {

        #region Private Fields

        private readonly ILogger<SerialPortInput> _logger;

        private SerialPortStream _serialPort;
        private string _portName = "";
        private int _baudRate = 115200;
        private StopBits _stopBits = StopBits.One;
        private Parity _parity = Parity.None;
        private DataBits _dataBits = DataBits.Eight;

        // Read/Write error state variable
        private bool _gotReadWriteError = true;

        // Serial port reader task
        private Thread _reader;
        // Serial port connection watcher
        private Thread _connectionWatcher;

        private readonly object _accessLock = new object();
        private bool _disconnectRequested;

        public SerialPortInput(ILogger<SerialPortInput> logger)
        {
            _logger = logger;
        }

        #endregion

        #region Public Events

        /// <summary>
        /// Connected state changed event.
        /// </summary>
        public delegate void ConnectionStatusChangedEventHandler(object sender, ConnectionStatusChangedEventArgs args);
        /// <summary>
        /// Occurs when connected state changed.
        /// </summary>
        public event ConnectionStatusChangedEventHandler ConnectionStatusChanged;

        /// <summary>
        /// Message received event.
        /// </summary>
        public delegate void MessageReceivedEventHandler(object sender, MessageReceivedEventArgs args);
        /// <summary>
        /// Occurs when message received.
        /// </summary>
        public event MessageReceivedEventHandler MessageReceived;

        #endregion

        #region Public Members

        /// <summary>
        /// Connect to the serial port.
        /// </summary>
        public bool Connect()
        {
            if (_disconnectRequested)
                return false;
            lock (_accessLock)
            {
                Disconnect();
                Open();
                _connectionWatcher = new Thread(ConnectionWatcherTask);
                _connectionWatcher.Start();
            }
            return IsConnected;
        }

        /// <summary>
        /// Disconnect the serial port.
        /// </summary>
        public void Disconnect()
        {
            if (_disconnectRequested)
                return;
            _disconnectRequested = true;
            Close();
            lock (_accessLock)
            {
                if (_connectionWatcher != null)
                {
                    if (!_connectionWatcher.Join(5000))
                        _connectionWatcher.Abort();
                    _connectionWatcher = null;
                }
                _disconnectRequested = false;
            }
        }

        /// <summary>
        /// Gets a value indicating whether the serial port is connected.
        /// </summary>
        /// <value><c>true</c> if connected; otherwise, <c>false</c>.</value>
        public bool IsConnected
        {
            get { return _serialPort != null && !_gotReadWriteError && !_disconnectRequested; }
        }

        /// <summary>
        /// Sets the serial port options.
        /// </summary>
        /// <param name="portName">Portname.</param>
        /// <param name="baudRate">Baudrate.</param>
        /// <param name="stopBits">Stopbits.</param>
        /// <param name="parity">Parity.</param>
        /// <param name="dataBits">Databits.</param>
        public void SetPort(string portName, int baudRate = 115200, StopBits stopBits = StopBits.One, Parity parity = Parity.None, DataBits dataBits = DataBits.Eight)
        {
            if (_portName != portName)
            {
                // set to error so that the connection watcher will reconnect
                // using the new port
                _gotReadWriteError = true;
            }
            _portName = portName;
            _baudRate = baudRate;
            _stopBits = stopBits;
            _parity = parity;
            _dataBits = dataBits;
        }

        /// <summary>
        /// Sends the message.
        /// </summary>
        /// <returns><c>true</c>, if message was sent, <c>false</c> otherwise.</returns>
        /// <param name="message">Message.</param>
        public bool SendMessage(byte[] message)
        {
            var success = false;
            if (IsConnected)
            {
                try
                {
                    _serialPort.Write(message, 0, message.Length);
                    success = true;
                    _logger.LogDebug(BitConverter.ToString(message));
                }
                catch (Exception e)
                {
                    _logger.LogError(null, e);
                }
            }
            return success;
        }

        #endregion

        #region Private members

        #region Serial Port handling

        private bool Open()
        {
            var success = false;
            lock (_accessLock)
            {
                Close();
                try
                {
                    var tryOpen = true;
                    if (Environment.OSVersion.Platform.ToString().StartsWith("Win") == false)
                    {
                        tryOpen = System.IO.File.Exists(_portName);
                    }
                    if (tryOpen)
                    {
                        _serialPort = new SerialPortStream();
                        _serialPort.ErrorReceived += HandleErrorReceived;
                        _serialPort.PortName = _portName;
                        _serialPort.BaudRate = _baudRate;
                        _serialPort.StopBits = _stopBits;
                        _serialPort.Parity = _parity;
                        _serialPort.DataBits = (int)_dataBits;

                        // We are not using serialPort.DataReceived event for receiving data since this is not working under Linux/Mono.
                        // We use the readerTask instead (see below).
                        _serialPort.Open();
                        success = true;
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(null, e);
                    Close();
                }
                if (_serialPort != null && _serialPort.IsOpen)
                {
                    _gotReadWriteError = false;
                    // Start the Reader task
                    _reader = new Thread(ReaderTask);
                    _reader.Start();
                    OnConnectionStatusChanged(new ConnectionStatusChangedEventArgs(true));
                }
            }
            return success;
        }

        private void Close()
        {
            lock (_accessLock)
            {
                // Stop the Reader task
                if (_reader != null)
                {
                    if (!_reader.Join(5000))
                        _reader.Abort();
                    _reader = null;
                }
                if (_serialPort != null)
                {
                    _serialPort.ErrorReceived -= HandleErrorReceived;
                    if (_serialPort.IsOpen)
                    {
                        _serialPort.Close();
                        OnConnectionStatusChanged(new ConnectionStatusChangedEventArgs(false));
                    }
                    _serialPort.Dispose();
                    _serialPort = null;
                }
                _gotReadWriteError = true;
            }
        }

        private void HandleErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            _logger.LogError(null, e.EventType);
        }

        #endregion

        #region Background Tasks

        private void ReaderTask()
        {
            while (IsConnected)
            {
                var msglen = 0;
                //
                try
                {
                    msglen = _serialPort.BytesToRead;
                    if (msglen > 0)
                    {
                        var message = new byte[msglen];
                        //
                        var readbytes = 0;
                        while (_serialPort.Read(message, readbytes, msglen - readbytes) <= 0)
                            ; // noop
                        if (MessageReceived != null)
                        {
                            OnMessageReceived(new MessageReceivedEventArgs(message));
                        }
                    }
                    else
                    {
                        Thread.Sleep(100);
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(null, e);
                    _gotReadWriteError = true;
                    Thread.Sleep(1000);
                }
            }
        }

        private void ConnectionWatcherTask()
        {
            // This task takes care of automatically reconnecting the interface
            // when the connection is drop or if an I/O error occurs
            while (!_disconnectRequested)
            {
                if (_gotReadWriteError)
                {
                    try
                    {
                        Close();
                        // wait 1 sec before reconnecting
                        Thread.Sleep(1000);
                        if (!_disconnectRequested)
                        {
                            try
                            {
                                Open();
                            }
                            catch (Exception e)
                            {
                                _logger.LogError(null, e);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(null, e);
                    }
                }
                if (!_disconnectRequested)
                    Thread.Sleep(1000);
            }
        }

        #endregion

        #region Events Raising

        /// <summary>
        /// Raises the connected state changed event.
        /// </summary>
        /// <param name="args">Arguments.</param>
        private void OnConnectionStatusChanged(ConnectionStatusChangedEventArgs args)
        {
            _logger.LogDebug(args.Connected.ToString());
            if (ConnectionStatusChanged != null)
                ConnectionStatusChanged(this, args);
        }

        /// <summary>
        /// Raises the message received event.
        /// </summary>
        /// <param name="args">Arguments.</param>
        protected virtual void OnMessageReceived(MessageReceivedEventArgs args)
        {
            _logger.LogDebug(BitConverter.ToString(args.Data));
            if (MessageReceived != null)
                MessageReceived(this, args);
        }

        #endregion

        #endregion

    }
}
