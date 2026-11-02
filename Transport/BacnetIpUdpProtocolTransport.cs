namespace System.IO.BACnet;

/// <summary>
/// This is the standard BACNet udp transport
/// </summary>
public class BacnetIpUdpProtocolTransport : BacnetTransportBase
{
    private UdpClient _sharedConn;
    private UdpClient _exclusiveConn;
    private readonly bool _exclusivePort;
    private readonly bool _dontFragment;
    private readonly string _localEndpoint;
    private readonly ITransportMonitor _transportMonitor;
    private BacnetAddress _broadcastAddress;
    private bool _disposing;
    private CancellationTokenSource _receiveCancellation;
    private TimeSpan _receiveErrorLogInterval = TimeSpan.FromMinutes(5);

    private const int MinReceiveErrorDelayMs = 100;
    private const int MaxReceiveErrorDelayMs = 5000;

    public delegate void BvlcResultReceiveHandler(IPEndPoint sender, BacnetBvlcResults result);
    public event BvlcResultReceiveHandler OnBvlcResultReceived; 

    public BVLC Bvlc { get; private set; }
    public int SharedPort { get; }
    public int ExclusivePort { get; }

    /// <summary>
    /// Minimum size of the socket receive buffer, in bytes. Datagrams that arrive while the buffer is full are
    /// dropped by the operating system. A larger system default is kept. Must be set before <see cref="Start"/>.
    /// </summary>
    public int SocketReceiveBufferSize { get; set; } = 1024 * 1024;

