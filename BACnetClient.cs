using System.IO.BACnet.Factory;
using System.Runtime.CompilerServices;
using static System.IO.BACnet.BacnetIpUdpProtocolTransport;

namespace System.IO.BACnet;

public delegate void MessageRecievedHandler(IBacnetTransport sender, byte[] buffer, int offset, int msgLength, BacnetAddress remoteAddress);

/// <summary>
/// BACnet network client or server
/// </summary>
public class BacnetClient : IBacnetMessageFactoryParameters, IDisposable
{
    private int mRetries;
    private readonly InvokeIdGenerator mInvokeId = new();

    private readonly LastSegmentAck _lastSegmentAck = new();

    /// <summary>
    /// Dictionary of List of Tuples with sequence-number and byte[] per invoke-id
    /// TODO: invoke-id should be PER (remote) DEVICE!
    /// </summary>
    private readonly Dictionary<byte, List<Tuple<byte, byte[]>>> _segmentsPerInvokeId = new();
    private readonly Dictionary<byte, object> _locksPerInvokeId = new();
    private readonly Dictionary<byte, byte> _expectedSegmentsPerInvokeId = new();
    private readonly BacnetMessageFactory _messageFactory;

    public const int DefaultUdpPort = 0xBAC0;
    public const int DefaultTimeout = 1000;
    public const int DefaultRetries = 3;

    public IBacnetTransport Transport { get; }
    public BacnetMaxAdpu MaxApduLength => Transport.MaxApduLength;
    public ushort VendorId { get; set; } = 260;
    public int Timeout { get; set; }
    public int TransmitTimeout { get; set; } = 30000;
    public BacnetMaxSegments MaxSegments { get; set; } = BacnetMaxSegments.MAX_SEG0;
    public byte ProposedWindowSize { get; set; } = 10;
    public bool ForceWindowSize { get; set; }
    public bool DefaultSegmentationHandling { get; set; } = true;
    public ILog Log { get; set; } = LogManager.GetLogger<BacnetClient>();
    public event BvlcResultReceiveHandler OnBvlcResultReceived;

    public int Retries
    {
        get => mRetries;
        set => mRetries = Math.Max(1, value);
    }

    public class Segmentation
    {
        // ReSharper disable InconsistentNaming
        // was public before refactor so can't change this
        public EncodeBuffer buffer;
        public byte sequence_number;
        public byte window_size;
        public byte max_segments;
        // ReSharper restore InconsistentNaming
    }

    private sealed class LastSegmentAck
    {
        private readonly ManualResetEvent _wait = new(false);
        private readonly object _lockObject = new();
        private BacnetAddress _address;
        private byte _invokeId;

        public byte SequenceNumber;
        public byte WindowSize;

        public void Set(BacnetAddress adr, byte invokeId, byte sequenceNumber, byte windowSize)
        {
            lock (_lockObject)
            {
                _address = adr;
                _invokeId = invokeId;
                SequenceNumber = sequenceNumber;
                WindowSize = windowSize;
                _wait.Set();
            }
        }

        public bool Wait(BacnetAddress adr, byte invokeId, int timeout)
        {
            Monitor.Enter(_lockObject);
            while (!adr.Equals(this._address) || this._invokeId != invokeId)
            {
                _wait.Reset();
                Monitor.Exit(_lockObject);
                if (!_wait.WaitOne(timeout)) return false;
                Monitor.Enter(_lockObject);
            }
            Monitor.Exit(_lockObject);
            _address = null;
            return true;
        }
    }

    public BacnetClient(int port = DefaultUdpPort, int timeout = DefaultTimeout, int retries = DefaultRetries, ITransportMonitor transportMonitor = null)
        : this(new BacnetIpUdpProtocolTransport(port, transportMonitor: transportMonitor), timeout, retries)
    {
    }

    public BacnetClient(IBacnetTransport transport, int timeout = DefaultTimeout, int retries = DefaultRetries)
    {
        Transport = transport;
        Timeout = timeout;
        Retries = retries;
        _messageFactory = new BacnetMessageFactory(this);
        if (Transport is BacnetIpUdpProtocolTransport ipudp)
        {
            ipudp.OnBvlcResultReceived += OnBvlcResultReceived;
        }
    }

    public override bool Equals(object obj)
    {
        return Transport.Equals((obj as BacnetClient)?.Transport);
    }

    public override int GetHashCode()
    {
        return Transport.GetHashCode();
    }

    public override string ToString()
    {
        return Transport.ToString();
    }

    public EncodeBuffer GetEncodeBuffer(int startOffset)
    {
        return new EncodeBuffer(new byte[Transport.MaxBufferLength], startOffset);
    }

    public void Start()
    {
        Transport.Start();
        Transport.MessageReceived += OnRecieve;
        Log.Info("Started communication");
    }

    public delegate void ConfirmedServiceRequestHandler(BacnetClient sender, BacnetAddress adr, BacnetPduTypes type, BacnetConfirmedServices service, BacnetMaxSegments maxSegments, BacnetMaxAdpu maxAdpu, byte invokeId, byte[] buffer, int offset, int length);
    public event ConfirmedServiceRequestHandler OnConfirmedServiceRequest;
    public delegate void ReadPropertyRequestHandler(BacnetClient sender, BacnetAddress adr, byte invokeId, BacnetObjectId objectId, BacnetPropertyReference property, BacnetMaxSegments maxSegments);
    public event ReadPropertyRequestHandler OnReadPropertyRequest;
    public delegate void ReadPropertyMultipleRequestHandler(BacnetClient sender, BacnetAddress adr, byte invokeId, IList<BacnetReadAccessSpecification> properties, BacnetMaxSegments maxSegments);
    public event ReadPropertyMultipleRequestHandler OnReadPropertyMultipleRequest;
    public delegate void WritePropertyRequestHandler(BacnetClient sender, BacnetAddress adr, byte invokeId, BacnetObjectId objectId, BacnetPropertyValue value, BacnetMaxSegments maxSegments);
    public event WritePropertyRequestHandler OnWritePropertyRequest;
    public delegate void WritePropertyMultipleRequestHandler(BacnetClient sender, BacnetAddress adr, byte invokeId, BacnetObjectId objectId, ICollection<BacnetPropertyValue> values, BacnetMaxSegments maxSegments);
    public event WritePropertyMultipleRequestHandler OnWritePropertyMultipleRequest;
    public delegate void AtomicWriteFileRequestHandler(BacnetClient sender, BacnetAddress adr, byte invokeId, bool isStream, BacnetObjectId objectId, int position, uint blockCount, byte[][] blocks, int[] counts, BacnetMaxSegments maxSegments);
    public event AtomicWriteFileRequestHandler OnAtomicWriteFileRequest;
    public delegate void AtomicReadFileRequestHandler(BacnetClient sender, BacnetAddress adr, byte invokeId, bool isStream, BacnetObjectId objectId, int position, uint count, BacnetMaxSegments maxSegments);
    public event AtomicReadFileRequestHandler OnAtomicReadFileRequest;
    public delegate void SubscribeCOVRequestHandler(BacnetClient sender, BacnetAddress adr, byte invokeId, uint subscriberProcessIdentifier, BacnetObjectId monitoredObjectIdentifier, bool cancellationRequest, bool issueConfirmedNotifications, uint lifetime, BacnetMaxSegments maxSegments);
    public event SubscribeCOVRequestHandler OnSubscribeCOV;
    public delegate void EventNotificationCallbackHandler(BacnetClient sender, BacnetAddress adr, byte invokeId, BacnetEventNotificationData eventData, bool needConfirm);
    public event EventNotificationCallbackHandler OnEventNotify;
    public delegate void SubscribeCOVPropertyRequestHandler(BacnetClient sender, BacnetAddress adr, byte invokeId, uint subscriberProcessIdentifier, BacnetObjectId monitoredObjectIdentifier, BacnetPropertyReference monitoredProperty, bool cancellationRequest, bool issueConfirmedNotifications, uint lifetime, float covIncrement, BacnetMaxSegments maxSegments);
    public event SubscribeCOVPropertyRequestHandler OnSubscribeCOVProperty;
    public delegate void DeviceCommunicationControlRequestHandler(BacnetClient sender, BacnetAddress adr, byte invokeId, uint timeDuration, uint enableDisable, string password, BacnetMaxSegments maxSegments);
    public event DeviceCommunicationControlRequestHandler OnDeviceCommunicationControl;
    public delegate void ReinitializedRequestHandler(BacnetClient sender, BacnetAddress adr, byte invokeId, BacnetReinitializedStates state, string password, BacnetMaxSegments maxSegments);
    public event ReinitializedRequestHandler OnReinitializedDevice;
    public delegate void ReadRangeHandler(BacnetClient sender, BacnetAddress adr, byte invokeId, BacnetObjectId objectId, BacnetPropertyReference property, BacnetReadRangeRequestTypes requestType, uint position, DateTime time, int count, BacnetMaxSegments maxSegments);
    public event ReadRangeHandler OnReadRange;
    public delegate void CreateObjectRequestHandler(BacnetClient sender, BacnetAddress adr, byte invokeId, BacnetObjectId objectId, ICollection<BacnetPropertyValue> values, BacnetMaxSegments maxSegments);
    public event CreateObjectRequestHandler OnCreateObjectRequest;
    public delegate void DeleteObjectRequestHandler(BacnetClient sender, BacnetAddress adr, byte invokeId, BacnetObjectId objectId, BacnetMaxSegments maxSegments);
    public event DeleteObjectRequestHandler OnDeleteObjectRequest;
    public delegate void GetAlarmSummaryOrEventInformationRequestHandler(BacnetClient sender, BacnetAddress adr, byte invokeId, bool getEvent, BacnetObjectId objectId, BacnetMaxAdpu maxApdu, BacnetMaxSegments maxSegments);
    public event GetAlarmSummaryOrEventInformationRequestHandler OnGetAlarmSummaryOrEventInformation;
    public delegate void AlarmAcknowledgeRequestHandler(BacnetClient sender, BacnetAddress adr, byte invokeId, uint ackProcessIdentifier, BacnetObjectId eventObjectIdentifier, uint eventStateAcked, string ackSource, BacnetGenericTime eventTimeStamp, BacnetGenericTime ackTimeStamp);
    public event AlarmAcknowledgeRequestHandler OnAlarmAcknowledge;

