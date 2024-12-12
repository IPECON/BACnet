namespace System.IO.BACnet.Factory
{
    public class BacnetMessageFactory
    {
        private readonly IBacnetMessageFactoryParameters _parameters;

        public BacnetMessageFactory(IBacnetMessageFactoryParameters bacnetMessageFactoryParameters)
        {
            _parameters = bacnetMessageFactoryParameters;
        }

        public void CreateSynchronizeTime(EncodeBuffer buffer, BacnetAddress adr, DateTime dateTime, BacnetAddress source = null)
        {
            NPDU.Encode(buffer, BacnetNpduControls.PriorityNormalMessage, adr, source);
            APDU.EncodeUnconfirmedServiceRequest(buffer, BacnetPduTypes.PDU_TYPE_UNCONFIRMED_SERVICE_REQUEST, dateTime.Kind == DateTimeKind.Utc
                ? BacnetUnconfirmedServices.SERVICE_UNCONFIRMED_UTC_TIME_SYNCHRONIZATION
                : BacnetUnconfirmedServices.SERVICE_UNCONFIRMED_TIME_SYNCHRONIZATION);
            Services.EncodeTimeSync(buffer, dateTime);
        }

        public void CreateSynchronizeTime(EncodeBuffer buffer, BacnetAddress adr, BacnetDate bacnetDate, BacnetTime bacnetTime, bool isUtc, BacnetAddress source = null)
        {
            NPDU.Encode(buffer, BacnetNpduControls.PriorityNormalMessage, adr, source);
            APDU.EncodeUnconfirmedServiceRequest(buffer, BacnetPduTypes.PDU_TYPE_UNCONFIRMED_SERVICE_REQUEST, isUtc
                ? BacnetUnconfirmedServices.SERVICE_UNCONFIRMED_UTC_TIME_SYNCHRONIZATION
                : BacnetUnconfirmedServices.SERVICE_UNCONFIRMED_TIME_SYNCHRONIZATION);

            ASN1.encode_tag(buffer, (byte)BacnetApplicationTags.BACNET_APPLICATION_TAG_DATE, false, 4);
            bacnetDate.Encode(buffer);
            ASN1.encode_tag(buffer, (byte)BacnetApplicationTags.BACNET_APPLICATION_TAG_TIME, false, 4);
            bacnetTime.Encode(buffer);
        }

        public void CreateWriteFileRequest(EncodeBuffer buffer, BacnetAddress adr, BacnetObjectId objectId, int position, int count, byte[] fileBuffer, bool waitForTransmit, byte invokeId = 0)
        {
            NPDU.Encode(buffer, BacnetNpduControls.PriorityNormalMessage | BacnetNpduControls.ExpectingReply, adr.RoutedDestination, adr.RoutedSource);
            APDU.EncodeConfirmedServiceRequest(buffer, _parameters.PduConfirmedServiceRequest(), BacnetConfirmedServices.SERVICE_CONFIRMED_ATOMIC_WRITE_FILE, _parameters.MaxSegments, _parameters.MaxApduLength, invokeId);
            Services.EncodeAtomicWriteFile(buffer, true, objectId, position, 1, new[] { fileBuffer }, new[] { count });
        }

        public void CreateReadFileRequest(EncodeBuffer buffer, BacnetAddress adr, BacnetObjectId objectId, int position, uint count, bool waitForTransmit, byte invokeId = 0)
        {
            NPDU.Encode(buffer, BacnetNpduControls.PriorityNormalMessage | BacnetNpduControls.ExpectingReply, adr.RoutedDestination, adr.RoutedSource);
            APDU.EncodeConfirmedServiceRequest(buffer, _parameters.PduConfirmedServiceRequest(), BacnetConfirmedServices.SERVICE_CONFIRMED_ATOMIC_READ_FILE, _parameters.MaxSegments, _parameters.MaxApduLength, invokeId);
            Services.EncodeAtomicReadFile(buffer, true, objectId, position, count);
        }

        public void CreateReadRangeRequestCore(EncodeBuffer buffer, BacnetAddress adr, BacnetObjectId objectId, BacnetReadRangeRequestTypes bacnetReadRangeRequestTypes, DateTime readFrom, uint idxBegin,
            uint quantity, bool waitForTransmit, byte invokeId = 0)
        {
            NPDU.Encode(buffer, BacnetNpduControls.PriorityNormalMessage | BacnetNpduControls.ExpectingReply, adr.RoutedDestination, adr.RoutedSource);
            APDU.EncodeConfirmedServiceRequest(buffer, _parameters.PduConfirmedServiceRequest(), BacnetConfirmedServices.SERVICE_CONFIRMED_READ_RANGE, _parameters.MaxSegments, _parameters.MaxApduLength, invokeId);
            Services.EncodeReadRange(buffer, objectId, (uint)BacnetPropertyIds.PROP_LOG_BUFFER, ASN1.BACNET_ARRAY_ALL, bacnetReadRangeRequestTypes, idxBegin, readFrom, (int)quantity);
        }

        public void CreateSubscribeCOVRequest(EncodeBuffer buffer, BacnetAddress adr, BacnetObjectId objectId, uint subscribeId, bool cancel, bool issueConfirmedNotifications, uint lifetime, bool waitForTransmit, byte invokeId = 0)
        {
            NPDU.Encode(buffer, BacnetNpduControls.PriorityNormalMessage | BacnetNpduControls.ExpectingReply, adr.RoutedDestination, adr.RoutedSource);
            APDU.EncodeConfirmedServiceRequest(buffer, _parameters.PduConfirmedServiceRequest(), BacnetConfirmedServices.SERVICE_CONFIRMED_SUBSCRIBE_COV, _parameters.MaxSegments, _parameters.MaxApduLength, invokeId);
            Services.EncodeSubscribeCOV(buffer, subscribeId, objectId, cancel, issueConfirmedNotifications, lifetime);
        }

        public void CreateSendConfirmedEventNotificationRequest(EncodeBuffer buffer, BacnetAddress adr, BacnetEventNotificationData eventData, bool waitForTransmit, byte invokeId = 0, BacnetAddress source = null)
        {
            NPDU.Encode(buffer, BacnetNpduControls.PriorityNormalMessage | BacnetNpduControls.ExpectingReply, adr, source);
            APDU.EncodeConfirmedServiceRequest(buffer, _parameters.PduConfirmedServiceRequest(), BacnetConfirmedServices.SERVICE_CONFIRMED_EVENT_NOTIFICATION, _parameters.MaxSegments, _parameters.MaxApduLength, invokeId);
            Services.EncodeEventNotifyConfirmed(buffer, eventData);
        }

        public void CreateSubscribePropertyRequest(EncodeBuffer buffer, BacnetAddress adr, BacnetObjectId objectId, BacnetPropertyReference monitoredProperty, uint subscribeId, bool cancel, bool issueConfirmedNotifications, uint lifetime, bool waitForTransmit, byte invokeId = 0)
        {
            NPDU.Encode(buffer, BacnetNpduControls.PriorityNormalMessage | BacnetNpduControls.ExpectingReply, adr.RoutedDestination, adr.RoutedSource);
            APDU.EncodeConfirmedServiceRequest(buffer, _parameters.PduConfirmedServiceRequest(), BacnetConfirmedServices.SERVICE_CONFIRMED_SUBSCRIBE_COV_PROPERTY, _parameters.MaxSegments, _parameters.MaxApduLength, invokeId);
            Services.EncodeSubscribeProperty(buffer, subscribeId, objectId, cancel, issueConfirmedNotifications, lifetime, monitoredProperty, false, 0f);
        }

        public void CreateReadPropertyRequest(EncodeBuffer buffer, BacnetAddress address, BacnetObjectId objectId, BacnetPropertyIds propertyId, bool waitForTransmit, byte invokeId = 0, uint arrayIndex = ASN1.BACNET_ARRAY_ALL)
        {
            NPDU.Encode(buffer, BacnetNpduControls.PriorityNormalMessage | BacnetNpduControls.ExpectingReply, address.RoutedDestination, address.RoutedSource);
            APDU.EncodeConfirmedServiceRequest(buffer, _parameters.PduConfirmedServiceRequest(), BacnetConfirmedServices.SERVICE_CONFIRMED_READ_PROPERTY, _parameters.MaxSegments, _parameters.MaxApduLength, invokeId);
            Services.EncodeReadProperty(buffer, objectId, (uint)propertyId, arrayIndex);
        }

        public void CreateWritePropertyRequest(EncodeBuffer buffer, BacnetAddress adr, BacnetObjectId objectId, BacnetPropertyIds propertyId, IEnumerable<BacnetValue> valueList, bool waitForTransmit,
            uint arrayIndex = ASN1.BACNET_ARRAY_ALL, byte invokeId = 0, BacnetWritePriority writePriority = BacnetWritePriority.NO_PRIORITY)
        {
            NPDU.Encode(buffer, BacnetNpduControls.PriorityNormalMessage | BacnetNpduControls.ExpectingReply, adr.RoutedDestination, adr.RoutedSource);
            APDU.EncodeConfirmedServiceRequest(buffer, BacnetPduTypes.PDU_TYPE_CONFIRMED_SERVICE_REQUEST, BacnetConfirmedServices.SERVICE_CONFIRMED_WRITE_PROPERTY, _parameters.MaxSegments, _parameters.MaxApduLength, invokeId);
            Services.EncodeWriteProperty(buffer, objectId, (uint)propertyId, arrayIndex, (uint)writePriority, valueList);
        }

        public void CreateWritePropertyMultipleRequest(EncodeBuffer buffer, BacnetAddress adr, BacnetObjectId objectId, ICollection<BacnetPropertyValue> valueList, bool waitForTransmit,
            byte invokeId = 0)
        {
            NPDU.Encode(buffer, BacnetNpduControls.PriorityNormalMessage | BacnetNpduControls.ExpectingReply, adr.RoutedDestination, adr.RoutedSource);
            APDU.EncodeConfirmedServiceRequest(buffer, BacnetPduTypes.PDU_TYPE_CONFIRMED_SERVICE_REQUEST, BacnetConfirmedServices.SERVICE_CONFIRMED_WRITE_PROP_MULTIPLE, _parameters.MaxSegments, _parameters.MaxApduLength, invokeId);
            Services.EncodeWritePropertyMultiple(buffer, objectId, valueList);
        }

        public void CreateWritePropertyMultipleRequest(EncodeBuffer buffer, BacnetAddress adr, ICollection<BacnetReadAccessResult> valueList, bool waitForTransmit, byte invokeId = 0)
        {
            NPDU.Encode(buffer, BacnetNpduControls.PriorityNormalMessage | BacnetNpduControls.ExpectingReply, adr.RoutedDestination, adr.RoutedSource);
            APDU.EncodeConfirmedServiceRequest(buffer, BacnetPduTypes.PDU_TYPE_CONFIRMED_SERVICE_REQUEST, BacnetConfirmedServices.SERVICE_CONFIRMED_WRITE_PROP_MULTIPLE, _parameters.MaxSegments, _parameters.MaxApduLength, invokeId);
            Services.EncodeWriteObjectMultiple(buffer, valueList);
        }

        public void CreateReadPropertyMultipleRequest(EncodeBuffer buffer, BacnetAddress adr, BacnetObjectId objectId, IList<BacnetPropertyReference> propertyIdAndArrayIndex, bool waitForTransmit, byte invokeId = 0)
        {
            NPDU.Encode(buffer, BacnetNpduControls.PriorityNormalMessage | BacnetNpduControls.ExpectingReply, adr.RoutedDestination, adr.RoutedSource);
            APDU.EncodeConfirmedServiceRequest(buffer, _parameters.PduConfirmedServiceRequest(), BacnetConfirmedServices.SERVICE_CONFIRMED_READ_PROP_MULTIPLE, _parameters.MaxSegments, _parameters.MaxApduLength, invokeId);
            Services.EncodeReadPropertyMultiple(buffer, objectId, propertyIdAndArrayIndex);
        }

        public void CreateReadPropertyMultipleRequest(EncodeBuffer buffer, BacnetAddress adr, IList<BacnetReadAccessSpecification> properties, bool waitForTransmit, byte invokeId = 0)
        {
            NPDU.Encode(buffer, BacnetNpduControls.PriorityNormalMessage | BacnetNpduControls.ExpectingReply, adr.RoutedDestination, adr.RoutedSource);
            APDU.EncodeConfirmedServiceRequest(buffer, _parameters.PduConfirmedServiceRequest(), BacnetConfirmedServices.SERVICE_CONFIRMED_READ_PROP_MULTIPLE, _parameters.MaxSegments, _parameters.MaxApduLength, invokeId);
            Services.EncodeReadPropertyMultiple(buffer, properties);
        }

        public void CreateCreateObjectRequest(EncodeBuffer buffer, BacnetAddress adr, BacnetObjectId objectId, ICollection<BacnetPropertyValue> valueList, bool waitForTransmit, byte invokeId = 0)
        {
            NPDU.Encode(buffer, BacnetNpduControls.PriorityNormalMessage | BacnetNpduControls.ExpectingReply, adr.RoutedDestination, adr.RoutedSource);
            APDU.EncodeConfirmedServiceRequest(buffer, _parameters.PduConfirmedServiceRequest(), BacnetConfirmedServices.SERVICE_CONFIRMED_CREATE_OBJECT, _parameters.MaxSegments, _parameters.MaxApduLength, invokeId);
            Services.EncodeCreateProperty(buffer, objectId, valueList);
        }

        public void CreateDeleteObjectRequest(EncodeBuffer buffer, BacnetAddress adr, BacnetObjectId objectId, bool waitForTransmit, byte invokeId = 0)
        {
            NPDU.Encode(buffer, BacnetNpduControls.PriorityNormalMessage | BacnetNpduControls.ExpectingReply, adr.RoutedDestination, adr.RoutedSource);
            APDU.EncodeConfirmedServiceRequest(buffer, BacnetPduTypes.PDU_TYPE_CONFIRMED_SERVICE_REQUEST, BacnetConfirmedServices.SERVICE_CONFIRMED_DELETE_OBJECT, _parameters.MaxSegments, _parameters.MaxApduLength, invokeId);
            ASN1.encode_application_object_id(buffer, objectId.type, objectId.instance);
        }

        public void CreateRemoveListElementRequest(EncodeBuffer buffer, BacnetAddress adr, BacnetObjectId objectId, BacnetPropertyReference reference, IList<BacnetValue> valueList, bool waitForTransmit, byte invokeId = 0)
        {
            NPDU.Encode(buffer, BacnetNpduControls.PriorityNormalMessage | BacnetNpduControls.ExpectingReply, adr.RoutedDestination, adr.RoutedSource);
            APDU.EncodeConfirmedServiceRequest(buffer, BacnetPduTypes.PDU_TYPE_CONFIRMED_SERVICE_REQUEST, BacnetConfirmedServices.SERVICE_CONFIRMED_REMOVE_LIST_ELEMENT, _parameters.MaxSegments, _parameters.MaxApduLength, invokeId);
            Services.EncodeAddListElement(buffer, objectId, reference.propertyIdentifier, reference.propertyArrayIndex, valueList);
        }

        public void CreateAddListElementRequest(EncodeBuffer buffer, BacnetAddress adr, BacnetObjectId objectId, BacnetPropertyReference reference, IList<BacnetValue> valueList, bool waitForTransmit, byte invokeId = 0)
        {
            NPDU.Encode(buffer, BacnetNpduControls.PriorityNormalMessage | BacnetNpduControls.ExpectingReply, adr.RoutedDestination, adr.RoutedSource);
            APDU.EncodeConfirmedServiceRequest(buffer, BacnetPduTypes.PDU_TYPE_CONFIRMED_SERVICE_REQUEST, BacnetConfirmedServices.SERVICE_CONFIRMED_ADD_LIST_ELEMENT, _parameters.MaxSegments, _parameters.MaxApduLength, invokeId);
            Services.EncodeAddListElement(buffer, objectId, reference.propertyIdentifier, reference.propertyArrayIndex, valueList);
        }

        public void CreateRawEncodedDecodedPropertyConfirmedRequest(EncodeBuffer buffer, BacnetAddress adr, BacnetObjectId objectId, BacnetPropertyIds propertyId, BacnetConfirmedServices serviceId, byte[] inOutBuffer, bool waitForTransmit, byte invokeId = 0)
        {
            NPDU.Encode(buffer, BacnetNpduControls.PriorityNormalMessage | BacnetNpduControls.ExpectingReply, adr.RoutedDestination, adr.RoutedSource);
            APDU.EncodeConfirmedServiceRequest(buffer, _parameters.PduConfirmedServiceRequest(), serviceId, _parameters.MaxSegments, _parameters.MaxApduLength, invokeId);
            ASN1.encode_context_object_id(buffer, 0, objectId.type, objectId.instance);
            ASN1.encode_context_enumerated(buffer, 1, (byte)propertyId);
        }

        public void CreateDeviceCommunicationControlRequest(EncodeBuffer buffer, BacnetAddress adr, uint timeDuration, uint enableDisable, string password, bool waitForTransmit, byte invokeId = 0)
        {
            NPDU.Encode(buffer, BacnetNpduControls.PriorityNormalMessage | BacnetNpduControls.ExpectingReply, adr.RoutedDestination, adr.RoutedSource);
            APDU.EncodeConfirmedServiceRequest(buffer, BacnetPduTypes.PDU_TYPE_CONFIRMED_SERVICE_REQUEST, BacnetConfirmedServices.SERVICE_CONFIRMED_DEVICE_COMMUNICATION_CONTROL, _parameters.MaxSegments, _parameters.MaxApduLength, invokeId);
            Services.EncodeDeviceCommunicationControl(buffer, timeDuration, enableDisable, password);
        }

        public void CreateGetAlarmSummaryOrEventRequest(EncodeBuffer buffer, BacnetAddress adr, bool getEvent, IList<BacnetGetEventInformationData> alarms, bool waitForTransmit, byte invokeId = 0)
        {
            NPDU.Encode(buffer, BacnetNpduControls.PriorityNormalMessage | BacnetNpduControls.ExpectingReply, adr.RoutedDestination, adr.RoutedSource);

            var service = getEvent
                ? BacnetConfirmedServices.SERVICE_CONFIRMED_GET_EVENT_INFORMATION
                : BacnetConfirmedServices.SERVICE_CONFIRMED_GET_ALARM_SUMMARY;

            APDU.EncodeConfirmedServiceRequest(buffer, _parameters.PduConfirmedServiceRequest(), service, _parameters.MaxSegments, _parameters.MaxApduLength, invokeId);

            // Get Next, never true if GetAlarmSummary is usee
            if (alarms.Count != 0)
                ASN1.encode_context_object_id(buffer, 0, alarms[alarms.Count - 1].objectIdentifier.type, alarms[alarms.Count - 1].objectIdentifier.instance);
        }

        public void CreateAlarmAcknowledgement(EncodeBuffer buffer, BacnetAddress adr, BacnetObjectId objId, BacnetEventStates eventState, string ackText, BacnetGenericTime evTimeStamp, BacnetGenericTime ackTimeStamp, bool waitForTransmit, byte invokeId = 0, uint ackProcessIdentifier = 57)
        {
            NPDU.Encode(buffer, BacnetNpduControls.PriorityNormalMessage, adr.RoutedDestination, adr.RoutedSource);
            APDU.EncodeConfirmedServiceRequest(buffer, BacnetPduTypes.PDU_TYPE_CONFIRMED_SERVICE_REQUEST, BacnetConfirmedServices.SERVICE_CONFIRMED_ACKNOWLEDGE_ALARM, _parameters.MaxSegments, _parameters.MaxApduLength, invokeId);
            Services.EncodeAlarmAcknowledge(buffer, ackProcessIdentifier, objId, (uint)eventState, ackText, evTimeStamp, ackTimeStamp);
        }

        public void CreateReinitializeRequest(EncodeBuffer buffer, BacnetAddress adr, BacnetReinitializedStates state, string password, bool waitForTransmit, byte invokeId = 0)
        {
            NPDU.Encode(buffer, BacnetNpduControls.PriorityNormalMessage | BacnetNpduControls.ExpectingReply, adr.RoutedDestination, adr.RoutedSource);
            APDU.EncodeConfirmedServiceRequest(buffer, BacnetPduTypes.PDU_TYPE_CONFIRMED_SERVICE_REQUEST, BacnetConfirmedServices.SERVICE_CONFIRMED_REINITIALIZE_DEVICE, _parameters.MaxSegments, _parameters.MaxApduLength, invokeId);
            Services.EncodeReinitializeDevice(buffer, state, password);
        }

        public void CreateConfirmedNotify(EncodeBuffer buffer, BacnetAddress adr, uint subscriberProcessIdentifier, uint initiatingDeviceIdentifier, BacnetObjectId monitoredObjectIdentifier, uint timeRemaining, IList<BacnetPropertyValue> values, bool waitForTransmit, byte invokeId = 0)
        {
            NPDU.Encode(buffer, BacnetNpduControls.PriorityNormalMessage | BacnetNpduControls.ExpectingReply, adr.RoutedDestination, adr.RoutedSource);
            APDU.EncodeConfirmedServiceRequest(buffer, BacnetPduTypes.PDU_TYPE_CONFIRMED_SERVICE_REQUEST, BacnetConfirmedServices.SERVICE_CONFIRMED_COV_NOTIFICATION, _parameters.MaxSegments, _parameters.MaxApduLength, invokeId);
            Services.EncodeCOVNotifyConfirmed(buffer, subscriberProcessIdentifier, initiatingDeviceIdentifier, monitoredObjectIdentifier, timeRemaining, values);
        }

        public void CreateUnconfirmedNotify(EncodeBuffer buffer, BacnetAddress adr, uint subscriberProcessIdentifier, uint initiatingDeviceIdentifier, BacnetObjectId monitoredObjectIdentifier, uint timeRemaining, IList<BacnetPropertyValue> values)
        {
            NPDU.Encode(buffer, BacnetNpduControls.PriorityNormalMessage, adr.RoutedDestination, adr.RoutedSource);
            APDU.EncodeUnconfirmedServiceRequest(buffer, BacnetPduTypes.PDU_TYPE_UNCONFIRMED_SERVICE_REQUEST, BacnetUnconfirmedServices.SERVICE_UNCONFIRMED_COV_NOTIFICATION);
            Services.EncodeCOVNotifyUnconfirmed(buffer, subscriberProcessIdentifier, initiatingDeviceIdentifier, monitoredObjectIdentifier, timeRemaining, values);
        }

        public void CreateLifeSafetyOperationRequest(EncodeBuffer buffer, BacnetAddress address, BacnetObjectId objectId, uint processId, string requestingSrc, BacnetLifeSafetyOperations operation, bool waitForTransmit, byte invokeId = 0)
        {
            NPDU.Encode(buffer, BacnetNpduControls.PriorityNormalMessage | BacnetNpduControls.ExpectingReply, address.RoutedDestination, address.RoutedSource);
            APDU.EncodeConfirmedServiceRequest(buffer, BacnetPduTypes.PDU_TYPE_CONFIRMED_SERVICE_REQUEST, BacnetConfirmedServices.SERVICE_CONFIRMED_LIFE_SAFETY_OPERATION, _parameters.MaxSegments, _parameters.MaxApduLength, invokeId);
            Services.EncodeLifeSafetyOperation(buffer, processId, requestingSrc, (uint)operation, objectId);
        }

        public void CreateErrorResponse(EncodeBuffer buffer, BacnetAddress adr, BacnetConfirmedServices service, byte invokeId, BacnetErrorClasses errorClass, BacnetErrorCodes errorCode)
        {
            NPDU.Encode(buffer, BacnetNpduControls.PriorityNormalMessage, adr.RoutedDestination, adr.RoutedSource);
            APDU.EncodeError(buffer, BacnetPduTypes.PDU_TYPE_ERROR, service, invokeId);
            Services.EncodeError(buffer, errorClass, errorCode);
        }

        public void CreateSimpleAckResponse(EncodeBuffer buffer, BacnetAddress adr, BacnetConfirmedServices service, byte invokeId)
        {
            NPDU.Encode(buffer, BacnetNpduControls.PriorityNormalMessage, adr.RoutedDestination, adr.RoutedSource);
            APDU.EncodeSimpleAck(buffer, BacnetPduTypes.PDU_TYPE_SIMPLE_ACK, service, invokeId);
        }

        public void CreateSegmentAckResponse(EncodeBuffer buffer, BacnetAddress adr, bool negative, bool server, byte originalInvokeId, byte sequenceNumber, byte actualWindowSize)
        {
            NPDU.Encode(buffer, BacnetNpduControls.PriorityNormalMessage, adr.RoutedDestination, adr.RoutedSource);
            APDU.EncodeSegmentAck(buffer, BacnetPduTypes.PDU_TYPE_SEGMENT_ACK | (negative ? BacnetPduTypes.NEGATIVE_ACK : 0) | (server ? BacnetPduTypes.SERVER : 0), originalInvokeId, sequenceNumber, actualWindowSize);
        }
    }
}