    /// <summary>
    /// While receiving keeps failing, the 1st, 2nd, 4th, 8th, ... failure in a row is logged, and at least one
    /// failure in this interval, so a long outage still shows in the log. Must be greater than zero;
    /// <see cref="TimeSpan.MaxValue"/> logs the powers of two only.
    /// </summary>
    public TimeSpan ReceiveErrorLogInterval
    {
        get => _receiveErrorLogInterval;
        set => _receiveErrorLogInterval = value > TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "The interval must be greater than zero.");
    }

    // Give 0.0.0.0:xxxx if the socket is open with System.Net.IPAddress.Any
    // Today only used by _GetBroadcastAddress method & the bvlc layer class in BBMD mode
    // Some more complex solutions could avoid this, that's why this property is virtual
    public virtual IPEndPoint LocalEndPoint => (IPEndPoint)_exclusiveConn.Client.LocalEndPoint;

    public BacnetIpUdpProtocolTransport(int port, bool useExclusivePort = false, bool dontFragment = false,
        int maxPayload = 1472, string localEndpointIp = "", ITransportMonitor transportMonitor = null)
    {
        SharedPort = port;
        MaxBufferLength = maxPayload;
        Type = BacnetAddressTypes.IP;
        HeaderLength = BVLC.BVLC_HEADER_LENGTH;
        MaxApduLength = BVLC.BVLC_MAX_APDU;

        _exclusivePort = useExclusivePort;
        _dontFragment = dontFragment;
        _localEndpoint = localEndpointIp;
        _transportMonitor = transportMonitor;
    }

    public BacnetIpUdpProtocolTransport(int sharedPort, int exclusivePort, bool dontFragment = false,
        int maxPayload = 1472, string localEndpointIp = "", ITransportMonitor transportMonitor = null)
        : this(sharedPort, false, dontFragment, maxPayload, localEndpointIp)
    {
        _transportMonitor = transportMonitor;
        ExclusivePort = exclusivePort;
    }

    public override bool Equals(object obj)
    {
        var a = obj as BacnetIpUdpProtocolTransport;
        return a?.SharedPort == SharedPort;
    }

    public override int GetHashCode()
    {
        return SharedPort.GetHashCode();
    }

    public override string ToString()
    {
        return $"Udp:{SharedPort}";
    }

    private void Open()
    {
        if (!_exclusivePort)
        {
            /* We need a shared broadcast "listen" port. This is the 0xBAC0 port */
            /* This will enable us to have more than 1 client, on the same machine. Perhaps it's not that important though. */
            /* We (might) only recieve the broadcasts on this. Any unicasts to this might be eaten by another local client */
            if (_sharedConn == null)
            {
                _sharedConn = new UdpClient { ExclusiveAddressUse = false };
                _sharedConn.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                var ep = new IPEndPoint(IPAddress.Any, SharedPort);
                if (!string.IsNullOrEmpty(_localEndpoint)) ep = new IPEndPoint(IPAddress.Parse(_localEndpoint), SharedPort);
                DisableConnReset(_sharedConn);
                _sharedConn.Client.Bind(ep);
                SetDontFragment(_sharedConn, _dontFragment);
                SetReceiveBufferSize(_sharedConn);
                Log.Info($"Binded shared {ep} using UDP");
            }
            /* This is our own exclusive port. We'll recieve everything sent to this. */
            /* So this is how we'll present our selves to the world */
            if (_exclusiveConn == null)
            {
                var ep = new IPEndPoint(IPAddress.Any, ExclusivePort);
                if (!string.IsNullOrEmpty(_localEndpoint)) ep = new IPEndPoint(IPAddress.Parse(_localEndpoint), ExclusivePort);
                _exclusiveConn = new UdpClient(ep);

                // Gets the Endpoint : the assigned Udp port number in fact
                ep = (IPEndPoint)_exclusiveConn.Client.LocalEndPoint;
                // closes the socket
                _exclusiveConn.Close();
                // Re-opens it with the freeed port number, to be sure it's a real active/server socket
                // which cannot be disarmed for listen by .NET for incoming call after a few inactivity
                // minutes ... yes it's like this at least on several systems
                _exclusiveConn = new UdpClient(ep)
                {
                    EnableBroadcast = true
                };
                SetDontFragment(_exclusiveConn, _dontFragment);
                DisableConnReset(_exclusiveConn);
                SetReceiveBufferSize(_exclusiveConn);
            }
        }
        else
        {
            var ep = new IPEndPoint(IPAddress.Any, SharedPort);
            if (!string.IsNullOrEmpty(_localEndpoint)) ep = new IPEndPoint(IPAddress.Parse(_localEndpoint), SharedPort);
            _exclusiveConn = new UdpClient { ExclusiveAddressUse = true };
            DisableConnReset(_exclusiveConn);
            _exclusiveConn.Client.Bind(ep);
            SetDontFragment(_exclusiveConn, _dontFragment);
            SetReceiveBufferSize(_exclusiveConn);
            _exclusiveConn.EnableBroadcast = true;
            Log.Info($"Binded exclusively to {ep} using UDP");
        }

        Bvlc = new BVLC(this);
    }

    /// <summary>
    ///   Prevent exception on setting Don't Fragment on OSX
    /// </summary>
    /// <remarks>
    ///   https://github.com/dotnet/runtime/issues/27653
    /// </remarks>
    /// <param name="client"></param>
    /// <param name="dontFragment"></param>
    private void SetDontFragment(UdpClient client, bool dontFragment)
    {
        if (Environment.OSVersion.Platform != PlatformID.MacOSX)
        {
            try
            {
                client.DontFragment = dontFragment;
            }
            catch (SocketException e)
            {
                Log.WarnFormat("Unable to set DontFragment", e);
            }
        }
    }

    private void SetReceiveBufferSize(UdpClient client)
    {
        try
        {
            if (client.Client.ReceiveBufferSize < SocketReceiveBufferSize)
                client.Client.ReceiveBufferSize = SocketReceiveBufferSize;

            // The OS may cap the size (Linux at net.core.rmem_max), so log what we got
            Log.Info($"Socket receive buffer is {client.Client.ReceiveBufferSize} bytes (requested at least {SocketReceiveBufferSize})");
        }
        catch (SocketException e)
        {
            Log.Warn($"Unable to set ReceiveBufferSize to {SocketReceiveBufferSize}", e);
        }
    }

    /// <summary>
    ///   Done to prevent exceptions in Socket.ReceiveFrom()
    /// </summary>
    /// <remarks>
    ///   http://microsoft.public.win32.programmer.networks.narkive.com/RlxW2V6m/udp-comms-and-connection-reset-problem
    /// </remarks>
    private static void DisableConnReset(UdpClient client)
    {
        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            const uint IOC_IN = 0x80000000;
            const uint IOC_VENDOR = 0x18000000;
            const uint SIO_UDP_CONNRESET = IOC_IN | IOC_VENDOR | 12;

            client?.Client.IOControl(unchecked((int)SIO_UDP_CONNRESET),
                new[] { System.Convert.ToByte(false) }, null);
        }
    }

    protected void Close()
    {
        _exclusiveConn.Close();
    }

    public override void Start()
    {
        // Already started: a second set of receive loops would process the same sockets concurrently
        if (_receiveCancellation != null)
            return;

        _disposing = false;

        Open();

        _receiveCancellation = new CancellationTokenSource();
        StartReceiving(_sharedConn);
        StartReceiving(_exclusiveConn);
    }

    private void StartReceiving(UdpClient connection)
    {
        if (connection == null)
            return;

        // ReceiveLoop handles all its exceptions, so the task never faults
        var cancellationToken = _receiveCancellation.Token;
        _ = Task.Run(() => ReceiveLoop(connection, cancellationToken));
    }

    /// <summary>
    /// Receives datagrams one at a time and processes each before receiving the next, so they are processed in
    /// arrival order. When datagrams are already queued in the socket, the receive completes synchronously and
    /// the next iteration handles it, so a burst doesn't nest one call per datagram.
    /// Message handlers must not block: the client processes replies right here and hands requests and
    /// notifications to its own dispatcher thread.
    /// </summary>
    private async Task ReceiveLoop(UdpClient connection, CancellationToken cancellationToken)
    {
        var failedReceives = 0;
        long lastFailureLoggedAt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received;

            try
            {
                received = await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is ObjectDisposedException || cancellationToken.IsCancellationRequested || _disposing)
            {
                LogSafely(log => log.Debug("Connection has been disposed"));
                return;
            }
            catch (Exception e)
            {
                failedReceives++;

                // Log the 1st, 2nd, 4th, 8th, ... failure in a row, so a persistent error doesn't flood the log, and
                // at least one per ReceiveErrorLogInterval, so the gaps don't keep doubling during a long outage
                var now = Environment.TickCount64;
                if ((failedReceives & (failedReceives - 1)) == 0
                    || now - lastFailureLoggedAt >= (long)ReceiveErrorLogInterval.TotalMilliseconds)
                {
                    lastFailureLoggedAt = now;
                    LogSafely(log => log.Error($"Failed to receive data ({failedReceives} in a row)", e));
                }

                // Keep receiving, but back off while the error persists: 100 ms, doubling up to 5 s
                var delay = Math.Min(MinReceiveErrorDelayMs << Math.Min(failedReceives - 1, 6), MaxReceiveErrorDelayMs);
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            if (failedReceives > 0)
            {
                LogSafely(log => log.Warn($"Receiving data again after {failedReceives} failed receives"));
                failedReceives = 0;
            }

            try
            {
                OnReceiveData(received.Buffer, received.RemoteEndPoint);
            }
            catch
            {
                // OnReceiveData logs its own errors, so only a failing logger gets here. That must not end the loop,
                // or nothing would be received on this socket any more.
            }
        }
    }

    private void LogSafely(Action<ILog> write)
    {
        try
        {
            write(Log);
        }
        catch
        {
            // A failing logger must not end the receive loop
        }
    }

    private void OnReceiveData(byte[] receiveBuffer, IPEndPoint ep)
    {
        try
        {
            var receivedLength = receiveBuffer.Length;

            if (receivedLength == 0) // Empty frame : port scanner maybe
                return;

            //verify message
            Convert(ep, out var remoteAddress);

            _transportMonitor?.FrameReceived(receivedLength, ep);

            if (receivedLength < BVLC.BVLC_HEADER_LENGTH)
            {
                var receiveBufferHex = ConvertToHex(receiveBuffer);
                Log.Warn($"Some garbage data got in: {receiveBufferHex}");
                return;
            }

            // Basic Header lenght
            var headerLength = Bvlc.Decode(receiveBuffer, 0, out var function, out var _, ep);

            if (headerLength == -1)
            {
                var receiveBufferHex = ConvertToHex(receiveBuffer);
                Log.Warn($"Unknow BVLC Header in: {receiveBufferHex}");
                return;
            }

            switch (function)
            {
                case BacnetBvlcFunctions.BVLC_RESULT:
                    if (receiveBuffer.Length >= 5)
                    {
                        var resultCode = (BacnetBvlcResults)((receiveBuffer[4] << 8) + receiveBuffer[5]);
                        OnBvlcResultReceived?.Invoke(ep, resultCode);
                    }

                    break;

                case BacnetBvlcFunctions.BVLC_FORWARDED_NPDU:
                    // BVLC_FORWARDED_NPDU frame by a BBMD, change the remote_address to the original one
                    // stored in the BVLC header, we don't care about the BBMD address
                    var ip = ((long)receiveBuffer[7] << 24) + ((long)receiveBuffer[6] << 16) +
                             ((long)receiveBuffer[5] << 8) + receiveBuffer[4];

                    var port = (receiveBuffer[8] << 8) + receiveBuffer[9]; // 0xbac0 maybe
                    Convert(new IPEndPoint(ip, port), out remoteAddress);
                    break;
            }

            if (function != BacnetBvlcFunctions.BVLC_ORIGINAL_UNICAST_NPDU &&
                function != BacnetBvlcFunctions.BVLC_ORIGINAL_BROADCAST_NPDU &&
                function != BacnetBvlcFunctions.BVLC_FORWARDED_NPDU)
            {
                Log.Debug($"{function} - ignoring");
                return;
            }

            if (receivedLength <= headerLength)
            {
                var receiveBufferHex = ConvertToHex(receiveBuffer);
                Log.Warn($"Missing data, only header received: {receiveBufferHex}");
                return;
            }

            InvokeMessageRecieved(receiveBuffer, headerLength, receivedLength - headerLength, remoteAddress);
        }
        catch (ObjectDisposedException)
        {
            Log.Debug("Connection has been disposed");
        }
        catch (Exception e)
        {
            if (_disposing)
                return;

            Log.Error("Exception in OnRecieveData", e);
        }
    }

    public bool SendRegisterAsForeignDevice(IPEndPoint bbmd, short ttl)
    {
        if (bbmd.AddressFamily != AddressFamily.InterNetwork)
            return false;

        Bvlc.SendRegisterAsForeignDevice(bbmd, ttl);
        return true;
    }
    public bool SendRemoteWhois(byte[] buffer, IPEndPoint bbmd, int msgLength)
    {
        if (bbmd.AddressFamily != AddressFamily.InterNetwork)
            return false;

        Bvlc.SendRemoteWhois(buffer, bbmd, msgLength);
        return true;
    }

    public static string ConvertToHex(byte[] buffer)
    {
        return BitConverter.ToString(buffer).Replace("-", "");
    }

    public int Send(byte[] buffer, int dataLength, IPEndPoint ep)
    {
        var bytesSent = _exclusiveConn.Send(buffer, dataLength, ep);
        _transportMonitor?.FrameSent(bytesSent, ep);
        return bytesSent;
    }

    public override int Send(byte[] buffer, int offset, int dataLength, BacnetAddress address, bool waitForTransmission, int timeout)
    {
        if (_exclusiveConn == null) return 0;

        //add header
        var fullLength = dataLength + HeaderLength;
        Bvlc.Encode(buffer, offset - BVLC.BVLC_HEADER_LENGTH, address.net == 0xFFFF
            ? BacnetBvlcFunctions.BVLC_ORIGINAL_BROADCAST_NPDU
            : BacnetBvlcFunctions.BVLC_ORIGINAL_UNICAST_NPDU, fullLength);

        //create end point
        Convert(address, out var ep);

        var bytesSent = _exclusiveConn.Send(buffer, fullLength, ep);
        _transportMonitor?.FrameSent(bytesSent, ep);
        return bytesSent;
    }

    public static void Convert(IPEndPoint ep, out BacnetAddress address)
    {
        var tmp1 = ep.Address.GetAddressBytes();
        var tmp2 = BitConverter.GetBytes((ushort)ep.Port);
        Array.Reverse(tmp2);
        Array.Resize(ref tmp1, tmp1.Length + tmp2.Length);
        Array.Copy(tmp2, 0, tmp1, tmp1.Length - tmp2.Length, tmp2.Length);
        address = new BacnetAddress(BacnetAddressTypes.IP, 0, tmp1);
    }

    public static void Convert(BacnetAddress address, out IPEndPoint ep)
    {
        long ipAddress = BitConverter.ToUInt32(address.adr, 0);
        var port = (ushort)((address.adr[4] << 8) | (address.adr[5] << 0));
        ep = new IPEndPoint(ipAddress, port);
    }

    // Get the IPAddress only if one is present
    // this could be usefull to find the broadcast address even if the socket is open on the default interface
    // removes somes virtual interfaces (certainly not all)
    private static UnicastIPAddressInformation GetAddressDefaultInterface()
    {
        var unicastAddresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(i => i.OperationalStatus == OperationalStatus.Up)
            .Where(i => i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Where(i => !(i.Name.Contains("VirtualBox") || i.Name.Contains("VMware")))
            .SelectMany(i => i.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .ToArray();

        return unicastAddresses.Length == 1
            ? unicastAddresses.Single()
            : null;
    }

    // A lot of problems on Mono (Raspberry) to get the correct broadcast @
    // so this method is overridable (this allows the implementation of operating system specific code)
    // Marc solution http://stackoverflow.com/questions/8119414/how-to-query-the-subnet-masks-using-mono-on-linux for instance
    protected virtual BacnetAddress _GetBroadcastAddress()
    {
        // general broadcast by default if nothing better is found
        var ep = new IPEndPoint(IPAddress.Parse("255.255.255.255"), SharedPort);

        UnicastIPAddressInformation ipAddr = null;

        if (LocalEndPoint.Address.ToString() == "0.0.0.0")
        {
            ipAddr = GetAddressDefaultInterface();
        }
        else
        {
            // restricted local broadcast (directed ... routable)
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                foreach (var ip in adapter.GetIPProperties().UnicastAddresses)
                    if (LocalEndPoint.Address.Equals(ip.Address))
                    {
                        ipAddr = ip;
                        break;
                    }
        }

        if (ipAddr != null)
        {
            try
            {
                var strCurrentIP = ipAddr.Address.ToString().Split('.');
                var strIPNetMask = ipAddr.IPv4Mask.ToString().Split('.');
                var broadcastStr = new StringBuilder();
                for (var i = 0; i < 4; i++)
                {
                    broadcastStr.Append(((byte)(int.Parse(strCurrentIP[i]) | ~int.Parse(strIPNetMask[i]))).ToString());
                    if (i != 3) broadcastStr.Append('.');
                }
                ep = new IPEndPoint(IPAddress.Parse(broadcastStr.ToString()), SharedPort);
            }
            catch
            {
                // on mono IPv4Mask feature not implemented
            }
        }

        Convert(ep, out var broadcast);
        broadcast.net = 0xFFFF;
        return broadcast;
    }

    public override BacnetAddress GetBroadcastAddress()
    {
        return _broadcastAddress ??= _GetBroadcastAddress();
    }

    public override void Dispose()
    {
        _disposing = true;
        _receiveCancellation?.Cancel();
        _receiveCancellation?.Dispose();
        _receiveCancellation = null;
        _exclusiveConn?.Close();
        _exclusiveConn = null;
        _sharedConn?.Close();
        _sharedConn = null;
    }
}