    protected void ProcessConfirmedServiceRequest(BacnetAddress address, BacnetPduTypes type, BacnetConfirmedServices service, BacnetMaxSegments maxSegments, BacnetMaxAdpu maxAdpu, byte invokeId, byte[] buffer, int offset, int length)
    {
        try
        {
            Log.Debug($"ConfirmedServiceRequest {service}");

            OnConfirmedServiceRequest?.Invoke(this, address, type, service, maxSegments, maxAdpu, invokeId, buffer, offset, length);

            //don't send segmented messages, if client don't want it
            if ((type & BacnetPduTypes.SEGMENTED_RESPONSE_ACCEPTED) == 0)
                maxSegments = BacnetMaxSegments.MAX_SEG0;

            if (service == BacnetConfirmedServices.SERVICE_CONFIRMED_READ_PROPERTY && OnReadPropertyRequest != null)
            {
                int thsRejectReason;

                if ((thsRejectReason = Services.DecodeReadProperty(buffer, offset, length, out var objectId, out var property)) >= 0)
                {
                    OnReadPropertyRequest(this, address, invokeId, objectId, property, maxSegments);
                }
                else
                {
                    switch (thsRejectReason)
                    {
                        case -1:
                            SendConfirmedServiceReject(address, invokeId, BacnetRejectReason.MISSING_REQUIRED_PARAMETER);
                            break;
                        case -2:
                            SendConfirmedServiceReject(address, invokeId, BacnetRejectReason.INVALID_TAG);
                            break;
                        case -3:
                            SendConfirmedServiceReject(address, invokeId, BacnetRejectReason.TOO_MANY_ARGUMENTS);
                            break;
                    }
                    Log.Warn("Couldn't decode DecodeReadProperty");
                }
            }
            else if (service == BacnetConfirmedServices.SERVICE_CONFIRMED_WRITE_PROPERTY && OnWritePropertyRequest != null)
            {
                if (Services.DecodeWriteProperty(address, buffer, offset, length, out var objectId, out var value) >= 0)
                    OnWritePropertyRequest(this, address, invokeId, objectId, value, maxSegments);
                else
                {
                    SendAbort(address, invokeId, BacnetAbortReason.OTHER);
                    //ErrorResponse(address, service, invokeId, BacnetErrorClasses.ERROR_CLASS_SERVICES, BacnetErrorCodes.ERROR_CODE_ABORT_OTHER);
                    //SendConfirmedServiceReject(adr, invokeId, BacnetRejectReason.OTHER); 
                    Log.Warn("Couldn't decode DecodeWriteProperty");
                }
            }
            else if (service == BacnetConfirmedServices.SERVICE_CONFIRMED_READ_PROP_MULTIPLE && OnReadPropertyMultipleRequest != null)
            {
                if (Services.DecodeReadPropertyMultiple(buffer, offset, length, out var properties) >= 0)
                    OnReadPropertyMultipleRequest(this, address, invokeId, properties, maxSegments);
                else
                {
                    SendAbort(address, invokeId, BacnetAbortReason.OTHER);
                    //ErrorResponse(address, service, invokeId, BacnetErrorClasses.ERROR_CLASS_SERVICES, BacnetErrorCodes.ERROR_CODE_ABORT_OTHER);
                    Log.Warn("Couldn't decode DecodeReadPropertyMultiple");
                }
            }
            else if (service == BacnetConfirmedServices.SERVICE_CONFIRMED_WRITE_PROP_MULTIPLE && OnWritePropertyMultipleRequest != null)
            {
                if (Services.DecodeWritePropertyMultiple(address, buffer, offset, length, out var objectId, out var values) >= 0)
                    OnWritePropertyMultipleRequest(this, address, invokeId, objectId, values, maxSegments);
                else
                {
                    SendAbort(address, invokeId, BacnetAbortReason.OTHER);
                    //ErrorResponse(address, service, invokeId, BacnetErrorClasses.ERROR_CLASS_SERVICES, BacnetErrorCodes.ERROR_CODE_ABORT_OTHER);
                    Log.Warn("Couldn't decode DecodeWritePropertyMultiple");
                }
            }
            else if (service == BacnetConfirmedServices.SERVICE_CONFIRMED_COV_NOTIFICATION && OnCOVNotification != null)
            {
                if (Services.DecodeCOVNotifyUnconfirmed(address, buffer, offset, length, out var subscriberProcessIdentifier, out var initiatingDeviceIdentifier, out var monitoredObjectIdentifier, out var timeRemaining, out var values) >= 0)
                    OnCOVNotification(this, address, invokeId, subscriberProcessIdentifier, initiatingDeviceIdentifier, monitoredObjectIdentifier, timeRemaining, true, values, maxSegments);
                else
                {
                    SendAbort(address, invokeId, BacnetAbortReason.OTHER);
                    //ErrorResponse(address, service, invokeId, BacnetErrorClasses.ERROR_CLASS_SERVICES, BacnetErrorCodes.ERROR_CODE_ABORT_OTHER);
                    Log.Warn("Couldn't decode COVNotify");
                }
            }
            else if (service == BacnetConfirmedServices.SERVICE_CONFIRMED_ATOMIC_WRITE_FILE && OnAtomicWriteFileRequest != null)
            {
                if (Services.DecodeAtomicWriteFile(buffer, offset, length, out var isStream, out var objectId, out var position, out var blockCount, out var blocks, out var counts) >= 0)
                    OnAtomicWriteFileRequest(this, address, invokeId, isStream, objectId, position, blockCount, blocks, counts, maxSegments);
                else
                {
                    SendAbort(address, invokeId, BacnetAbortReason.OTHER);
                    //ErrorResponse(address, service, invokeId, BacnetErrorClasses.ERROR_CLASS_SERVICES, BacnetErrorCodes.ERROR_CODE_ABORT_OTHER);
                    Log.Warn("Couldn't decode AtomicWriteFile");
                }
            }
            else if (service == BacnetConfirmedServices.SERVICE_CONFIRMED_ATOMIC_READ_FILE && OnAtomicReadFileRequest != null)
            {
                if (Services.DecodeAtomicReadFile(buffer, offset, length, out var isStream, out var objectId, out var position, out var count) >= 0)
                    OnAtomicReadFileRequest(this, address, invokeId, isStream, objectId, position, count, maxSegments);
                else
                {
                    SendAbort(address, invokeId, BacnetAbortReason.OTHER);
                    //ErrorResponse(address, service, invokeId, BacnetErrorClasses.ERROR_CLASS_SERVICES, BacnetErrorCodes.ERROR_CODE_ABORT_OTHER);
                    Log.Warn("Couldn't decode AtomicReadFile");
                }
            }
            else if (service == BacnetConfirmedServices.SERVICE_CONFIRMED_SUBSCRIBE_COV && OnSubscribeCOV != null)
            {
                if (Services.DecodeSubscribeCOV(buffer, offset, length, out var subscriberProcessIdentifier, out var monitoredObjectIdentifier, out var cancellationRequest, out var issueConfirmedNotifications, out var lifetime) >= 0)
                    OnSubscribeCOV(this, address, invokeId, subscriberProcessIdentifier, monitoredObjectIdentifier, cancellationRequest, issueConfirmedNotifications, lifetime, maxSegments);
                else
                {
                    SendAbort(address, invokeId, BacnetAbortReason.OTHER);
                    //ErrorResponse(address, service, invokeId, BacnetErrorClasses.ERROR_CLASS_SERVICES, BacnetErrorCodes.ERROR_CODE_ABORT_OTHER);
                    Log.Warn("Couldn't decode SubscribeCOV");
                }
            }
            else if (service == BacnetConfirmedServices.SERVICE_CONFIRMED_SUBSCRIBE_COV_PROPERTY && OnSubscribeCOVProperty != null)
            {
                if (Services.DecodeSubscribeProperty(buffer, offset, length, out var subscriberProcessIdentifier, out var monitoredObjectIdentifier, out var monitoredProperty, out var cancellationRequest, out var issueConfirmedNotifications, out var lifetime, out var covIncrement) >= 0)
                    OnSubscribeCOVProperty(this, address, invokeId, subscriberProcessIdentifier, monitoredObjectIdentifier, monitoredProperty, cancellationRequest, issueConfirmedNotifications, lifetime, covIncrement, maxSegments);
                else
                {
                    SendAbort(address, invokeId, BacnetAbortReason.OTHER);
                    //ErrorResponse(address, service, invokeId, BacnetErrorClasses.ERROR_CLASS_SERVICES, BacnetErrorCodes.ERROR_CODE_ABORT_OTHER);
                    Log.Warn("Couldn't decode SubscribeCOVProperty");
                }
            }
            else if (service == BacnetConfirmedServices.SERVICE_CONFIRMED_DEVICE_COMMUNICATION_CONTROL && OnDeviceCommunicationControl != null)
            {
                if (Services.DecodeDeviceCommunicationControl(buffer, offset, length, out var timeDuration, out var enableDisable, out var password) >= 0)
                    OnDeviceCommunicationControl(this, address, invokeId, timeDuration, enableDisable, password, maxSegments);
                else
                {
                    SendAbort(address, invokeId, BacnetAbortReason.OTHER);
                    //ErrorResponse(address, service, invokeId, BacnetErrorClasses.ERROR_CLASS_SERVICES, BacnetErrorCodes.ERROR_CODE_ABORT_OTHER);
                    Log.Warn("Couldn't decode DeviceCommunicationControl");
                }
            }
            else if (service == BacnetConfirmedServices.SERVICE_CONFIRMED_REINITIALIZE_DEVICE && OnReinitializedDevice != null)
            {
                if (Services.DecodeReinitializeDevice(buffer, offset, length, out var state, out var password) >= 0)
                    OnReinitializedDevice(this, address, invokeId, state, password, maxSegments);
                else
                {
                    SendAbort(address, invokeId, BacnetAbortReason.OTHER);
                    //ErrorResponse(address, service, invokeId, BacnetErrorClasses.ERROR_CLASS_SERVICES, BacnetErrorCodes.ERROR_CODE_ABORT_OTHER);
                    Log.Warn("Couldn't decode ReinitializeDevice");
                }
            }
            else if (service == BacnetConfirmedServices.SERVICE_CONFIRMED_EVENT_NOTIFICATION && OnEventNotify != null) // F. Chaxel
            {
                if (Services.DecodeEventNotifyData(buffer, offset, length, out var eventData) >= 0)
                {
                    OnEventNotify(this, address, invokeId, eventData, true);
                }
                else
                {
                    SendAbort(address, invokeId, BacnetAbortReason.OTHER);
                    //ErrorResponse(address, service, invokeId, BacnetErrorClasses.ERROR_CLASS_SERVICES, BacnetErrorCodes.ERROR_CODE_ABORT_OTHER);
                    Log.Warn("Couldn't decode confirmed Event/Alarm Notification");
                }
            }
            else if (service == BacnetConfirmedServices.SERVICE_CONFIRMED_READ_RANGE && OnReadRange != null)
            {
                if (Services.DecodeReadRange(buffer, offset, length, out var objectId, out var property, out var requestType, out var position, out var time, out var count) >= 0)
                    OnReadRange(this, address, invokeId, objectId, property, requestType, position, time, count, maxSegments);
                else
                {
                    SendAbort(address, invokeId, BacnetAbortReason.OTHER);
                    //ErrorResponse(address, service, invokeId, BacnetErrorClasses.ERROR_CLASS_SERVICES, BacnetErrorCodes.ERROR_CODE_ABORT_OTHER);
                    Log.Warn("Couldn't decode ReadRange");
                }
            }
            else if (service == BacnetConfirmedServices.SERVICE_CONFIRMED_CREATE_OBJECT && OnCreateObjectRequest != null)
            {
                if (Services.DecodeCreateObject(address, buffer, offset, length, out var objectId, out var values) >= 0)
                    OnCreateObjectRequest(this, address, invokeId, objectId, values, maxSegments);
                else
                {
                    SendAbort(address, invokeId, BacnetAbortReason.OTHER);
                    //ErrorResponse(address, service, invokeId, BacnetErrorClasses.ERROR_CLASS_SERVICES, BacnetErrorCodes.ERROR_CODE_ABORT_OTHER);
                    Log.Warn("Couldn't decode CreateObject");
                }
            }
            else if (service == BacnetConfirmedServices.SERVICE_CONFIRMED_DELETE_OBJECT && OnDeleteObjectRequest != null)
            {
                if (Services.DecodeDeleteObject(buffer, offset, length, out var objectId) >= 0)
                    OnDeleteObjectRequest(this, address, invokeId, objectId, maxSegments);
                else
                {
                    SendAbort(address, invokeId, BacnetAbortReason.OTHER);
                    //ErrorResponse(address, service, invokeId, BacnetErrorClasses.ERROR_CLASS_SERVICES, BacnetErrorCodes.ERROR_CODE_ABORT_OTHER);
                    Log.Warn("Couldn't decode DecodeDeleteObject");
                }
            }
            else if (service == BacnetConfirmedServices.SERVICE_CONFIRMED_GET_ALARM_SUMMARY && OnGetAlarmSummaryOrEventInformation != null)
            {
                // DAL -- added the core code required but since I couldn't test it we just reject this service
                // rejecting it shouldn't be too bad a thing since GetAlarmSummary has been retired anyway...
                // if someone needs it they can uncomment the related code and test.
#if false
                    BacnetObjectId objectId = default(BacnetObjectId);
                    objectId.Type = BacnetObjectTypes.MAX_BACNET_OBJECT_TYPE;
                    if (Services.DecodeAlarmSummaryOrEventRequest(buffer, offset, length, false, ref objectId) >= 0)
                    {
                        OnGetAlarmSummaryOrEventInformation(this, address, invokeId, false, objectId, maxAdpu, maxSegments);
                    }
                    else
                    {
                        // DAL
                        SendAbort(address, invokeId, BacnetAbortReason.OTHER);
                        //ErrorResponse(address, service, invokeId, BacnetErrorClasses.ERROR_CLASS_SERVICES, BacnetErrorCodes.ERROR_CODE_ABORT_OTHER);
                        Log.Warn("Couldn't decode GetAlarmSummary");
                    }
#else
                SendConfirmedServiceReject(address, invokeId, BacnetRejectReason.RECOGNIZED_SERVICE); // should be unrecognized but this is the way it was spelled..
#endif
            }
            else if (service == BacnetConfirmedServices.SERVICE_CONFIRMED_GET_EVENT_INFORMATION && OnGetAlarmSummaryOrEventInformation != null)
            {
                BacnetObjectId objectId = default;
                objectId.Type = BacnetObjectTypes.MAX_BACNET_OBJECT_TYPE;
                if (Services.DecodeAlarmSummaryOrEventRequest(buffer, offset, length, true, ref objectId) >= 0)
                {
                    OnGetAlarmSummaryOrEventInformation(this, address, invokeId, true, objectId, maxAdpu, maxSegments);
                }
                else
                {
                    SendAbort(address, invokeId, BacnetAbortReason.OTHER);
                    //ErrorResponse(address, service, invokeId, BacnetErrorClasses.ERROR_CLASS_SERVICES, BacnetErrorCodes.ERROR_CODE_ABORT_OTHER);
                    Log.Warn("Couldn't decode GetEventInformation");
                }
            }
            else if (service == BacnetConfirmedServices.SERVICE_CONFIRMED_ACKNOWLEDGE_ALARM && OnAlarmAcknowledge != null)
            {
                if (Services.DecodeAlarmAcknowledge(buffer, offset, length, out uint ackProcessIdentifier, out BacnetObjectId eventObjectIdentifier, out uint eventStateAcked, out string ackSource, out BacnetGenericTime eventTimeStamp, out BacnetGenericTime ackTimeStamp) >= 0)
                {
                    OnAlarmAcknowledge(this, address, invokeId, ackProcessIdentifier, eventObjectIdentifier, eventStateAcked, ackSource, eventTimeStamp, ackTimeStamp);
                }
                else
                {
                    SendAbort(address, invokeId, BacnetAbortReason.OTHER);
                    //ErrorResponse(address, service, invokeId, BacnetErrorClasses.ERROR_CLASS_SERVICES, BacnetErrorCodes.ERROR_CODE_ABORT_OTHER);
                    Log.Warn("Couldn't decode AlarmAcknowledge");
                }
            }
            else
            {
                SendConfirmedServiceReject(address, invokeId, BacnetRejectReason.RECOGNIZED_SERVICE); // should be unrecognized but this is the way it was spelled..
                Log.Debug($"Confirmed service not handled: {service}");
            }
        }
        catch (Exception ex)
        {
            SendAbort(address, invokeId, BacnetAbortReason.OTHER);
            //ErrorResponse(address, service, invokeId, BacnetErrorClasses.ERROR_CLASS_DEVICE, BacnetErrorCodes.ERROR_CODE_ABORT_OTHER);
            Log.Error("Error in ProcessConfirmedServiceRequest", ex);
        }
    }

    public delegate void UnconfirmedServiceRequestHandler(BacnetClient sender, BacnetAddress adr, BacnetPduTypes type, BacnetUnconfirmedServices service, byte[] buffer, int offset, int length);
    public event UnconfirmedServiceRequestHandler OnUnconfirmedServiceRequest;
    public delegate void WhoHasHandler(BacnetClient sender, BacnetAddress adr, int lowLimit, int highLimit, BacnetObjectId? objId, string objName);
    public event WhoHasHandler OnWhoHas;
    public delegate void IamHandler(BacnetClient sender, BacnetAddress adr, uint deviceId, uint maxAPDU, BacnetSegmentations segmentation, ushort vendorId);
    public event IamHandler OnIam;
    public delegate void WhoIsHandler(BacnetClient sender, BacnetAddress adr, int lowLimit, int highLimit);
    public event WhoIsHandler OnWhoIs;
    public delegate void TimeSynchronizeHandler(BacnetClient sender, BacnetAddress adr, DateTime dateTime, bool utc);
    public event TimeSynchronizeHandler OnTimeSynchronize;
    public delegate void IHaveHandler(BacnetClient sender, BacnetAddress adr, BacnetIHaveData data);
    public event IHaveHandler OnIHave;

    //used by both 'confirmed' and 'unconfirmed' notify
    public delegate void COVNotificationHandler(BacnetClient sender, BacnetAddress adr, byte invokeId, uint subscriberProcessIdentifier, BacnetObjectId initiatingDeviceIdentifier, BacnetObjectId monitoredObjectIdentifier, uint timeRemaining, bool needConfirm, ICollection<BacnetPropertyValue> values, BacnetMaxSegments maxSegments);
    public event COVNotificationHandler OnCOVNotification;

    protected void ProcessUnconfirmedServiceRequest(BacnetAddress address, BacnetPduTypes type, BacnetUnconfirmedServices service, byte[] buffer, int offset, int length)
    {
        try
        {
            Log.Debug("UnconfirmedServiceRequest");
            OnUnconfirmedServiceRequest?.Invoke(this, address, type, service, buffer, offset, length);
            if (service == BacnetUnconfirmedServices.SERVICE_UNCONFIRMED_I_AM && OnIam != null)
            {
                if (Services.DecodeIamBroadcast(buffer, offset, out var deviceId, out var maxAdpu, out var segmentation, out var vendorId) >= 0)
                    OnIam(this, address, deviceId, maxAdpu, segmentation, vendorId);
                else
                    Log.Warn("Couldn't decode IamBroadcast");
            }
            else if (service == BacnetUnconfirmedServices.SERVICE_UNCONFIRMED_WHO_IS && OnWhoIs != null)
            {
                if (Services.DecodeWhoIsBroadcast(buffer, offset, length, out var lowLimit, out var highLimit) >= 0)
                    OnWhoIs(this, address, lowLimit, highLimit);
                else
                    Log.Warn("Couldn't decode WhoIsBroadcast");
            }
            else if (service == BacnetUnconfirmedServices.SERVICE_UNCONFIRMED_WHO_HAS && OnWhoHas != null)
            {
                if (Services.DecodeWhoHasBroadcast(buffer, offset, length, out var lowLimit, out var highLimit, out var objId, out var objName) >= 0)
                    OnWhoHas(this, address, lowLimit, highLimit, objId, objName);
                else
                    Log.Warn("Couldn't decode WhoHasBroadcast");
            }
            else if (service == BacnetUnconfirmedServices.SERVICE_UNCONFIRMED_COV_NOTIFICATION && OnCOVNotification != null)
            {
                if (Services.DecodeCOVNotifyUnconfirmed(address, buffer, offset, length, out var subscriberProcessIdentifier, out var initiatingDeviceIdentifier, out var monitoredObjectIdentifier, out var timeRemaining, out var values) >= 0)
                    OnCOVNotification(this, address, 0, subscriberProcessIdentifier, initiatingDeviceIdentifier, monitoredObjectIdentifier, timeRemaining, false, values, BacnetMaxSegments.MAX_SEG0);
                else
                    Log.Warn("Couldn't decode COVNotifyUnconfirmed");
            }
            else if (service == BacnetUnconfirmedServices.SERVICE_UNCONFIRMED_TIME_SYNCHRONIZATION && OnTimeSynchronize != null)
            {
                if (Services.DecodeTimeSync(buffer, offset, length, out var dateTime) >= 0)
                    OnTimeSynchronize(this, address, dateTime, false);
                else
                    Log.Warn("Couldn't decode TimeSynchronize");
            }
            else if (service == BacnetUnconfirmedServices.SERVICE_UNCONFIRMED_UTC_TIME_SYNCHRONIZATION && OnTimeSynchronize != null)
            {
                if (Services.DecodeTimeSync(buffer, offset, length, out var dateTime) >= 0)
                    OnTimeSynchronize(this, address, dateTime, true);
                else
                    Log.Warn("Couldn't decode TimeSynchronize");
            }
            else if (service == BacnetUnconfirmedServices.SERVICE_UNCONFIRMED_EVENT_NOTIFICATION && OnEventNotify != null)
            {
                if (Services.DecodeEventNotifyData(buffer, offset, length, out var eventData) >= 0)
                    OnEventNotify(this, address, 0, eventData, false);
                else
                    Log.Warn("Couldn't decode unconfirmed Event/Alarm Notification");
            }
            else if (service == BacnetUnconfirmedServices.SERVICE_UNCONFIRMED_I_HAVE && OnIHave != null)
            {
                if (Services.DecodeIHaveBroadcast(buffer, offset, length, out var iHaveData) >= 0)
                    OnIHave(this, address, iHaveData);
                else
                    Log.Warn("Couldn't decode IHave");
            }
            else
            {
                Log.Debug($"Unconfirmed service not handled: {service}");
                // SendUnConfirmedServiceReject(adr); ? exists ?
            }
        }
        catch (Exception ex)
        {
            Log.Error("Error in ProcessUnconfirmedServiceRequest", ex);
        }
    }

    public delegate void SimpleAckHandler(BacnetClient sender, BacnetAddress adr, BacnetPduTypes type, BacnetConfirmedServices service, byte invokeId, byte[] data, int dataOffset, int dataLength);
    public event SimpleAckHandler OnSimpleAck;

    protected void ProcessSimpleAck(BacnetAddress adr, BacnetPduTypes type, BacnetConfirmedServices service, byte invokeId, byte[] buffer, int offset, int length)
    {
        try
        {
            Log.Debug($"Received SimpleAck for {service}");
            OnSimpleAck?.Invoke(this, adr, type, service, invokeId, buffer, offset, length);
        }
        catch (Exception ex)
        {
            Log.Error("Error in ProcessSimpleAck", ex);
        }
    }

    public delegate void ComplexAckHandler(BacnetClient sender, BacnetAddress adr, BacnetPduTypes type, BacnetConfirmedServices service, byte invokeId, byte[] buffer, int offset, int length);
    public event ComplexAckHandler OnComplexAck;

    protected void ProcessComplexAck(BacnetAddress adr, BacnetPduTypes type, BacnetConfirmedServices service, byte invokeId, byte[] buffer, int offset, int length)
    {
        try
        {
            Log.Debug($"Received ComplexAck for {service}");
            OnComplexAck?.Invoke(this, adr, type, service, invokeId, buffer, offset, length);
        }
        catch (Exception ex)
        {
            Log.Error($"Error in {nameof(ProcessComplexAck)}", ex);
        }
    }

    public delegate void ErrorHandler(BacnetClient sender, BacnetAddress adr, BacnetPduTypes type, BacnetConfirmedServices service, byte invokeId, BacnetErrorClasses errorClass, BacnetErrorCodes errorCode, byte[] buffer, int offset, int length);
    public event ErrorHandler OnError;

    protected void ProcessError(BacnetAddress adr, BacnetPduTypes type, BacnetConfirmedServices service, byte invokeId, byte[] buffer, int offset, int length)
    {
        try
        {
            if (Services.DecodeError(buffer, offset, length, out var errorClass, out var errorCode) < 0)
                Log.Warn("Couldn't decode received Error");

            Log.Debug($"Received Error {errorClass} {errorCode}");
            OnError?.Invoke(this, adr, type, service, invokeId, errorClass, errorCode, buffer, offset, length);
        }
        catch (Exception ex)
        {
            Log.Error($"Error in {nameof(ProcessError)}", ex);
        }
    }

    public delegate void AbortHandler(BacnetClient sender, BacnetAddress adr, BacnetPduTypes type, byte invokeId, BacnetAbortReason reason, byte[] buffer, int offset, int length);
    public event AbortHandler OnAbort;

    protected void ProcessAbort(BacnetAddress adr, BacnetPduTypes type, byte invokeId, BacnetAbortReason reason, byte[] buffer, int offset, int length)
    {
        try
        {
            Log.Debug($"Received Abort, reason: {reason}");
            OnAbort?.Invoke(this, adr, type, invokeId, reason, buffer, offset, length);
        }
        catch (Exception ex)
        {
            Log.Error("Error in ProcessAbort", ex);
        }
    }

    public delegate void RejectHandler(BacnetClient sender, BacnetAddress adr, BacnetPduTypes type, byte invokeId, BacnetRejectReason reason, byte[] buffer, int offset, int length);
    public event RejectHandler OnReject;

    protected void ProcessReject(BacnetAddress adr, BacnetPduTypes type, byte invokeId, BacnetRejectReason reason, byte[] buffer, int offset, int length)
    {
        try
        {
            Log.Debug($"Received Reject, reason: {reason}");
            OnReject?.Invoke(this, adr, type, invokeId, reason, buffer, offset, length);
        }
        catch (Exception ex)
        {
            Log.Error("Error in ProcessReject", ex);
        }
    }

    public delegate void SegmentAckHandler(BacnetClient sender, BacnetAddress adr, BacnetPduTypes type, byte originalInvokeId, byte sequenceNumber, byte actualWindowSize, byte[] buffer, int offset, int length);
    public event SegmentAckHandler OnSegmentAck;

    protected void ProcessSegmentAck(BacnetAddress adr, BacnetPduTypes type, byte originalInvokeId, byte sequenceNumber, byte actualWindowSize, byte[] buffer, int offset, int length)
    {
        try
        {
            Log.Debug("Received SegmentAck");
            OnSegmentAck?.Invoke(this, adr, type, originalInvokeId, sequenceNumber, actualWindowSize, buffer, offset, length);
        }
        catch (Exception ex)
        {
            Log.Error("Error in ProcessSegmentAck", ex);
        }
    }

    public delegate void SegmentHandler(BacnetClient sender, BacnetAddress adr, BacnetPduTypes type, BacnetConfirmedServices service, byte invokeId, BacnetMaxSegments maxSegments, BacnetMaxAdpu maxAdpu, byte sequenceNumber, byte[] buffer, int offset, int length);
    public event SegmentHandler OnSegment;

    private void ProcessSegment(BacnetAddress address, BacnetPduTypes type, BacnetConfirmedServices service, byte invokeId, BacnetMaxSegments maxSegments, BacnetMaxAdpu maxAdpu, bool server, byte sequenceNumber, byte proposedWindowNumber, byte[] buffer, int offset, int length)
    {
        if (!_locksPerInvokeId.TryGetValue(invokeId, out var lockObj))
        {
            lockObj = new object();
            _locksPerInvokeId[invokeId] = lockObj;
        }

        lock (lockObj)
        {
            ProcessSegmentLocked(address, type, service, invokeId, maxSegments, maxAdpu, server, sequenceNumber,
                proposedWindowNumber, buffer, offset, length);
        }
    }

    private void ProcessSegmentLocked(BacnetAddress adr, BacnetPduTypes type, BacnetConfirmedServices service,
        byte invokeId, BacnetMaxSegments maxSegments, BacnetMaxAdpu maxAdpu, bool server, byte sequenceNumber,
        byte proposedWindowNumber, byte[] buffer, int offset, int length)
    {
        Log.Trace($@"Processing Segment #{sequenceNumber} of invoke-id #{invokeId}");

        if (!_segmentsPerInvokeId.ContainsKey(invokeId))
            _segmentsPerInvokeId[invokeId] = new List<Tuple<byte, byte[]>>();

        if (!_expectedSegmentsPerInvokeId.ContainsKey(invokeId))
            _expectedSegmentsPerInvokeId[invokeId] = byte.MaxValue;

        var moreFollows = (type & BacnetPduTypes.MORE_FOLLOWS) == BacnetPduTypes.MORE_FOLLOWS;

        if (!moreFollows)
            _expectedSegmentsPerInvokeId[invokeId] = (byte)(sequenceNumber + 1);

        //send ACK
        if (sequenceNumber % proposedWindowNumber == 0 || !moreFollows)
        {
            if (ForceWindowSize)
                proposedWindowNumber = ProposedWindowSize;

            SegmentAckResponse(adr.SwapDirection(), false, server, invokeId, sequenceNumber, proposedWindowNumber);
        }

        //Send on
        OnSegment?.Invoke(this, adr, type, service, invokeId, maxSegments, maxAdpu, sequenceNumber, buffer, offset, length);

        //default segment assembly. We run this seperately from the above handler, to make sure that it comes after!
        if (DefaultSegmentationHandling)
            PerformDefaultSegmentHandling(adr, type, service, invokeId, maxSegments, maxAdpu, sequenceNumber, buffer, offset, length);
    }

    /// <summary>
    /// This is a simple handling that stores all segments in memory and assembles them when done
    /// </summary>
    private void PerformDefaultSegmentHandling(BacnetAddress adr, BacnetPduTypes type, BacnetConfirmedServices service, byte invokeId, BacnetMaxSegments maxSegments, BacnetMaxAdpu maxAdpu, byte sequenceNumber, byte[] buffer, int offset, int length)
    {
        var segments = _segmentsPerInvokeId[invokeId];

        if (sequenceNumber == 0)
        {
            //copy buffer + encode new adpu header
            type &= ~BacnetPduTypes.SEGMENTED_MESSAGE;
            var confirmedServiceRequest = (type & BacnetPduTypes.PDU_TYPE_MASK) == BacnetPduTypes.PDU_TYPE_CONFIRMED_SERVICE_REQUEST;
            var adpuHeaderLen = confirmedServiceRequest ? 4 : 3;

            var copy = new byte[length + adpuHeaderLen];
            Array.Copy(buffer, offset, copy, adpuHeaderLen, length);
            var encodedBuffer = new EncodeBuffer(copy, 0);

            if (confirmedServiceRequest)
                APDU.EncodeConfirmedServiceRequest(encodedBuffer, type, service, maxSegments, maxAdpu, invokeId);
            else
                APDU.EncodeComplexAck(encodedBuffer, type, service, invokeId);

            segments.Add(Tuple.Create(sequenceNumber, copy)); // doesn't include BVLC or NPDU
        }
        else
        {
            //copy only content part
            segments.Add(Tuple.Create(sequenceNumber, buffer.Skip(offset).Take(length).ToArray()));
        }

        //process when finished
        if (segments.Count < _expectedSegmentsPerInvokeId[invokeId])
            return;

        //assemble whole part
        var apduBuffer = segments.OrderBy(s => s.Item1).SelectMany(s => s.Item2).ToArray();
        segments.Clear();
        _expectedSegmentsPerInvokeId[invokeId] = byte.MaxValue;

        //process
        ProcessApdu(adr, type, apduBuffer, 0, apduBuffer.Length);
    }

    private void ProcessApdu(BacnetAddress adr, BacnetPduTypes type, byte[] buffer, int offset, int length)
    {
        switch (type & BacnetPduTypes.PDU_TYPE_MASK)
        {
            case BacnetPduTypes.PDU_TYPE_UNCONFIRMED_SERVICE_REQUEST:
                {
                    var apduHeaderLen = APDU.DecodeUnconfirmedServiceRequest(buffer, offset, out type, out var service);
                    offset += apduHeaderLen;
                    length -= apduHeaderLen;
                    ProcessUnconfirmedServiceRequest(adr, type, service, buffer, offset, length);
                }
                break;

            case BacnetPduTypes.PDU_TYPE_SIMPLE_ACK:
                {
                    var apduHeaderLen = APDU.DecodeSimpleAck(buffer, offset, out type, out var service, out var invokeId);
                    offset += apduHeaderLen;
                    length -= apduHeaderLen;
                    ProcessSimpleAck(adr, type, service, invokeId, buffer, offset, length);
                }
                break;

            case BacnetPduTypes.PDU_TYPE_COMPLEX_ACK:
                {
                    var apduHeaderLen = APDU.DecodeComplexAck(buffer, offset, out type, out var service, out var invokeId,
                        out var sequenceNumber, out var proposedWindowNumber);

                    offset += apduHeaderLen;
                    length -= apduHeaderLen;
                    if ((type & BacnetPduTypes.SEGMENTED_MESSAGE) == 0) //don't process segmented messages here
                    {
                        ProcessComplexAck(adr, type, service, invokeId, buffer, offset, length);
                    }
                    else
                    {
                        ProcessSegment(adr, type, service, invokeId, BacnetMaxSegments.MAX_SEG0, BacnetMaxAdpu.MAX_APDU50, false,
                            sequenceNumber, proposedWindowNumber, buffer, offset, length);
                    }
                }
                break;

            case BacnetPduTypes.PDU_TYPE_SEGMENT_ACK:
                {
                    var apduHeaderLen = APDU.DecodeSegmentAck(buffer, offset, out type, out var originalInvokeId,
                        out var sequenceNumber, out var actualWindowSize);

                    offset += apduHeaderLen;
                    length -= apduHeaderLen;
                    _lastSegmentAck.Set(adr, originalInvokeId, sequenceNumber, actualWindowSize);
                    ProcessSegmentAck(adr, type, originalInvokeId, sequenceNumber, actualWindowSize, buffer, offset, length);
                }
                break;

            case BacnetPduTypes.PDU_TYPE_ERROR:
                {
                    var apduHeaderLen = APDU.DecodeError(buffer, offset, out type, out var service, out var invokeId);
                    offset += apduHeaderLen;
                    length -= apduHeaderLen;
                    ProcessError(adr, type, service, invokeId, buffer, offset, length);
                }
                break;

            case BacnetPduTypes.PDU_TYPE_ABORT:
                {
                    var apduHeaderLen = APDU.DecodeAbort(buffer, offset, out type, out var invokeId, out var reason);
                    offset += apduHeaderLen;
                    length -= apduHeaderLen;
                    ProcessAbort(adr, type, invokeId, reason, buffer, offset, length);
                }
                break;

            case BacnetPduTypes.PDU_TYPE_REJECT:
                {
                    var apduHeaderLen = APDU.DecodeReject(buffer, offset, out type, out var invokeId, out var reason);
                    offset += apduHeaderLen;
                    length -= apduHeaderLen;
                    ProcessReject(adr, type, invokeId, reason, buffer, offset, length);
                }
                break;

            case BacnetPduTypes.PDU_TYPE_CONFIRMED_SERVICE_REQUEST:
                {
                    var apduHeaderLen = APDU.DecodeConfirmedServiceRequest(buffer, offset, out type, out var service,
                        out var maxSegments, out var maxAdpu, out var invokeId, out var sequenceNumber, out var proposedWindowNumber);

                    offset += apduHeaderLen;
                    length -= apduHeaderLen;

                    if ((type & BacnetPduTypes.SEGMENTED_MESSAGE) == 0) //don't process segmented messages here
                    {
                        ProcessConfirmedServiceRequest(adr, type, service, maxSegments, maxAdpu, invokeId, buffer, offset, length);
                    }
                    else
                    {
                        ProcessSegment(adr, type, service, invokeId, maxSegments, maxAdpu, true, sequenceNumber, proposedWindowNumber, buffer, offset, length);
                    }
                }
                break;

            default:
                Log.Warn($"Something else arrived: {type}");
                break;
        }
    }

    public void SendNetworkMessage(BacnetAddress adr, byte[] buffer, int bufLen, BacnetNetworkMessageTypes messageType, ushort vendorId = 0)
    {
        if (adr == null)
        {
            adr = Transport.GetBroadcastAddress();
        }
        var b = GetEncodeBuffer(Transport.HeaderLength);
        NPDU.Encode(b, BacnetNpduControls.NetworkLayerMessage, adr, null, 255, messageType, vendorId);
        b.Add(buffer, bufLen);
        Transport.Send(b.buffer, Transport.HeaderLength, b.offset - Transport.HeaderLength, adr, false, 0);
    }

    public void SendIAmRouterToNetwork(ushort[] networks)
    {
        var b = GetEncodeBuffer(0);
        for (int i = 0; i < networks.Length; i++)
        {
            ASN1.encode_unsigned16(b, networks[i]);
        }
        SendNetworkMessage(null, b.buffer, b.offset, BacnetNetworkMessageTypes.NETWORK_MESSAGE_I_AM_ROUTER_TO_NETWORK);
    }

    public void SendInitializeRoutingTableAck(BacnetAddress adr, ushort[] networks)
    {
        var b = GetEncodeBuffer(0);
        if (networks != null)
        {
            for (int i = 0; i < networks.Length; i++)
            {
                ASN1.encode_unsigned16(b, networks[i]);
            }
        }
        SendNetworkMessage(adr, b.buffer, b.offset, BacnetNetworkMessageTypes.NETWORK_MESSAGE_INIT_RT_TABLE_ACK);
    }

    public void SendRejectToNetwork(BacnetAddress adr, ushort[] networks)
    {
        var b = GetEncodeBuffer(0);
        /* Sending our DNET doesn't make a lot of sense, does it? */
        for (int i = 0; i < networks.Length; i++)
        {
            ASN1.encode_unsigned16(b, networks[i]);
        }
        SendNetworkMessage(adr, b.buffer, b.offset, BacnetNetworkMessageTypes.NETWORK_MESSAGE_REJECT_MESSAGE_TO_NETWORK);
    }

    public delegate void NetworkMessageHandler(BacnetClient sender, BacnetAddress adr, BacnetNpduControls npduFunction, BacnetNetworkMessageTypes npduMessageType, byte[] buffer, int offset, int messageLength);
    public event NetworkMessageHandler OnNetworkMessage;
    public delegate void WhoIsRouterToNetworkHandler(BacnetClient sender, BacnetAddress adr, BacnetNpduControls npduFunction, byte[] buffer, int offset, int messageLength);
    public event WhoIsRouterToNetworkHandler OnWhoIsRouterToNetworkMessage;
    public delegate void IAmRouterToNetworkHandler(BacnetClient sender, BacnetAddress adr, BacnetNpduControls npduFunction, byte[] buffer, int offset, int messageLength);
    public event IAmRouterToNetworkHandler OnIAmRouterToNetworkMessage;
    public delegate void ICouldBeRouterToNetworkHandler(BacnetClient sender, BacnetAddress adr, BacnetNpduControls npduFunction, byte[] buffer, int offset, int messageLength);
    public event ICouldBeRouterToNetworkHandler OnICouldBeRouterToNetworkMessage;
    public delegate void RejectMessageToNetworkHandler(BacnetClient sender, BacnetAddress adr, BacnetNpduControls npduFunction, byte[] buffer, int offset, int messageLength);
    public event RejectMessageToNetworkHandler OnRejectMessageToNetworkMessage;
    public delegate void RouterBusyToNetworkHandler(BacnetClient sender, BacnetAddress adr, BacnetNpduControls npduFunction, byte[] buffer, int offset, int messageLength);
    public event RouterBusyToNetworkHandler OnRouterBusyToNetworkMessage;
    public delegate void RouterAvailableToNetworkHandler(BacnetClient sender, BacnetAddress adr, BacnetNpduControls npduFunction, byte[] buffer, int offset, int messageLength);
    public event RouterAvailableToNetworkHandler OnRouterAvailableToNetworkMessage;
    public delegate void InitRtTableToNetworkHandler(BacnetClient sender, BacnetAddress adr, BacnetNpduControls npduFunction, byte[] buffer, int offset, int messageLength);
    public event InitRtTableToNetworkHandler OnInitRtTableToNetworkMessage;
    public delegate void InitRtTableAckToNetworkHandler(BacnetClient sender, BacnetAddress adr, BacnetNpduControls npduFunction, byte[] buffer, int offset, int messageLength);
    public event InitRtTableAckToNetworkHandler OnInitRtTableAckToNetworkMessage;
    public delegate void EstablishConnectionToNetworkHandler(BacnetClient sender, BacnetAddress adr, BacnetNpduControls npduFunction, byte[] buffer, int offset, int messageLength);
    public event EstablishConnectionToNetworkHandler OnEstablishConnectionToNetworkMessage;
    public delegate void DisconnectConnectionToNetworkHandler(BacnetClient sender, BacnetAddress adr, BacnetNpduControls npduFunction, byte[] buffer, int offset, int messageLength);
    public event DisconnectConnectionToNetworkHandler OnDisconnectConnectionToNetworkMessage;
    public delegate void UnrecognizedNetworkHandler(BacnetClient sender, BacnetAddress adr, BacnetNpduControls npduFunction, byte[] buffer, int offset, int messageLength);
    public event UnrecognizedNetworkHandler OnUnrecognizedNetworkMessage;

    private void ProcessNetworkMessage(BacnetAddress adr, BacnetNpduControls npduFunction, BacnetNetworkMessageTypes npduMessageType, byte[] buffer, int offset, int messageLength)
    {
        // DAL I don't want to make a generic router, but I do want to put in enough infrastructure
        // that I can build on it to route multiple devices in the normal bacnet way.
        OnNetworkMessage?.Invoke(this, adr, npduFunction, npduMessageType, buffer, offset, messageLength);
        switch (npduMessageType)
        {
            case BacnetNetworkMessageTypes.NETWORK_MESSAGE_WHO_IS_ROUTER_TO_NETWORK:
                OnWhoIsRouterToNetworkMessage?.Invoke(this, adr, npduFunction, buffer, offset, messageLength);
                break;
            case BacnetNetworkMessageTypes.NETWORK_MESSAGE_I_AM_ROUTER_TO_NETWORK:
                OnIAmRouterToNetworkMessage?.Invoke(this, adr, npduFunction, buffer, offset, messageLength);
                break;
            case BacnetNetworkMessageTypes.NETWORK_MESSAGE_I_COULD_BE_ROUTER_TO_NETWORK:
                OnICouldBeRouterToNetworkMessage?.Invoke(this, adr, npduFunction, buffer, offset, messageLength);
                break;
            case BacnetNetworkMessageTypes.NETWORK_MESSAGE_REJECT_MESSAGE_TO_NETWORK:
                OnRejectMessageToNetworkMessage?.Invoke(this, adr, npduFunction, buffer, offset, messageLength);
                break;
            case BacnetNetworkMessageTypes.NETWORK_MESSAGE_ROUTER_BUSY_TO_NETWORK:
                OnRouterBusyToNetworkMessage?.Invoke(this, adr, npduFunction, buffer, offset, messageLength);
                break;
            case BacnetNetworkMessageTypes.NETWORK_MESSAGE_ROUTER_AVAILABLE_TO_NETWORK:
                OnRouterAvailableToNetworkMessage?.Invoke(this, adr, npduFunction, buffer, offset, messageLength);
                break;
            case BacnetNetworkMessageTypes.NETWORK_MESSAGE_INIT_RT_TABLE:
                OnInitRtTableToNetworkMessage?.Invoke(this, adr, npduFunction, buffer, offset, messageLength);
                break;
            case BacnetNetworkMessageTypes.NETWORK_MESSAGE_INIT_RT_TABLE_ACK:
                OnInitRtTableAckToNetworkMessage?.Invoke(this, adr, npduFunction, buffer, offset, messageLength);
                break;
            case BacnetNetworkMessageTypes.NETWORK_MESSAGE_ESTABLISH_CONNECTION_TO_NETWORK:
                OnEstablishConnectionToNetworkMessage?.Invoke(this, adr, npduFunction, buffer, offset, messageLength);
                break;
            case BacnetNetworkMessageTypes.NETWORK_MESSAGE_DISCONNECT_CONNECTION_TO_NETWORK:
                OnDisconnectConnectionToNetworkMessage?.Invoke(this, adr, npduFunction, buffer, offset, messageLength);
                break;
            default:
                /* An unrecognized message is bad; send an error response. */
                OnUnrecognizedNetworkMessage?.Invoke(this, adr, npduFunction, buffer, offset, messageLength);
                break;
        }
    }

    private void OnRecieve(IBacnetTransport sender, byte[] buffer, int offset, int msgLength, BacnetAddress remoteAddress)
    {
        try
        {
            if (Transport == null)
                return; //we're disposed 

            if (msgLength <= 0)
                return;

            // parse
            var npduLen = NPDU.Decode(buffer, offset, out var npduFunction, out var destination, out var source, out _, out var npduMessageType, out _);

            remoteAddress.RoutedSource = source;
            remoteAddress.RoutedDestination = destination;

            if (npduLen <= 0)
                return;

            offset += npduLen;
            msgLength -= npduLen;

            if (msgLength < 0) // could be 0 for an already parsed
                return;

            if (npduFunction.HasFlag(BacnetNpduControls.NetworkLayerMessage))
            {
                Log.Info("Network Layer message received");
                ProcessNetworkMessage(remoteAddress, npduFunction, npduMessageType, buffer, offset, msgLength);
                return;
            }

            if (msgLength <= 0)
                return;

            var apduType = APDU.GetDecodedType(buffer, offset);
            ProcessApdu(remoteAddress, apduType, buffer, offset, msgLength);
        }
        catch (Exception ex)
        {
            Log.Error("Error in OnRecieve", ex);
        }
    }

    public void RegisterAsForeignDevice(string bbmdIp, short ttl, int port = DefaultUdpPort)
    {
        try
        {
            var ep = new IPEndPoint(IPAddress.Parse(bbmdIp), port);
            var sent = false;

            switch (Transport)
            {
                case BacnetIpUdpProtocolTransport t:
                    sent = t.SendRegisterAsForeignDevice(ep, ttl);
                    break;

                case BacnetIpV6UdpProtocolTransport t:
                    sent = t.SendRegisterAsForeignDevice(ep, ttl);
                    break;
            }

            if (sent)
                Log.Debug($"Sending Register as a Foreign Device to {bbmdIp}");
            else
                Log.Warn("The given address do not match with the IP version");
        }
        catch (Exception ex)
        {
            Log.Error("Error on RegisterAsForeignDevice (Wrong Transport, not IP ?)", ex);
        }
    }

    public void RemoteWhoIs(string bbmdIp, int port = DefaultUdpPort, int lowLimit = -1, int highLimit = -1, BacnetAddress source = null)
    {
        try
        {
            var ep = new IPEndPoint(IPAddress.Parse(bbmdIp), port);

            var b = GetEncodeBuffer(Transport.HeaderLength);
            var broadcast = Transport.GetBroadcastAddress();
            NPDU.Encode(b, BacnetNpduControls.PriorityNormalMessage, broadcast, source);
            APDU.EncodeUnconfirmedServiceRequest(b, BacnetPduTypes.PDU_TYPE_UNCONFIRMED_SERVICE_REQUEST, BacnetUnconfirmedServices.SERVICE_UNCONFIRMED_WHO_IS);
            Services.EncodeWhoIsBroadcast(b, lowLimit, highLimit);

            var sent = false;

            switch (Transport)
            {
                case BacnetIpUdpProtocolTransport t:
                    sent = t.SendRemoteWhois(b.buffer, ep, b.offset);
                    break;

                case BacnetIpV6UdpProtocolTransport t:
                    sent = t.SendRemoteWhois(b.buffer, ep, b.offset);
                    break;
            }

            if (sent)
                Log.Debug($"Sending Remote Whois to {bbmdIp}");
            else
                Log.Warn("The given address do not match with the IP version");
        }
        catch (Exception ex)
        {
            Log.Error("Error on Sending Whois to remote BBMD (Wrong Transport, not IP ?)", ex);
        }
    }

    public void WhoIs(int lowLimit = -1, int highLimit = -1, BacnetAddress receiver = null)
    {
        if (receiver == null)
        {
            // _receiver could be an unicast @ : for direct acces 
            // usefull on BIP for a known IP:Port, unknown device Id
            receiver = Transport.GetBroadcastAddress();
            Log.Debug("Broadcasting WhoIs");
        }
        else
        {
            Log.Debug($"Sending WhoIs to {receiver}");
        }

        var b = GetEncodeBuffer(Transport.HeaderLength);
        NPDU.Encode(b, BacnetNpduControls.PriorityNormalMessage, receiver.RoutedDestination ?? receiver, receiver.RoutedSource);
        APDU.EncodeUnconfirmedServiceRequest(b, BacnetPduTypes.PDU_TYPE_UNCONFIRMED_SERVICE_REQUEST, BacnetUnconfirmedServices.SERVICE_UNCONFIRMED_WHO_IS);
        Services.EncodeWhoIsBroadcast(b, lowLimit, highLimit);

        Transport.Send(b.buffer, Transport.HeaderLength, b.offset - Transport.HeaderLength, receiver, false, 0);
    }

    public void Iam(uint deviceId, BacnetSegmentations segmentation = BacnetSegmentations.SEGMENTATION_BOTH, BacnetAddress receiver = null)
    {
        if (receiver == null)
        {
            receiver = Transport.GetBroadcastAddress();
            Log.Debug($"Broadcasting Iam {deviceId}");
        }
        else
        {
            Log.Debug($"Sending Iam {deviceId} to {receiver}");
        }

        var b = GetEncodeBuffer(Transport.HeaderLength);
        NPDU.Encode(b, BacnetNpduControls.PriorityNormalMessage, receiver.RoutedDestination ?? receiver, receiver.RoutedSource);
        APDU.EncodeUnconfirmedServiceRequest(b, BacnetPduTypes.PDU_TYPE_UNCONFIRMED_SERVICE_REQUEST, BacnetUnconfirmedServices.SERVICE_UNCONFIRMED_I_AM);
        Services.EncodeIamBroadcast(b, deviceId, (uint)GetMaxApdu(), segmentation, VendorId);

        Transport.Send(b.buffer, Transport.HeaderLength, b.offset - Transport.HeaderLength, receiver, false, 0);
    }

    public void WhoHas(BacnetObjectId objId, int lowLimit = -1, int highLimit = -1, BacnetAddress receiver = null)
    {
        WhoHasCore(objId, null, lowLimit, highLimit, receiver);
    }

    public void WhoHas(string objName, int lowLimit = -1, int highLimit = -1, BacnetAddress receiver = null)
    {
        WhoHasCore(null, objName, lowLimit, highLimit, receiver);
    }

    private void WhoHasCore(BacnetObjectId? objId, string objName, int lowLimit, int highLimit, BacnetAddress receiver)
    {
        if (receiver == null)
        {
            receiver = Transport.GetBroadcastAddress();
            Log.Debug($"Broadcasting WhoHas {objId?.ToString() ?? objName}");
        }
        else
        {
            Log.Debug($"Sending WhoHas {objId?.ToString() ?? objName} to {receiver}");
        }

        var b = GetEncodeBuffer(Transport.HeaderLength);
        NPDU.Encode(b, BacnetNpduControls.PriorityNormalMessage, receiver.RoutedDestination ?? receiver, receiver.RoutedSource);
        APDU.EncodeUnconfirmedServiceRequest(b, BacnetPduTypes.PDU_TYPE_UNCONFIRMED_SERVICE_REQUEST, BacnetUnconfirmedServices.SERVICE_UNCONFIRMED_WHO_HAS);
        Services.EncodeWhoHasBroadcast(b, lowLimit, highLimit, objId, objName);

        Transport.Send(b.buffer, Transport.HeaderLength, b.offset - Transport.HeaderLength, receiver, false, 0);
    }

    // ReSharper disable once InconsistentNaming
    public void IHave(BacnetObjectId deviceId, BacnetObjectId objId, string objName, BacnetAddress source = null)
    {
        Log.Debug($"Broadcasting IHave {objName} {objId}");

        var b = GetEncodeBuffer(Transport.HeaderLength);
        var broadcast = Transport.GetBroadcastAddress();
        NPDU.Encode(b, BacnetNpduControls.PriorityNormalMessage, broadcast, source);
        APDU.EncodeUnconfirmedServiceRequest(b, BacnetPduTypes.PDU_TYPE_UNCONFIRMED_SERVICE_REQUEST, BacnetUnconfirmedServices.SERVICE_UNCONFIRMED_I_HAVE);
        Services.EncodeIhaveBroadcast(b, deviceId, objId, objName);

        Transport.Send(b.buffer, Transport.HeaderLength, b.offset - Transport.HeaderLength, broadcast, false, 0);
    }

    public void SendUnconfirmedEventNotification(BacnetAddress adr, BacnetEventNotificationData eventData, BacnetAddress source = null)
    {
        Log.Debug($"Sending Event Notification {eventData.eventType} {eventData.eventObjectIdentifier}");

        var b = GetEncodeBuffer(Transport.HeaderLength);
        NPDU.Encode(b, BacnetNpduControls.PriorityNormalMessage, adr, source);
        APDU.EncodeUnconfirmedServiceRequest(b, BacnetPduTypes.PDU_TYPE_UNCONFIRMED_SERVICE_REQUEST, BacnetUnconfirmedServices.SERVICE_UNCONFIRMED_EVENT_NOTIFICATION);
        Services.EncodeEventNotifyUnconfirmed(b, eventData);
        Transport.Send(b.buffer, Transport.HeaderLength, b.offset - Transport.HeaderLength, adr, false, 0);
    }

    public void SendConfirmedServiceReject(BacnetAddress adr, byte invokeId, BacnetRejectReason reason)
    {
        Log.Debug($"Sending Service reject: {reason}");

        var b = GetEncodeBuffer(Transport.HeaderLength);

        NPDU.Encode(b, BacnetNpduControls.PriorityNormalMessage, adr.RoutedDestination, adr.RoutedSource);
        APDU.EncodeError(b, BacnetPduTypes.PDU_TYPE_REJECT, (BacnetConfirmedServices)reason, invokeId);
        Transport.Send(b.buffer, Transport.HeaderLength, b.offset - Transport.HeaderLength, adr, false, 0);
    }

    public void SendAbort(BacnetAddress adr, byte invokeId, BacnetAbortReason reason)
    {
        Log.Debug($"Sending Service reject: {reason}");

        var b = GetEncodeBuffer(Transport.HeaderLength);

        NPDU.Encode(b, BacnetNpduControls.PriorityNormalMessage, adr.RoutedDestination, adr.RoutedSource);
        APDU.EncodeError(b, BacnetPduTypes.PDU_TYPE_ABORT, (BacnetConfirmedServices)reason, invokeId);
        Transport.Send(b.buffer, Transport.HeaderLength, b.offset - Transport.HeaderLength, adr, false, 0);
    }

    public void SynchronizeTime(BacnetAddress adr, DateTime dateTime, BacnetAddress source = null)
    {
        Log.Debug($"Sending Time Synchronize: {dateTime} {dateTime.Kind.ToString().ToUpper()}");

        var buffer = GetEncodeBuffer(Transport.HeaderLength);
        _messageFactory.CreateSynchronizeTime(buffer, adr, dateTime, source);
        Transport.Send(buffer.buffer, Transport.HeaderLength, buffer.offset - Transport.HeaderLength, adr, false, 0);
    }

    public void SynchronizeTime(BacnetAddress adr, BacnetDate bacnetDate, BacnetTime bacnetTime, bool isUtc, BacnetAddress source)
    {
        var buffer = GetEncodeBuffer(Transport.HeaderLength);
        _messageFactory.CreateSynchronizeTime(buffer, adr, bacnetDate, bacnetTime, isUtc, source);
        Transport.Send(buffer.buffer, Transport.HeaderLength, buffer.offset - Transport.HeaderLength, adr, false, 0);
    }

    public int GetMaxApdu()
    {
        return GetMaxApdu(Transport.MaxApduLength);
    }

    public int GetMaxApdu(BacnetMaxAdpu apduLength)
    {
        int maxAPDU;
        switch (apduLength)
        {
            case BacnetMaxAdpu.MAX_APDU1476:
                maxAPDU = 1476;
                break;
            case BacnetMaxAdpu.MAX_APDU1024:
                maxAPDU = 1024;
                break;
            case BacnetMaxAdpu.MAX_APDU480:
                maxAPDU = 480;
                break;
            case BacnetMaxAdpu.MAX_APDU206:
                maxAPDU = 206;
                break;
            case BacnetMaxAdpu.MAX_APDU128:
                maxAPDU = 128;
                break;
            case BacnetMaxAdpu.MAX_APDU50:
                maxAPDU = 50;
                break;
            default:
                throw new NotImplementedException();
        }

        //max udp payload IRL seems to differ from the expectations in BACnet
        //so we have to adjust it. (In order to fulfill the standard)
        const int maxNPDUHeaderLength = 4;       //usually it's '2', but it can also be more than '4'. Beware!
        return Math.Min(maxAPDU, Transport.MaxBufferLength - Transport.HeaderLength - maxNPDUHeaderLength);
    }

    public int GetFileBufferMaxSize()
    {
        //6 should be the max_apdu_header_length for Confirmed (with segmentation)
        //12 should be the max_atomic_write_file
        return GetMaxApdu() - 18;
    }

    private Exception OperationTimedOut([CallerMemberName] string name = "") => new TimeoutException($"Operation {name} timed out.");

    public void WriteFileRequest(BacnetAddress adr, BacnetObjectId objectId, ref int position, int count, byte[] fileBuffer, byte invokeId = 0, int? maxRetries = null, int? timeout = null)
    {
        maxRetries ??= Retries;
        timeout ??= Timeout;

        using var result = (BacnetAsyncResult)BeginWriteFileRequest(adr, objectId, position, count, fileBuffer, true, invokeId);
        for (var r = 0; r < maxRetries; r++)
        {
            if (result.WaitForDone(timeout.Value))
            {
                EndWriteFileRequest(result, out position, out var ex);
                if (ex != null)
                    throw ex;
                return;
            }
            if (r < maxRetries - 1)
                result.Resend();
        }

        throw OperationTimedOut();
    }

    public IAsyncResult BeginWriteFileRequest(BacnetAddress adr, BacnetObjectId objectId, int position, int count, byte[] fileBuffer, bool waitForTransmit, byte invokeId = 0)
    {
        Log.Debug("Sending AtomicWriteFileRequest");
        if (invokeId == 0)
            invokeId = mInvokeId.GetNext();

        var buffer = GetEncodeBuffer(Transport.HeaderLength);
        _messageFactory.CreateWriteFileRequest(buffer, adr, objectId, position, count, fileBuffer, waitForTransmit, invokeId);

        //send
        var ret = new BacnetAsyncResult(this, adr, invokeId, buffer.buffer, buffer.offset - Transport.HeaderLength, waitForTransmit, TransmitTimeout);
        ret.Resend();

        return ret;
    }

    public void EndWriteFileRequest(IAsyncResult result, out int position, out Exception ex)
    {
        var res = (BacnetAsyncResult)result;
        ex = res.Error;
        if (ex == null && !res.WaitForDone(Timeout))
            ex = new Exception("Wait Timeout");

        if (ex == null)
        {
            //decode
            if (Services.DecodeAtomicWriteFileAcknowledge(res.Result, 0, res.Result.Length, out _, out position) < 0)
                ex = new Exception("Decode");
        }
        else
        {
            position = -1;
        }

        res.Dispose();
    }

    public IAsyncResult BeginReadFileRequest(BacnetAddress adr, BacnetObjectId objectId, int position, uint count, bool waitForTransmit, byte invokeId = 0)
    {
        Log.Debug("Sending AtomicReadFileRequest");
        if (invokeId == 0)
            invokeId = mInvokeId.GetNext();

        //encode
        var buffer = GetEncodeBuffer(Transport.HeaderLength);
        _messageFactory.CreateReadFileRequest(buffer, adr, objectId, position, count, waitForTransmit, invokeId);

        //send
        var ret = new BacnetAsyncResult(this, adr, invokeId, buffer.buffer, buffer.offset - Transport.HeaderLength, waitForTransmit, TransmitTimeout);
        ret.Resend();

        return ret;
    }

    public void EndReadFileRequest(IAsyncResult result, out uint count, out int position, out bool endOfFile, out byte[] fileBuffer, out int fileBufferOffset, out Exception ex)
    {
        var res = (BacnetAsyncResult)result;
        ex = res.Error;
        if (ex == null && !res.WaitForDone(Timeout))
            ex = new Exception("Wait Timeout");

        if (ex == null)
        {
            //decode
            if (Services.DecodeAtomicReadFileAcknowledge(res.Result, 0, res.Result.Length, out endOfFile, out _, out position, out count, out fileBuffer, out fileBufferOffset) < 0)
                ex = new Exception("Decode");
        }
        else
        {
            count = 0;
            endOfFile = true;
            position = -1;
            fileBufferOffset = -1;
            fileBuffer = Array.Empty<byte>();
        }

        res.Dispose();
    }

    public void ReadFileRequest(BacnetAddress adr, BacnetObjectId objectId, ref int position, ref uint count, out bool endOfFile, out byte[] fileBuffer, out int fileBufferOffset, byte invokeId = 0, int? maxRetries = null, int? timeout = null)
    {
        maxRetries ??= Retries;
        timeout ??= Timeout;

        using var result = (BacnetAsyncResult)BeginReadFileRequest(adr, objectId, position, count, true, invokeId);
        for (var r = 0; r < maxRetries; r++)
        {
            if (result.WaitForDone(timeout.Value))
            {
                EndReadFileRequest(result, out count, out position, out endOfFile, out fileBuffer, out fileBufferOffset, out var ex);
                if (ex != null)
                    throw ex;
                return;
            }
            if (r < maxRetries - 1)
                result.Resend();
        }

        throw OperationTimedOut();
    }

    // Read range by postion
    public IAsyncResult BeginReadRangeRequest(BacnetAddress adr, BacnetObjectId objectId, uint idxBegin, uint quantity, bool waitForTransmit, byte invokeId = 0)
    {
        return BeginReadRangeRequestCore(adr, objectId, BacnetReadRangeRequestTypes.RR_BY_POSITION, DateTime.Now, idxBegin, quantity, waitForTransmit, invokeId);
    }

    // Read range by start time
    public IAsyncResult BeginReadRangeRequest(BacnetAddress adr, BacnetObjectId objectId, DateTime readFrom, uint quantity, bool waitForTransmit, byte invokeId = 0)
    {
        return BeginReadRangeRequestCore(adr, objectId, BacnetReadRangeRequestTypes.RR_BY_TIME, readFrom, 1, quantity, waitForTransmit, invokeId);
    }

    private IAsyncResult BeginReadRangeRequestCore(BacnetAddress adr, BacnetObjectId objectId, BacnetReadRangeRequestTypes bacnetReadRangeRequestTypes, DateTime readFrom, uint idxBegin, uint quantity, bool waitForTransmit, byte invokeId = 0)
    {
        Log.Debug("Sending ReadRangeRequest");
        if (invokeId == 0)
            invokeId = mInvokeId.GetNext();

        //encode
        var buffer = GetEncodeBuffer(Transport.HeaderLength);
        _messageFactory.CreateReadRangeRequestCore(buffer, adr, objectId, bacnetReadRangeRequestTypes, readFrom, idxBegin, quantity, waitForTransmit, invokeId);
        //send
        var ret = new BacnetAsyncResult(this, adr, invokeId, buffer.buffer, buffer.offset - Transport.HeaderLength, waitForTransmit, TransmitTimeout);
        ret.Resend();

        return ret;
    }

    public void EndReadRangeRequest(IAsyncResult result, out byte[] trendbuffer, out uint itemCount, out Exception ex)
    {
        var res = (BacnetAsyncResult)result;
        itemCount = 0;
        trendbuffer = null;

        ex = res.Error;
        if (ex == null && !res.WaitForDone(40 * 1000))
            ex = new Exception("Wait Timeout");

        if (ex == null)
        {
            itemCount = Services.DecodeReadRangeAcknowledge(res.Result, 0, res.Result.Length, out trendbuffer);
            if (itemCount == 0)
                ex = new Exception("Decode");
        }

        res.Dispose();
    }

    public void ReadRangeRequest(BacnetAddress adr, BacnetObjectId objectId, DateTime readFrom, ref uint quantity, out byte[] range, byte invokeId = 0)
    {
        ReadRangeRequestCore(BacnetReadRangeRequestTypes.RR_BY_TIME, adr, objectId, 1, readFrom, ref quantity, out range, invokeId);
    }
    public void ReadRangeRequest(BacnetAddress adr, BacnetObjectId objectId, uint idxBegin, ref uint quantity, out byte[] range, byte invokeId = 0)
    {
        ReadRangeRequestCore(BacnetReadRangeRequestTypes.RR_BY_POSITION, adr, objectId, idxBegin, DateTime.Now, ref quantity, out range, invokeId);
    }

    private void ReadRangeRequestCore(BacnetReadRangeRequestTypes requestType, BacnetAddress adr, BacnetObjectId objectId, uint idxBegin, DateTime readFrom, ref uint quantity, out byte[] range, byte invokeId = 0, int? maxRetries = null, int? timeout = null)
    {
        maxRetries ??= Retries;
        timeout ??= Timeout;

        Func<IAsyncResult> getResult;
        uint quantityCopy = quantity;
        switch (requestType)
        {
            case BacnetReadRangeRequestTypes.RR_BY_TIME:
                getResult = () => BeginReadRangeRequest(adr, objectId, readFrom, quantityCopy, true, invokeId);
                break;

            case BacnetReadRangeRequestTypes.RR_BY_POSITION:
                getResult = () => BeginReadRangeRequest(adr, objectId, idxBegin, quantityCopy, true, invokeId);
                break;

            default:
                throw new NotImplementedException($"BacnetReadRangeRequestTypes-Type {requestType} not supported in {nameof(ReadRangeRequestCore)}!");
        }

        range = null;
        using var result = (BacnetAsyncResult)getResult();
        for (var r = 0; r < maxRetries; r++)
        {
            if (result.WaitForDone(timeout.Value))
            {
                EndReadRangeRequest(result, out range, out quantity, out var ex); // quantity read could be less than demanded
                if (ex != null)
                    throw ex;
                return;
            }
            if (r < maxRetries - 1)
                result.Resend();
        }

        throw OperationTimedOut();
    }

    public void SubscribeCOVRequest(BacnetAddress adr, BacnetObjectId objectId, uint subscribeId, bool cancel, bool issueConfirmedNotifications, uint lifetime, byte invokeId = 0, int? maxRetries = null, int? timeout = null)
    {
        maxRetries ??= Retries;
        timeout ??= Timeout;

        using var result = (BacnetAsyncResult)BeginSubscribeCOVRequest(adr, objectId, subscribeId, cancel, issueConfirmedNotifications, lifetime, true, invokeId);
        for (var r = 0; r < maxRetries; r++)
        {
            if (result.WaitForDone(timeout.Value))
            {
                EndSubscribeCOVRequest(result, out var ex);
                if (ex != null)
                    throw ex;
                return;
            }
            if (r < maxRetries - 1)
                result.Resend();
        }

        throw OperationTimedOut();
    }

    public IAsyncResult BeginSubscribeCOVRequest(BacnetAddress adr, BacnetObjectId objectId, uint subscribeId, bool cancel, bool issueConfirmedNotifications, uint lifetime, bool waitForTransmit, byte invokeId = 0)
    {
        Log.Debug($"Sending SubscribeCOVRequest {objectId}");
        if (invokeId == 0)
            invokeId = mInvokeId.GetNext();

        var buffer = GetEncodeBuffer(Transport.HeaderLength);
        _messageFactory.CreateSubscribeCOVRequest(buffer, adr, objectId, subscribeId, cancel, issueConfirmedNotifications, lifetime, waitForTransmit, invokeId);

        //send
        var ret = new BacnetAsyncResult(this, adr, invokeId, buffer.buffer, buffer.offset - Transport.HeaderLength, waitForTransmit, TransmitTimeout);
        ret.Resend();

        return ret;
    }

    public void EndSubscribeCOVRequest(IAsyncResult result, out Exception ex)
    {
        var res = (BacnetAsyncResult)result;
        ex = res.Error;
        if (ex == null && !res.WaitForDone(Timeout))
            ex = new Exception("Wait Timeout");

        res.Dispose();
    }

    public void SendConfirmedEventNotificationRequest(BacnetAddress adr, BacnetEventNotificationData eventData, byte invokeId = 0, BacnetAddress source = null, int? maxRetries = null, int? timeout = null)
    {
        maxRetries ??= Retries;
        timeout ??= Timeout;

        using var result = (BacnetAsyncResult)BeginSendConfirmedEventNotificationRequest(adr, eventData, true, invokeId, source);
        for (var r = 0; r < maxRetries; r++)
        {
            if (result.WaitForDone(timeout.Value))
            {
                EndSendConfirmedEventNotificationRequest(result, out var ex);
                if (ex != null)
                    throw ex;
                return;
            }
            if (r < maxRetries - 1)
                result.Resend();
        }

        throw OperationTimedOut();
    }

    public IAsyncResult BeginSendConfirmedEventNotificationRequest(BacnetAddress adr, BacnetEventNotificationData eventData, bool waitForTransmit, byte invokeId = 0, BacnetAddress source = null)
    {
        Log.Debug($"Sending Confirmed Event Notification {eventData.eventType} {eventData.eventObjectIdentifier}");
        if (invokeId == 0)
            invokeId = mInvokeId.GetNext();

        var buffer = GetEncodeBuffer(Transport.HeaderLength);
        _messageFactory.CreateSendConfirmedEventNotificationRequest(buffer, adr, eventData, waitForTransmit, invokeId, source);

        //send
        var ret = new BacnetAsyncResult(this, adr, invokeId, buffer.buffer, buffer.offset - Transport.HeaderLength, waitForTransmit, TransmitTimeout);
        ret.Resend();

        return ret;
    }

    public void EndSendConfirmedEventNotificationRequest(IAsyncResult result, out Exception ex)
    {
        var res = (BacnetAsyncResult)result;
        ex = res.Error;
        if (ex == null && !res.WaitForDone(Timeout))
            ex = new Exception("Wait Timeout");

        res.Dispose();
    }

    public void SubscribePropertyRequest(BacnetAddress adr, BacnetObjectId objectId, BacnetPropertyReference monitoredProperty, uint subscribeId, bool cancel, bool issueConfirmedNotifications, uint lifetime, byte invokeId = 0, int? maxRetries = null, int? timeout = null)
    {
        maxRetries ??= Retries;
        timeout ??= Timeout;

        using var result = (BacnetAsyncResult)BeginSubscribePropertyRequest(adr, objectId, monitoredProperty, subscribeId, cancel, issueConfirmedNotifications, lifetime, true, invokeId);
        for (var r = 0; r < maxRetries; r++)
        {
            if (result.WaitForDone(timeout.Value))
            {
                EndSubscribePropertyRequest(result, out var ex);
                if (ex != null)
                    throw ex;
                return;
            }
            if (r < maxRetries - 1)
                result.Resend();
        }

        throw OperationTimedOut();
    }

    public IAsyncResult BeginSubscribePropertyRequest(BacnetAddress adr, BacnetObjectId objectId, BacnetPropertyReference monitoredProperty, uint subscribeId, bool cancel, bool issueConfirmedNotifications, uint lifetime, bool waitForTransmit, byte invokeId = 0)
    {
        Log.Debug($"Sending SubscribePropertyRequest {objectId}.{monitoredProperty}");
        if (invokeId == 0)
            invokeId = mInvokeId.GetNext();

        var buffer = GetEncodeBuffer(Transport.HeaderLength);
        _messageFactory.CreateSubscribePropertyRequest(buffer, adr, objectId, monitoredProperty, subscribeId, cancel, issueConfirmedNotifications, lifetime, waitForTransmit, invokeId);

        //send
        var ret = new BacnetAsyncResult(this, adr, invokeId, buffer.buffer, buffer.offset - Transport.HeaderLength, waitForTransmit, TransmitTimeout);
        ret.Resend();

        return ret;
    }

    public void EndSubscribePropertyRequest(IAsyncResult result, out Exception ex)
    {
        var res = (BacnetAsyncResult)result;
        ex = res.Error;
        if (ex == null && !res.WaitForDone(Timeout))
            ex = new Exception("Wait Timeout");

        res.Dispose();
    }

    public void ReadPropertyRequest(BacnetAddress adr, BacnetObjectId objectId, BacnetPropertyIds propertyId, out IList<BacnetValue> valueList, byte invokeId = 0, uint arrayIndex = ASN1.BACNET_ARRAY_ALL, int? maxRetries = null, int? timeout = null)
    {
        maxRetries ??= Retries;
        timeout ??= Timeout;

        using var result = (BacnetAsyncResult)BeginReadPropertyRequest(adr, objectId, propertyId, true, invokeId, arrayIndex);
        for (var r = 0; r < maxRetries; r++)
        {
            if (result.WaitForDone(timeout.Value))
            {
                EndReadPropertyRequest(result, out valueList, out var ex);
                if (ex != null)
                    throw ex;
                return;
            }
            if (r < maxRetries - 1)
                result.Resend();
        }

        throw OperationTimedOut();
    }

    public Task<IList<BacnetValue>> ReadPropertyAsync(BacnetAddress address, BacnetObjectTypes objType, uint objInstance,
        BacnetPropertyIds propertyId, byte invokeId = 0, uint arrayIndex = ASN1.BACNET_ARRAY_ALL)
    {
        var objectId = new BacnetObjectId(objType, objInstance);
        return ReadPropertyAsync(address, objectId, propertyId, invokeId, arrayIndex);
    }

    public Task<IList<BacnetValue>> ReadPropertyAsync(BacnetAddress address, BacnetObjectId objectId,
        BacnetPropertyIds propertyId, byte invokeId = 0, uint arrayIndex = ASN1.BACNET_ARRAY_ALL)
    {
        return Task<IList<BacnetValue>>.Factory.StartNew(() =>
        {
            try
            {
                ReadPropertyRequest(address, objectId, propertyId, out IList<BacnetValue> result, invokeId, arrayIndex);
                return result;
            }
            catch (Exception e)
            {
                throw new Exception($"Failed to read property {propertyId} of {objectId} from {address}", e);
            }
        });
    }

    public IAsyncResult BeginReadPropertyRequest(BacnetAddress address, BacnetObjectId objectId, BacnetPropertyIds propertyId, bool waitForTransmit, byte invokeId = 0, uint arrayIndex = ASN1.BACNET_ARRAY_ALL)
    {
        Log.Debug($"Sending ReadPropertyRequest {objectId} {propertyId}");
        if (invokeId == 0)
            invokeId = mInvokeId.GetNext();

        var buffer = GetEncodeBuffer(Transport.HeaderLength);
        _messageFactory.CreateReadPropertyRequest(buffer, address, objectId, propertyId, waitForTransmit, invokeId, arrayIndex);

        //send
        var ret = new BacnetAsyncResult(this, address, invokeId, buffer.buffer, buffer.offset - Transport.HeaderLength, waitForTransmit, TransmitTimeout);
        ret.Resend();

        return ret;
    }

    public void EndReadPropertyRequest(IAsyncResult result, out IList<BacnetValue> valueList, out Exception ex)
    {
        var res = (BacnetAsyncResult)result;
        ex = res.Error;
        if (ex == null && !res.WaitForDone(Timeout))
            ex = new Exception("Wait Timeout");

        if (ex == null)
        {
            //decode
            if (Services.DecodeReadPropertyAcknowledge(res.Address, res.Result, 0, res.Result.Length, out _, out _, out valueList) < 0)
                ex = new Exception("Decode");
        }
        else
        {
            valueList = null;
        }

        res.Dispose();
    }

    public void WritePropertyRequest(BacnetAddress adr, BacnetObjectId objectId, BacnetPropertyIds propertyId, IEnumerable<BacnetValue> valueList, uint arrayIndex = ASN1.BACNET_ARRAY_ALL, byte invokeId = 0, int? maxRetries = null, int? timeout = null, BacnetWritePriority writePriority = BacnetWritePriority.NO_PRIORITY)
    {
        maxRetries ??= Retries;
        timeout ??= Timeout;

        using var result = (BacnetAsyncResult)BeginWritePropertyRequest(adr, objectId, propertyId, valueList, true, arrayIndex, invokeId, writePriority);
        for (var r = 0; r < maxRetries; r++)
        {
            if (result.WaitForDone(timeout.Value))
            {
                EndWritePropertyRequest(result, out var ex);
                if (ex != null)
                    throw ex;
                return;
            }
            if (r < maxRetries - 1)
                result.Resend();
        }

        throw OperationTimedOut();
    }

    public void WritePropertyMultipleRequest(BacnetAddress adr, BacnetObjectId objectId, ICollection<BacnetPropertyValue> valueList, byte invokeId = 0, int? maxRetries = null, int? timeout = null)
    {
        maxRetries ??= Retries;
        timeout ??= Timeout;

        using var result = (BacnetAsyncResult)BeginWritePropertyMultipleRequest(adr, objectId, valueList, true, invokeId);
        for (var r = 0; r < maxRetries; r++)
        {
            if (result.WaitForDone(timeout.Value))
            {
                EndWritePropertyRequest(result, out var ex); // Share the same with single write
                if (ex != null)
                    throw ex;
                return;
            }
            if (r < maxRetries - 1)
                result.Resend();
        }

        throw OperationTimedOut();
    }

    public IAsyncResult BeginWritePropertyRequest(BacnetAddress adr, BacnetObjectId objectId, BacnetPropertyIds propertyId, IEnumerable<BacnetValue> valueList, bool waitForTransmit,
        uint arrayIndex = ASN1.BACNET_ARRAY_ALL, byte invokeId = 0, BacnetWritePriority writePriority = BacnetWritePriority.NO_PRIORITY)
    {
        Log.Debug($"Sending WritePropertyRequest {objectId} {propertyId}");
        if (invokeId == 0)
            invokeId = mInvokeId.GetNext();

        var buffer = GetEncodeBuffer(Transport.HeaderLength);
        _messageFactory.CreateWritePropertyRequest(buffer, adr, objectId, propertyId, valueList, waitForTransmit, arrayIndex, invokeId, writePriority);

        //send
        var ret = new BacnetAsyncResult(this, adr, invokeId, buffer.buffer, buffer.offset - Transport.HeaderLength, waitForTransmit, TransmitTimeout);
        ret.Resend();

        return ret;
    }

    public IAsyncResult BeginWritePropertyMultipleRequest(BacnetAddress adr, BacnetObjectId objectId, ICollection<BacnetPropertyValue> valueList, bool waitForTransmit, byte invokeId = 0)
    {
        Log.Debug($"Sending WritePropertyMultipleRequest {objectId}");
        if (invokeId == 0) invokeId = mInvokeId.GetNext();

        var buffer = GetEncodeBuffer(Transport.HeaderLength);
        _messageFactory.CreateWritePropertyMultipleRequest(buffer, adr, objectId, valueList, waitForTransmit, invokeId);

        //send
        var ret = new BacnetAsyncResult(this, adr, invokeId, buffer.buffer, buffer.offset - Transport.HeaderLength, waitForTransmit, TransmitTimeout);
        ret.Resend();

        return ret;
    }

    public void EndWritePropertyRequest(IAsyncResult result, out Exception ex)
    {
        var res = (BacnetAsyncResult)result;
        ex = res.Error;
        if (ex == null && !res.WaitForDone(Timeout))
            ex = new Exception("Wait Timeout");

        res.Dispose();
    }

    // By Chritopher Günter : Write multiple properties on multiple objects
    public void WritePropertyMultipleRequest(BacnetAddress adr, ICollection<BacnetReadAccessResult> valueList, byte invokeId = 0, int? maxRetries = null, int? timeout = null)
    {
        maxRetries ??= Retries;
        timeout ??= Timeout;

        using var result = (BacnetAsyncResult)BeginWritePropertyMultipleRequest(adr, valueList, true, invokeId);
        for (var r = 0; r < maxRetries; r++)
        {
            if (result.WaitForDone(timeout.Value))
            {
                EndWritePropertyRequest(result, out var ex); // Share the same with single write
                if (ex != null)
                    throw ex;
                return;
            }
            if (r < maxRetries - 1)
                result.Resend();
        }

        throw OperationTimedOut();
    }

    public IAsyncResult BeginWritePropertyMultipleRequest(BacnetAddress adr, ICollection<BacnetReadAccessResult> valueList, bool waitForTransmit, byte invokeId = 0)
    {
        var objectIds = string.Join(", ", valueList.Select(v => v.objectIdentifier));
        Log.Debug($"Sending WritePropertyMultipleRequest {objectIds}");

        if (invokeId == 0)
            invokeId = mInvokeId.GetNext();

        var buffer = GetEncodeBuffer(Transport.HeaderLength);
        _messageFactory.CreateWritePropertyMultipleRequest(buffer, adr, valueList, waitForTransmit, invokeId);

        //send
        var ret = new BacnetAsyncResult(this, adr, invokeId, buffer.buffer, buffer.offset - Transport.HeaderLength, waitForTransmit, TransmitTimeout);
        ret.Resend();

        return ret;
    }

    public void ReadPropertyMultipleRequest(BacnetAddress address, BacnetObjectId objectId, IList<BacnetPropertyReference> propertyIdAndArrayIndex, out IList<BacnetReadAccessResult> values, byte invokeId = 0, int? maxRetries = null, int? timeout = null)
    {
        maxRetries ??= Retries;
        timeout ??= Timeout;

        using var result = (BacnetAsyncResult)BeginReadPropertyMultipleRequest(address, objectId, propertyIdAndArrayIndex, true, invokeId);
        for (var r = 0; r < maxRetries; r++)
        {
            if (result.WaitForDone(timeout.Value))
            {
                EndReadPropertyMultipleRequest(result, out values, out var ex);
                if (ex != null)
                    throw ex;
                return;
            }
            if (r < maxRetries - 1)
                result.Resend();
        }

        throw OperationTimedOut();
    }

    public Task<IList<BacnetPropertyValue>> ReadPropertyMultipleAsync(BacnetAddress address,
        BacnetObjectTypes objType, uint objInstance, params BacnetPropertyIds[] propertyIds)
    {
        var objectId = new BacnetObjectId(objType, objInstance);
        return ReadPropertyMultipleAsync(address, objectId, propertyIds);
    }

    public Task<IList<BacnetPropertyValue>> ReadPropertyMultipleAsync(BacnetAddress address,
        BacnetObjectId objectId, params BacnetPropertyIds[] propertyIds)
    {
        var propertyReferences = propertyIds.Select(p =>
            new BacnetPropertyReference((uint)p, ASN1.BACNET_ARRAY_ALL));

        return Task<IList<BacnetPropertyValue>>.Factory.StartNew(() =>
        {
            try
            {
                ReadPropertyMultipleRequest(address, objectId, propertyReferences.ToList(), out var result);
                return result.Single().values;
            }
            catch (Exception e)
            {
                throw new Exception($"Failed to read multiple properties of {objectId} from {address}", e);
            }
        });
    }

    public IAsyncResult BeginReadPropertyMultipleRequest(BacnetAddress adr, BacnetObjectId objectId, IList<BacnetPropertyReference> propertyIdAndArrayIndex, bool waitForTransmit, byte invokeId = 0)
    {
        var propertyIds = string.Join(", ", propertyIdAndArrayIndex.Select(v => (BacnetPropertyIds)v.propertyIdentifier));
        Log.Debug($"Sending ReadPropertyMultipleRequest {objectId} {propertyIds}");
        if (invokeId == 0)
            invokeId = mInvokeId.GetNext();

        var buffer = GetEncodeBuffer(Transport.HeaderLength);
        _messageFactory.CreateReadPropertyMultipleRequest(buffer, adr, objectId, propertyIdAndArrayIndex, waitForTransmit, invokeId);

        //send
        var ret = new BacnetAsyncResult(this, adr, invokeId, buffer.buffer, buffer.offset - Transport.HeaderLength, waitForTransmit, TransmitTimeout);
        ret.Resend();

        return ret;
    }

    // Another way to read multiple properties on multiples objects, if supported by devices
    public void ReadPropertyMultipleRequest(BacnetAddress address, IList<BacnetReadAccessSpecification> properties, out IList<BacnetReadAccessResult> values, byte invokeId = 0, int? maxRetries = null, int? timeout = null)
    {
        maxRetries ??= Retries;
        timeout ??= Timeout;

        using var result = (BacnetAsyncResult)BeginReadPropertyMultipleRequest(address, properties, true, invokeId);
        for (var r = 0; r < maxRetries; r++)
        {
            if (result.WaitForDone(timeout.Value))
            {
                EndReadPropertyMultipleRequest(result, out values, out var ex);
                if (ex != null)
                    throw ex;
                return;
            }
            if (r < maxRetries - 1)
                result.Resend();
        }

        throw OperationTimedOut();
    }

    public IAsyncResult BeginReadPropertyMultipleRequest(BacnetAddress adr, IList<BacnetReadAccessSpecification> properties, bool waitForTransmit, byte invokeId = 0)
    {
        var objectIds = string.Join(", ", properties.Select(v => v.objectIdentifier));
        Log.Debug($"Sending ReadPropertyMultipleRequest {objectIds}");
        if (invokeId == 0)
            invokeId = mInvokeId.GetNext();

        var buffer = GetEncodeBuffer(Transport.HeaderLength);
        _messageFactory.CreateReadPropertyMultipleRequest(buffer, adr, properties, waitForTransmit, invokeId);

        //send
        var ret = new BacnetAsyncResult(this, adr, invokeId, buffer.buffer, buffer.offset - Transport.HeaderLength, waitForTransmit, TransmitTimeout);
        ret.Resend();

        return ret;
    }

    public void EndReadPropertyMultipleRequest(IAsyncResult result, out IList<BacnetReadAccessResult> values, out Exception ex)
    {
        var res = (BacnetAsyncResult)result;
        ex = res.Error;
        if (ex == null && !res.WaitForDone(Timeout))
            ex = new Exception("Wait Timeout");

        if (ex == null)
        {
            //decode
            if (Services.DecodeReadPropertyMultipleAcknowledge(res.Address, res.Result, 0, res.Result.Length, out values) < 0)
                ex = new Exception("Decode");
        }
        else
        {
            values = null;
        }

        res.Dispose();
    }

    public BacnetPduTypes PduConfirmedServiceRequest()
    {
        return MaxSegments != BacnetMaxSegments.MAX_SEG0
            ? BacnetPduTypes.PDU_TYPE_CONFIRMED_SERVICE_REQUEST | BacnetPduTypes.SEGMENTED_RESPONSE_ACCEPTED
            : BacnetPduTypes.PDU_TYPE_CONFIRMED_SERVICE_REQUEST;
    }

    public void CreateObjectRequest(BacnetAddress adr, BacnetObjectId objectId, ICollection<BacnetPropertyValue> valueList = null, byte invokeId = 0, int? maxRetries = null, int? timeout = null)
    {
        maxRetries ??= Retries;
        timeout ??= Timeout;

        using var result = (BacnetAsyncResult)BeginCreateObjectRequest(adr, objectId, valueList, true, invokeId);
        for (var r = 0; r < maxRetries; r++)
        {
            if (result.WaitForDone(timeout.Value))
            {
                EndCreateObjectRequest(result, out var ex);
                if (ex != null)
                    throw ex;
                return;
            }
            if (r < maxRetries - 1)
                result.Resend();
        }

        throw OperationTimedOut();
    }

    public IAsyncResult BeginCreateObjectRequest(BacnetAddress adr, BacnetObjectId objectId, ICollection<BacnetPropertyValue> valueList, bool waitForTransmit, byte invokeId = 0)
    {
        Log.Debug("Sending CreateObjectRequest");
        if (invokeId == 0) invokeId = mInvokeId.GetNext();

        var buffer = GetEncodeBuffer(Transport.HeaderLength);

        _messageFactory.CreateCreateObjectRequest(buffer, adr, objectId, valueList, waitForTransmit, invokeId);

        //send
        var ret = new BacnetAsyncResult(this, adr, invokeId, buffer.buffer, buffer.offset - Transport.HeaderLength, waitForTransmit, TransmitTimeout);
        ret.Resend();

        return ret;
    }

    public void EndCreateObjectRequest(IAsyncResult result, out Exception ex)
    {
        var res = (BacnetAsyncResult)result;
        ex = res.Error;
        if (ex == null && !res.WaitForDone(Timeout))
            ex = new Exception("Wait Timeout");

        res.Dispose();
    }

    public void DeleteObjectRequest(BacnetAddress adr, BacnetObjectId objectId, byte invokeId = 0, int? maxRetries = null, int? timeout = null)
    {
        maxRetries ??= Retries;
        timeout ??= Timeout;

        using var result = (BacnetAsyncResult)BeginDeleteObjectRequest(adr, objectId, true, invokeId);
        for (var r = 0; r < maxRetries; r++)
        {
            if (result.WaitForDone(timeout.Value))
            {
                EndDeleteObjectRequest(result, out var ex);
                if (ex != null)
                    throw ex;
                return;
            }
            if (r < maxRetries - 1)
                result.Resend();
        }

        throw OperationTimedOut();
    }

    public IAsyncResult BeginDeleteObjectRequest(BacnetAddress adr, BacnetObjectId objectId, bool waitForTransmit, byte invokeId = 0)
    {
        Log.Debug("Sending DeleteObjectRequest");
        if (invokeId == 0) invokeId = mInvokeId.GetNext();

        var buffer = GetEncodeBuffer(Transport.HeaderLength);

        _messageFactory.CreateDeleteObjectRequest(buffer, adr, objectId, waitForTransmit, invokeId);

        //send
        var ret = new BacnetAsyncResult(this, adr, invokeId, buffer.buffer, buffer.offset - Transport.HeaderLength, waitForTransmit, TransmitTimeout);
        ret.Resend();

        return ret;
    }

    public void EndDeleteObjectRequest(IAsyncResult result, out Exception ex)
    {
        var res = (BacnetAsyncResult)result;
        ex = res.Error;
        if (ex == null && !res.WaitForDone(Timeout))
            ex = new Exception("Wait Timeout");

        res.Dispose();
    }

    public void AddListElementRequest(BacnetAddress adr, BacnetObjectId objectId, BacnetPropertyReference reference, IList<BacnetValue> valueList, byte invokeId = 0, int? maxRetries = null, int? timeout = null)
    {
        maxRetries ??= Retries;
        timeout ??= Timeout;

        using var result = (BacnetAsyncResult)BeginAddListElementRequest(adr, objectId, reference, valueList, true, invokeId);
        for (var r = 0; r < maxRetries; r++)
        {

            if (result.WaitForDone(timeout.Value))
            {
                EndAddListElementRequest(result, out var ex);
                if (ex != null)
                    throw ex;
                return;
            }
            if (r < maxRetries - 1)
                result.Resend();
        }

        throw OperationTimedOut();
    }

    public void RemoveListElementRequest(BacnetAddress adr, BacnetObjectId objectId, BacnetPropertyReference reference, IList<BacnetValue> valueList, byte invokeId = 0, int? maxRetries = null, int? timeout = null)
    {
        maxRetries ??= Retries;
        timeout ??= Timeout;

        using var result = (BacnetAsyncResult)BeginRemoveListElementRequest(adr, objectId, reference, valueList, true, invokeId);
        for (var r = 0; r < maxRetries; r++)
        {
            if (result.WaitForDone(timeout.Value))
            {
                EndAddListElementRequest(result, out var ex);
                if (ex != null)
                    throw ex;
                return;
            }
            if (r < maxRetries - 1)
                result.Resend();
        }

        throw OperationTimedOut();
    }

    public IAsyncResult BeginRemoveListElementRequest(BacnetAddress adr, BacnetObjectId objectId, BacnetPropertyReference reference, IList<BacnetValue> valueList, bool waitForTransmit, byte invokeId = 0)
    {
        Log.Debug("Sending RemoveListElementRequest");
        if (invokeId == 0)
            invokeId = mInvokeId.GetNext();

        var buffer = GetEncodeBuffer(Transport.HeaderLength);
        _messageFactory.CreateRemoveListElementRequest(buffer, adr, objectId, reference, valueList, waitForTransmit, invokeId);

        //send
        var ret = new BacnetAsyncResult(this, adr, invokeId, buffer.buffer, buffer.offset - Transport.HeaderLength, waitForTransmit, TransmitTimeout);
        ret.Resend();

        return ret;
    }

    public IAsyncResult BeginAddListElementRequest(BacnetAddress adr, BacnetObjectId objectId, BacnetPropertyReference reference, IList<BacnetValue> valueList, bool waitForTransmit, byte invokeId = 0)
    {
        Log.Debug($"Sending AddListElementRequest {objectId} {(BacnetPropertyIds)reference.propertyIdentifier}");
        if (invokeId == 0)
            invokeId = mInvokeId.GetNext();

        var buffer = GetEncodeBuffer(Transport.HeaderLength);
        _messageFactory.CreateAddListElementRequest(buffer, adr, objectId, reference, valueList, waitForTransmit, invokeId);

        //send
        var ret = new BacnetAsyncResult(this, adr, invokeId, buffer.buffer, buffer.offset - Transport.HeaderLength, waitForTransmit, TransmitTimeout);
        ret.Resend();

        return ret;
    }

    public void EndAddListElementRequest(IAsyncResult result, out Exception ex)
    {
        var res = (BacnetAsyncResult)result;
        ex = res.Error;
        if (ex == null && !res.WaitForDone(Timeout))
            ex = new Exception("Wait Timeout");

        res.Dispose();
    }

    // Read or Write without APDU Data encoding nor Decoding (just Request type, Object id and Property id)
    // Data is given by the caller starting with the Tag 3 (or maybe another one), and ending with it
    // return buffer start also with the Tag 3
    public void RawEncodedDecodedPropertyConfirmedRequest(BacnetAddress adr, BacnetObjectId objectId, BacnetPropertyIds propertyId, BacnetConfirmedServices serviceId, ref byte[] inOutBuffer, byte invokeId = 0, int? maxRetries = null, int? timeout = null)
    {
        maxRetries ??= Retries;
        timeout ??= Timeout;

        using var result = (BacnetAsyncResult)BeginRawEncodedDecodedPropertyConfirmedRequest(adr, objectId, propertyId, serviceId, inOutBuffer, true, invokeId);
        for (var r = 0; r < maxRetries; r++)
        {
            if (result.WaitForDone(timeout.Value))
            {
                EndRawEncodedDecodedPropertyConfirmedRequest(result, serviceId, out inOutBuffer, out var ex);
                if (ex != null)
                    throw ex;
                return;
            }
            if (r < maxRetries - 1)
                result.Resend();
        }

        throw OperationTimedOut();
    }

    public IAsyncResult BeginRawEncodedDecodedPropertyConfirmedRequest(BacnetAddress adr, BacnetObjectId objectId, BacnetPropertyIds propertyId, BacnetConfirmedServices serviceId, byte[] inOutBuffer, bool waitForTransmit, byte invokeId = 0)
    {
        Log.Debug("Sending RawEncodedRequest");
        if (invokeId == 0)
            invokeId = mInvokeId.GetNext();

        var buffer = GetEncodeBuffer(Transport.HeaderLength);
        _messageFactory.CreateRawEncodedDecodedPropertyConfirmedRequest(buffer, adr, objectId, propertyId, serviceId, inOutBuffer, waitForTransmit, invokeId);

        // No content encoding to do
        if (inOutBuffer != null)
            buffer.Add(inOutBuffer, inOutBuffer.Length);

        //send
        var ret = new BacnetAsyncResult(this, adr, invokeId, buffer.buffer, buffer.offset - Transport.HeaderLength, waitForTransmit, TransmitTimeout);
        ret.Resend();

        return ret;
    }

    public void EndRawEncodedDecodedPropertyConfirmedRequest(IAsyncResult result, BacnetConfirmedServices serviceId, out byte[] inOutBuffer, out Exception ex)
    {
        var res = (BacnetAsyncResult)result;
        ex = res.Error;
        if (ex == null && !res.WaitForDone(Timeout))
            ex = new Exception("Wait Timeout");

        inOutBuffer = null;

        if (ex == null)
        {
            if (serviceId == BacnetConfirmedServices.SERVICE_CONFIRMED_READ_PROPERTY)
            {
                //decode
                const int offset = 0;
                var buffer = res.Result;

                ex = new Exception("Decode");

                if (!ASN1.decode_is_context_tag(buffer, offset, 0))
                    return;
                var len = 1;
                len += ASN1.decode_object_id(buffer, offset + len, out BacnetObjectTypes _, out _);
                /* Tag 1: Property ID */
                len += ASN1.decode_tag_number_and_value(buffer, offset + len, out var tagNumber, out var lenValueType);
                if (tagNumber != 1)
                    return;
                len += ASN1.decode_enumerated(buffer, offset + len, lenValueType, out _);

                inOutBuffer = new byte[buffer.Length - len];
                Array.Copy(buffer, len, inOutBuffer, 0, inOutBuffer.Length);

                ex = null;
            }
        }

        res.Dispose();
    }

    public void DeviceCommunicationControlRequest(BacnetAddress adr, uint timeDuration, uint enableDisable, string password, byte invokeId = 0, int? maxRetries = null, int? timeout = null)
    {
        maxRetries ??= Retries;
        timeout ??= Timeout;

        using var result = (BacnetAsyncResult)BeginDeviceCommunicationControlRequest(adr, timeDuration, enableDisable, password, true, invokeId);
        for (var r = 0; r < maxRetries; r++)
        {
            if (result.WaitForDone(timeout.Value))
            {
                EndDeviceCommunicationControlRequest(result, out var ex);
                if (ex != null)
                    throw ex;

                return;
            }
            if (r < maxRetries - 1)
                result.Resend();
        }

        throw OperationTimedOut();
    }

    public IAsyncResult BeginDeviceCommunicationControlRequest(BacnetAddress adr, uint timeDuration, uint enableDisable, string password, bool waitForTransmit, byte invokeId = 0)
    {
        Log.Debug("Sending DeviceCommunicationControlRequest");
        if (invokeId == 0)
            invokeId = mInvokeId.GetNext();

        var buffer = GetEncodeBuffer(Transport.HeaderLength);
        _messageFactory.CreateDeviceCommunicationControlRequest(buffer, adr, timeDuration, enableDisable, password, waitForTransmit, invokeId);

        //send
        var ret = new BacnetAsyncResult(this, adr, invokeId, buffer.buffer, buffer.offset - Transport.HeaderLength, waitForTransmit, TransmitTimeout);
        ret.Resend();

        return ret;
    }

    public void EndDeviceCommunicationControlRequest(IAsyncResult result, out Exception ex)
    {
        var res = (BacnetAsyncResult)result;
        ex = res.Error;
        if (ex == null && !res.WaitForDone(Timeout))
            ex = new Exception("Wait Timeout");

        res.Dispose();
    }

    public void GetAlarmSummaryOrEventRequestAll(BacnetAddress adr, bool getEvent, IList<BacnetGetEventInformationData> alarms, byte invokeId = 0, int? maxRetries = null, int? timeout = null)
    {
        bool moreEvents;
        do
        {
            GetAlarmSummaryOrEventRequest(adr, getEvent, alarms, out moreEvents, invokeId, maxRetries, timeout);
        } while (moreEvents);
    }

    public void GetAlarmSummaryOrEventRequest(BacnetAddress adr, bool getEvent, IList<BacnetGetEventInformationData> alarms, out bool hasMoreEvents, byte invokeId = 0, int? maxRetries = null, int? timeout = null)
    {
        maxRetries ??= Retries;
        timeout ??= Timeout;

        using var result = (BacnetAsyncResult)BeginGetAlarmSummaryOrEventRequest(adr, getEvent, alarms, true, invokeId);
        for (var r = 0; r < maxRetries; r++)
        {
            if (result.WaitForDone(timeout.Value))
            {
                EndGetAlarmSummaryOrEventRequest(result, getEvent, alarms, out hasMoreEvents, out var ex);
                if (ex != null)
                    throw ex;

                return;
            }

            if (r < maxRetries - 1)
                result.Resend();
        }

        throw OperationTimedOut();
    }

    public Task<IList<BacnetGetEventInformationData>> GetEventsAsync(BacnetAddress address, byte invokeId = 0)
    {
        IList<BacnetGetEventInformationData> result = new List<BacnetGetEventInformationData>();

        return Task<IList<BacnetGetEventInformationData>>.Factory.StartNew(() =>
        {
            try
            {
                GetAlarmSummaryOrEventRequestAll(address, true, result, invokeId);
            }
            catch (Exception e)
            {
                throw new Exception($"Failed to get events from {address}", e);
            }

            return result;
        });
    }

    public IAsyncResult BeginGetAlarmSummaryOrEventRequest(BacnetAddress adr, bool getEvent, IList<BacnetGetEventInformationData> alarms, bool waitForTransmit, byte invokeId = 0)
    {
        Log.Debug("Sending Alarm summary request");
        if (invokeId == 0)
            invokeId = mInvokeId.GetNext();

        var buffer = GetEncodeBuffer(Transport.HeaderLength);
        _messageFactory.CreateGetAlarmSummaryOrEventRequest(buffer, adr, getEvent, alarms, waitForTransmit, invokeId);

        //send
        var ret = new BacnetAsyncResult(this, adr, invokeId, buffer.buffer, buffer.offset - Transport.HeaderLength, waitForTransmit, TransmitTimeout);
        ret.Resend();

        return ret;
    }

    public void EndGetAlarmSummaryOrEventRequest(IAsyncResult result, bool getEvent, IList<BacnetGetEventInformationData> alarms, out bool moreEvent, out Exception ex)
    {
        moreEvent = false;
        var res = (BacnetAsyncResult)result;
        ex = res.Error;
        if (ex == null && !res.WaitForDone(Timeout))
            ex = new Exception("Wait Timeout");

        if (ex == null)
        {
            if (Services.DecodeAlarmSummaryOrEvent(res.Result, 0, res.Result.Length, getEvent, ref alarms, out moreEvent) < 0)
                ex = new Exception("Decode");
        }
        else
        {
            ex = new Exception("Service not available", ex);
        }

        res.Dispose();
    }
    
    public void GetAlarmSummaryOrEventInformationResponse(BacnetAddress adr, bool getEvent, byte invokeId, Segmentation segmentation, BacnetGetEventInformationData[] data, bool moreEvents)
    {
        // 'getEvent' is not currently used.   Can be used if ever implementing GetAlarmSummary.
        // response could be segmented
        // but if you don't want it segmented (which would be normal usage)
        // you have to compute the message data and the 'more' flag
        // outside this function.
        HandleSegmentationResponse(adr, invokeId, segmentation, (_) =>
        {
            SendComplexAck(adr, invokeId, segmentation, BacnetConfirmedServices.SERVICE_CONFIRMED_GET_EVENT_INFORMATION, (b) =>
            {
                Services.EncodeGetEventInformationAcknowledge(b, data, moreEvents);
            });
        });
    }

    public void AlarmAcknowledgement(BacnetAddress adr, BacnetObjectId objId, BacnetEventStates eventState, string ackText, BacnetGenericTime evTimeStamp, BacnetGenericTime ackTimeStamp, byte invokeId = 0, uint ackProcessIdentifier = 57, int? maxRetries = null, int? timeout = null)
    {
        maxRetries ??= Retries;
        timeout ??= Timeout;

        using var result = (BacnetAsyncResult)BeginAlarmAcknowledgement(adr, objId, eventState, ackText, evTimeStamp, ackTimeStamp, true, invokeId, ackProcessIdentifier);
        for (var r = 0; r < maxRetries; r++)
        {
            if (result.WaitForDone(timeout.Value))
            {
                EndAlarmAcknowledgement(result, out var ex);
                if (ex != null)
                    throw ex;

                return;
            }
            if (r < maxRetries - 1)
                result.Resend();
        }

        throw OperationTimedOut();
    }

    public IAsyncResult BeginAlarmAcknowledgement(BacnetAddress adr, BacnetObjectId objId, BacnetEventStates eventState, string ackText, BacnetGenericTime evTimeStamp, BacnetGenericTime ackTimeStamp, bool waitForTransmit, byte invokeId = 0, uint ackProcessIdentifier = 57)
    {
        Log.Debug("Sending AlarmAcknowledgement");
        if (invokeId == 0)
            invokeId = mInvokeId.GetNext();

        var buffer = GetEncodeBuffer(Transport.HeaderLength);
        _messageFactory.CreateAlarmAcknowledgement(buffer, adr, objId, eventState, ackText, evTimeStamp, ackTimeStamp, waitForTransmit, invokeId, ackProcessIdentifier);

        //send
        var ret = new BacnetAsyncResult(this, adr, invokeId, buffer.buffer, buffer.offset - Transport.HeaderLength, waitForTransmit, TransmitTimeout);
        ret.Resend();

        return ret;
    }

    public void EndAlarmAcknowledgement(IAsyncResult result, out Exception ex)
    {
        var res = (BacnetAsyncResult)result;
        ex = res.Error;
        if (!res.WaitForDone(Timeout))
            ex = new Exception("Wait Timeout");
    }

    public void ReinitializeRequest(BacnetAddress adr, BacnetReinitializedStates state, string password, byte invokeId = 0, int? maxRetries = null, int? timeout = null)
    {
        maxRetries ??= Retries;
        timeout ??= Timeout;

        using var result = (BacnetAsyncResult)BeginReinitializeRequest(adr, state, password, true, invokeId);
        for (var r = 0; r < maxRetries; r++)
        {
            if (result.WaitForDone(timeout.Value))
            {
                EndReinitializeRequest(result, out var ex);
                if (ex != null)
                    throw ex;

                return;
            }
            if (r < maxRetries - 1)
                result.Resend();
        }

        throw OperationTimedOut();
    }

    public IAsyncResult BeginReinitializeRequest(BacnetAddress adr, BacnetReinitializedStates state, string password, bool waitForTransmit, byte invokeId = 0)
    {
        Log.Debug("Sending ReinitializeRequest");
        if (invokeId == 0)
            invokeId = mInvokeId.GetNext();

        var buffer = GetEncodeBuffer(Transport.HeaderLength);
        _messageFactory.CreateReinitializeRequest(buffer, adr, state, password, waitForTransmit, invokeId);

        //send
        var ret = new BacnetAsyncResult(this, adr, invokeId, buffer.buffer, buffer.offset - Transport.HeaderLength, waitForTransmit, TransmitTimeout);
        ret.Resend();

        return ret;
    }

    public void EndReinitializeRequest(IAsyncResult result, out Exception ex)
    {
        var res = (BacnetAsyncResult)result;
        ex = res.Error;
        if (ex == null && !res.WaitForDone(Timeout))
            ex = new Exception("Wait Timeout");

        res.Dispose();
    }

    public IAsyncResult BeginConfirmedNotify(BacnetAddress adr, uint subscriberProcessIdentifier, uint initiatingDeviceIdentifier, BacnetObjectId monitoredObjectIdentifier, uint timeRemaining, IList<BacnetPropertyValue> values, bool waitForTransmit, byte invokeId = 0)
    {
        Log.Debug("Sending Notify (confirmed)");
        if (invokeId == 0) invokeId = mInvokeId.GetNext();

        var buffer = GetEncodeBuffer(Transport.HeaderLength);
        _messageFactory.CreateConfirmedNotify(buffer, adr, subscriberProcessIdentifier, initiatingDeviceIdentifier, monitoredObjectIdentifier, timeRemaining, values, waitForTransmit, invokeId);

        //send
        var ret = new BacnetAsyncResult(this, adr, invokeId, buffer.buffer, buffer.offset - Transport.HeaderLength, waitForTransmit, TransmitTimeout);
        ret.Resend();

        return ret;
    }

    public void EndConfirmedNotify(IAsyncResult result, out Exception ex)
    {
        var res = (BacnetAsyncResult)result;
        ex = res.Error;
        if (!res.WaitForDone(Timeout))
            ex = new Exception("Wait Timeout");
    }

    public bool Notify(BacnetAddress adr, uint subscriberProcessIdentifier, uint initiatingDeviceIdentifier, BacnetObjectId monitoredObjectIdentifier, uint timeRemaining, bool issueConfirmedNotifications, IList<BacnetPropertyValue> values, int? maxRetries = null, int? timeout = null)
    {
        if (!issueConfirmedNotifications)
        {
            Log.Debug("Sending Notify (unconfirmed)");
            var buffer = GetEncodeBuffer(Transport.HeaderLength);
            _messageFactory.CreateUnconfirmedNotify(buffer, adr, subscriberProcessIdentifier, initiatingDeviceIdentifier, monitoredObjectIdentifier, timeRemaining, values);
            var sendbytes = Transport.Send(buffer.buffer, Transport.HeaderLength, buffer.offset - Transport.HeaderLength, adr, false, 0);
            return sendbytes == buffer.offset;
        }

        maxRetries ??= Retries;
        timeout ??= Timeout;

        using var result = (BacnetAsyncResult)BeginConfirmedNotify(adr, subscriberProcessIdentifier, initiatingDeviceIdentifier, monitoredObjectIdentifier, timeRemaining, values, true);
        for (var r = 0; r < maxRetries; r++)
        {
            if (result.WaitForDone(timeout.Value))
            {
                EndConfirmedNotify(result, out var ex);
                if (ex != null)
                    throw ex;
                return true;
            }
            if (r < maxRetries - 1)
                result.Resend();
        }

        return false;
    }

    public void LifeSafetyOperationRequest(BacnetAddress address, BacnetObjectId objectId, string requestingSrc, BacnetLifeSafetyOperations operation, byte invokeId = 0, int? maxRetries = null, int? timeout = null)
    {
        maxRetries ??= Retries;
        timeout ??= Timeout;

        using var result = (BacnetAsyncResult)BeginLifeSafetyOperationRequest(address, objectId, 0, requestingSrc, operation, true, invokeId);
        for (var r = 0; r < maxRetries; r++)
        {
            if (result.WaitForDone(timeout.Value))
            {
                EndLifeSafetyOperationRequest(result, out var ex);
                if (ex != null)
                    throw ex;

                return;
            }
            if (r < maxRetries - 1)
                result.Resend();
        }

        throw OperationTimedOut();
    }

    public IAsyncResult BeginLifeSafetyOperationRequest(BacnetAddress address, BacnetObjectId objectId, uint processId, string requestingSrc, BacnetLifeSafetyOperations operation, bool waitForTransmit, byte invokeId = 0)
    {
        Log.Debug($"Sending {ToTitleCase(operation)} {objectId}");
        if (invokeId == 0)
            invokeId = mInvokeId.GetNext();

        var buffer = GetEncodeBuffer(Transport.HeaderLength);
        _messageFactory.CreateLifeSafetyOperationRequest(buffer, address, objectId, processId, requestingSrc, operation, waitForTransmit, invokeId);

        //send
        var ret = new BacnetAsyncResult(this, address, invokeId, buffer.buffer, buffer.offset - Transport.HeaderLength, waitForTransmit, TransmitTimeout);
        ret.Resend();

        return ret;
    }

    public void EndLifeSafetyOperationRequest(IAsyncResult result, out Exception ex)
    {
        var res = (BacnetAsyncResult)result;
        ex = res.Error;
        if (ex == null && !res.WaitForDone(Timeout))
            ex = new Exception("Wait Timeout");

        res.Dispose();
    }

    public Segmentation GetSegmentBuffer(BacnetMaxSegments maxSegments)
    {
        if (maxSegments == BacnetMaxSegments.MAX_SEG0)
            return null;

        return new Segmentation
        {
            buffer = GetEncodeBuffer(Transport.HeaderLength),
            max_segments = maxSegments.ToByte(),
            window_size = ProposedWindowSize
        };
    }

    private EncodeBuffer EncodeSegmentHeader(BacnetAddress adr, byte invokeId, Segmentation segmentation, BacnetConfirmedServices service, bool moreFollows)
    {
        EncodeBuffer buffer;
        var isSegmented = false;
        if (segmentation == null)
            buffer = GetEncodeBuffer(Transport.HeaderLength);
        else
        {
            buffer = segmentation.buffer;
            isSegmented = segmentation.sequence_number > 0 | moreFollows;
        }
        buffer.Reset(Transport.HeaderLength);

        // encode
        NPDU.Encode(buffer, BacnetNpduControls.PriorityNormalMessage, adr.RoutedDestination, adr.RoutedSource);

        // set segments limits
        buffer.max_offset = buffer.offset + GetMaxApdu();
        var apduHeader = APDU.EncodeComplexAck(buffer, BacnetPduTypes.PDU_TYPE_COMPLEX_ACK | (isSegmented ? BacnetPduTypes.SEGMENTED_MESSAGE | BacnetPduTypes.SERVER : 0) | (moreFollows ? BacnetPduTypes.MORE_FOLLOWS : 0), service, invokeId, segmentation?.sequence_number ?? 0, segmentation?.window_size ?? 0);
        buffer.min_limit = (GetMaxApdu() - apduHeader) * (segmentation?.sequence_number ?? 0);

        return buffer;
    }

    private bool EncodeSegment(BacnetAddress adr, byte invokeId, Segmentation segmentation, BacnetConfirmedServices service, out EncodeBuffer buffer, Action<EncodeBuffer> apduContentEncode)
    {
        // encode (regular)
        buffer = EncodeSegmentHeader(adr, invokeId, segmentation, service, false);
        apduContentEncode(buffer);

        var moreFollows = (buffer.result & EncodeResult.NotEnoughBuffer) > 0;
        if (segmentation != null && moreFollows)
        {
            // reencode in segmented
            EncodeSegmentHeader(adr, invokeId, segmentation, service, true);
            apduContentEncode(buffer);
            return true;
        }

        if (moreFollows)
            return true;

        return segmentation != null && segmentation.sequence_number > 0;
    }

    /// <summary>
    /// Handle the segmentation of several too hugh response (if it's accepted by the client) 
    /// used by ReadRange, ReadProperty, ReadPropertyMultiple & ReadFile responses
    /// </summary>
    private void HandleSegmentationResponse(BacnetAddress adr, byte invokeId, Segmentation segmentation, Action<Segmentation> transmit)
    {
        // send first
        transmit(segmentation);

        if (segmentation == null || segmentation.buffer.result == EncodeResult.Good)
            return;

        // start new thread to handle the segment sequence (if required)
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var oldMaxInfoFrames = Transport.MaxInfoFrames;
            Transport.MaxInfoFrames = segmentation.window_size; // increase max_info_frames, to increase throughput. This might be against 'standard'

                while (true)
            {
                var moreFollows = (segmentation.buffer.result & EncodeResult.NotEnoughBuffer) > 0;

                    // wait for segmentACK
                    if ((segmentation.sequence_number - 1) % segmentation.window_size == 0 || !moreFollows)
                {
                    if (!WaitForAllTransmits(TransmitTimeout))
                    {
                        Log.Warn("Transmit timeout");
                        break;
                    }

                    var currentNumber = segmentation.sequence_number;

                    if (!WaitForSegmentAck(adr, invokeId, segmentation, Timeout))
                    {
                        Log.Warn("Didn't get segmentACK");
                        break;
                    }

                    if (segmentation.sequence_number != currentNumber)
                    {
                        Log.Debug("Oh, a retransmit");
                        moreFollows = true;
                    }
                }
                else
                {
                    // a negative segmentACK perhaps
                    var currentNumber = segmentation.sequence_number;
                    WaitForSegmentAck(adr, invokeId, segmentation, 0); // don't wait

                    if (segmentation.sequence_number != currentNumber)
                        Log.Debug("Oh, a retransmit");
                }

                if (moreFollows)
                    // lock (m_lockObject) transmit(segmentation);
                    transmit(segmentation);
                else
                    break;
            }

            Transport.MaxInfoFrames = oldMaxInfoFrames;
        });
    }

    private void SendComplexAck(BacnetAddress adr, byte invokeId, Segmentation segmentation, BacnetConfirmedServices service, Action<EncodeBuffer> apduContentEncode)
    {
        Log.Debug($"Sending {ToTitleCase(service)}");

        //encode
        if (EncodeSegment(adr, invokeId, segmentation, service, out var buffer, apduContentEncode))
        {
            //client doesn't support segments
            if (segmentation == null)
            {
                Log.Info("Segmentation denied");
                SendAbort(adr, invokeId, BacnetAbortReason.SEGMENTATION_NOT_SUPPORTED);
                //ErrorResponse(adr, service, invokeId, BacnetErrorClasses.ERROR_CLASS_SERVICES, BacnetErrorCodes.ERROR_CODE_ABORT_APDU_TOO_LONG);
                buffer.result = EncodeResult.Good;     //don't continue the segmentation
                return;
            }

            //first segment? validate max segments
            if (segmentation.sequence_number == 0)  //only validate first segment
            {
                if (segmentation.max_segments != 0xFF && segmentation.buffer.offset > segmentation.max_segments * (GetMaxApdu() - 5))      //5 is adpu header
                {
                    Log.Info("Too much segmenation");
                    SendAbort(adr, invokeId, BacnetAbortReason.APDU_TOO_LONG);
                    //ErrorResponse(adr, service, invokeId, BacnetErrorClasses.ERROR_CLASS_SERVICES, BacnetErrorCodes.ERROR_CODE_ABORT_APDU_TOO_LONG);
                    buffer.result = EncodeResult.Good;     //don't continue the segmentation
                    return;
                }
                Log.Debug("Segmentation required");
            }

            //increment before ack can do so (race condition)
            unchecked { segmentation.sequence_number++; }
        }

        //send
        Transport.Send(buffer.buffer, Transport.HeaderLength, buffer.GetLength() - Transport.HeaderLength, adr, false, 0);
    }

    public void ReadPropertyResponse(BacnetAddress adr, byte invokeId, Segmentation segmentation, BacnetObjectId objectId, BacnetPropertyReference property, IEnumerable<BacnetValue> value)
    {
        HandleSegmentationResponse(adr, invokeId, segmentation, _ =>
        {
            SendComplexAck(adr, invokeId, segmentation, BacnetConfirmedServices.SERVICE_CONFIRMED_READ_PROPERTY, b =>
            {
                Services.EncodeReadPropertyAcknowledge(b, objectId, property.propertyIdentifier, property.propertyArrayIndex, value);
            });
        });
    }

    public void CreateObjectResponse(BacnetAddress adr, byte invokeId, Segmentation segmentation, BacnetObjectId objectId)
    {
        SendComplexAck(adr, invokeId, segmentation, BacnetConfirmedServices.SERVICE_CONFIRMED_CREATE_OBJECT, b =>
        {
            Services.EncodeCreateObjectAcknowledge(b, objectId);
        });
    }

    public void ReadPropertyMultipleResponse(BacnetAddress adr, byte invokeId, Segmentation segmentation, IList<BacnetReadAccessResult> values)
    {
        HandleSegmentationResponse(adr, invokeId, segmentation, _ =>
        {
            SendComplexAck(adr, invokeId, segmentation, BacnetConfirmedServices.SERVICE_CONFIRMED_READ_PROP_MULTIPLE, b =>
            {
                Services.EncodeReadPropertyMultipleAcknowledge(b, values);
            });
        });
    }

    public void ReadRangeResponse(BacnetAddress adr, byte invokeId, Segmentation segmentation, BacnetObjectId objectId, BacnetPropertyReference property, BacnetResultFlags status, uint itemCount, byte[] applicationData, BacnetReadRangeRequestTypes requestType, uint firstSequenceNo)
    {
        HandleSegmentationResponse(adr, invokeId, segmentation, _ =>
        {
            SendComplexAck(adr, invokeId, segmentation, BacnetConfirmedServices.SERVICE_CONFIRMED_READ_RANGE, b =>
            {
                Services.EncodeReadRangeAcknowledge(b, objectId, property.propertyIdentifier, property.propertyArrayIndex, BacnetBitString.ConvertFromInt((uint)status), itemCount, applicationData, requestType, firstSequenceNo);
            });
        });
    }

    public void ReadFileResponse(BacnetAddress adr, byte invokeId, Segmentation segmentation, int position, uint count, bool endOfFile, byte[] fileBuffer)
    {
        HandleSegmentationResponse(adr, invokeId, segmentation, _ =>
        {
            SendComplexAck(adr, invokeId, segmentation, BacnetConfirmedServices.SERVICE_CONFIRMED_ATOMIC_READ_FILE, b =>
            {
                Services.EncodeAtomicReadFileAcknowledge(b, true, endOfFile, position, 1, new[] { fileBuffer }, new[] { (int)count });
            });
        });
    }

    public void WriteFileResponse(BacnetAddress adr, byte invokeId, Segmentation segmentation, int position)
    {
        SendComplexAck(adr, invokeId, segmentation, BacnetConfirmedServices.SERVICE_CONFIRMED_ATOMIC_WRITE_FILE, b =>
        {
            Services.EncodeAtomicWriteFileAcknowledge(b, true, position);
        });
    }

    public void ErrorResponse(BacnetAddress adr, BacnetConfirmedServices service, byte invokeId, BacnetErrorClasses errorClass, BacnetErrorCodes errorCode)
    {
        Log.Debug($"Sending ErrorResponse for {service}: {errorCode}");
        var buffer = GetEncodeBuffer(Transport.HeaderLength);
        _messageFactory.CreateErrorResponse(buffer, adr, service, invokeId, errorClass, errorCode);
        Transport.Send(buffer.buffer, Transport.HeaderLength, buffer.offset - Transport.HeaderLength, adr, false, 0);
    }

    public void SimpleAckResponse(BacnetAddress adr, BacnetConfirmedServices service, byte invokeId)
    {
        Log.Debug($"Sending SimpleAckResponse for {service}");
        var buffer = GetEncodeBuffer(Transport.HeaderLength);
        _messageFactory.CreateSimpleAckResponse(buffer, adr, service, invokeId);
        Transport.Send(buffer.buffer, Transport.HeaderLength, buffer.offset - Transport.HeaderLength, adr, false, 0);
    }

    public void SegmentAckResponse(BacnetAddress adr, bool negative, bool server, byte originalInvokeId, byte sequenceNumber, byte actualWindowSize)
    {
        Log.Debug("Sending SegmentAckResponse");
        var buffer = GetEncodeBuffer(Transport.HeaderLength);
        _messageFactory.CreateSegmentAckResponse(buffer, adr, negative, server, originalInvokeId, sequenceNumber, actualWindowSize);
        Transport.Send(buffer.buffer, Transport.HeaderLength, buffer.offset - Transport.HeaderLength, adr, false, 0);
    }

    public bool WaitForAllTransmits(int timeout)
    {
        return Transport.WaitForAllTransmits(timeout);
    }

    public bool WaitForSegmentAck(BacnetAddress adr, byte invokeId, Segmentation segmentation, int timeout)
    {
        if (!_lastSegmentAck.Wait(adr, invokeId, timeout))
            return false;

        segmentation.sequence_number = (byte)((_lastSegmentAck.SequenceNumber + 1) % 256);
        segmentation.window_size = _lastSegmentAck.WindowSize;
        return true;
    }

    private static string ToTitleCase(object obj)
    {
        var cultureTextInfo = Thread.CurrentThread.CurrentCulture.TextInfo;
        return cultureTextInfo.ToTitleCase($"{obj}".ToLower());
    }

    public void Dispose()
    {
        Transport.Dispose();
    }
}
