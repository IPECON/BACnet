namespace System.IO.BACnet;

public struct BacnetCOVSubscription : ASN1.IEncode, ASN1.IDecode
{
    /* BACnetRecipientProcess */
    public BacnetAddress Recipient { get; set; }
    public uint SubscriptionProcessIdentifier { get; set; }
    /* BACnetObjectPropertyReference */
    public BacnetObjectId MonitoredObjectIdentifier { get; set; }
    public BacnetPropertyReference MonitoredProperty { get; set; }
    /* BACnetCOVSubscription */
    public bool IssueConfirmedNotifications { get; set; }
    public uint TimeRemaining { get; set; }
    public float CovIncrement { get; set; }

    public void Encode(EncodeBuffer buffer)
    {
        /* Recipient [0] BACnetRecipientProcess - opening */
        ASN1.encode_opening_tag(buffer, 0);

        /*  recipient [0] BACnetRecipient - opening */
        ASN1.encode_opening_tag(buffer, 0);
        /* CHOICE - device [0] BACnetObjectIdentifier - opening */
        /* CHOICE - address [1] BACnetAddress - opening */
        ASN1.encode_opening_tag(buffer, 1);
        /* network-number Unsigned16, */
        /* -- A value of 0 indicates the local network */
        ASN1.encode_application_unsigned(buffer, Recipient.net);
        /* mac-address OCTET STRING */
        /* -- A string of length 0 indicates a broadcast */
        if (Recipient.net == 0xFFFF)
            ASN1.encode_application_octet_string(buffer, new byte[0], 0, 0);
        else
            ASN1.encode_application_octet_string(buffer, Recipient.adr, 0, Recipient.adr.Length);
        /* CHOICE - address [1] BACnetAddress - closing */
        ASN1.encode_closing_tag(buffer, 1);
        /*  recipient [0] BACnetRecipient - closing */
        ASN1.encode_closing_tag(buffer, 0);

        /* processIdentifier [1] Unsigned32 */
        ASN1.encode_context_unsigned(buffer, 1, SubscriptionProcessIdentifier);
        /* Recipient [0] BACnetRecipientProcess - closing */
        ASN1.encode_closing_tag(buffer, 0);

        /*  MonitoredPropertyReference [1] BACnetObjectPropertyReference, */
        ASN1.encode_opening_tag(buffer, 1);
        /* objectIdentifier [0] */
        ASN1.encode_context_object_id(buffer, 0, MonitoredObjectIdentifier.type,
            MonitoredObjectIdentifier.instance);
        /* propertyIdentifier [1] */
        /* FIXME: we are monitoring 2 properties! How to encode? */
        ASN1.encode_context_enumerated(buffer, 1, MonitoredProperty.propertyIdentifier);
        if (MonitoredProperty.propertyArrayIndex != ASN1.BACNET_ARRAY_ALL)
            ASN1.encode_context_unsigned(buffer, 2, MonitoredProperty.propertyArrayIndex);
        /* MonitoredPropertyReference [1] - closing */
        ASN1.encode_closing_tag(buffer, 1);

        /* IssueConfirmedNotifications [2] BOOLEAN, */
        ASN1.encode_context_boolean(buffer, 2, IssueConfirmedNotifications);
        /* TimeRemaining [3] Unsigned, */
        ASN1.encode_context_unsigned(buffer, 3, TimeRemaining);
        /* COVIncrement [4] REAL OPTIONAL, */
        if (CovIncrement > 0)
            ASN1.encode_context_real(buffer, 4, CovIncrement);
    }

    public int Decode(byte[] buffer, int offset, uint count)
    {
        var len = 0;

        Recipient = new BacnetAddress(BacnetAddressTypes.None, 0, null);

        if (!ASN1.decode_is_opening_tag_number(buffer, offset + len, 0))
            return -1;
        len++;
        if (!ASN1.decode_is_opening_tag_number(buffer, offset + len, 0))
            return -1;
        len++;
        if (!ASN1.decode_is_opening_tag_number(buffer, offset + len, 1))
            return -1;
        len++;
        len += ASN1.decode_tag_number_and_value(buffer, offset + len, out var tagNumber, out var lenValueType);
        if (tagNumber != (byte)BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT)
            return -1;
        len += ASN1.decode_unsigned(buffer, offset + len, lenValueType, out var tmp);
        Recipient.net = (ushort)tmp;
        len += ASN1.decode_tag_number_and_value(buffer, offset + len, out tagNumber, out lenValueType);
        if (tagNumber != (byte)BacnetApplicationTags.BACNET_APPLICATION_TAG_OCTET_STRING)
            return -1;
        Recipient.adr = new byte[lenValueType];
        len += ASN1.decode_octet_string(buffer, offset + len, (int)count, Recipient.adr, 0, lenValueType);
        if (!ASN1.decode_is_closing_tag_number(buffer, offset + len, 1))
            return -1;
        len++;
        if (!ASN1.decode_is_closing_tag_number(buffer, offset + len, 0))
            return -1;
        len++;

        len += ASN1.decode_tag_number_and_value(buffer, offset + len, out tagNumber, out lenValueType);
        if (tagNumber != 1)
            return -1;
        len += ASN1.decode_unsigned(buffer, offset + len, lenValueType, out var subscriptionProcessIdentifier);
        if (!ASN1.decode_is_closing_tag_number(buffer, offset + len, 0))
            return -1;

        SubscriptionProcessIdentifier = subscriptionProcessIdentifier;
        len++;

        if (!ASN1.decode_is_opening_tag_number(buffer, offset + len, 1))
            return -1;
        len++;
        len += ASN1.decode_tag_number_and_value(buffer, offset + len, out tagNumber, out lenValueType);
        if (tagNumber != 0)
            return -1;
        len += ASN1.decode_object_id(buffer, offset + len, out BacnetObjectTypes monitoredObjectIdentifierType, out uint monitoredObjectIdentifierInstance);
        len += ASN1.decode_tag_number_and_value(buffer, offset + len, out tagNumber, out lenValueType);
        if (tagNumber != 1)
            return -1;

        MonitoredObjectIdentifier = new BacnetObjectId(monitoredObjectIdentifierType, monitoredObjectIdentifierInstance);

        len += ASN1.decode_enumerated(buffer, offset + len, lenValueType, out var monitoredPropertyIdentifier);
        var tagLen = ASN1.decode_tag_number_and_value(buffer, offset + len, out tagNumber, out lenValueType);

        uint monitoredPropertyArrayIndex = ASN1.BACNET_ARRAY_ALL;
        if (tagNumber == 2)
        {
            len += tagLen;
            len += ASN1.decode_unsigned(buffer, offset + len, lenValueType,
                out monitoredPropertyArrayIndex);
        }

        MonitoredProperty = new BacnetPropertyReference(monitoredPropertyIdentifier, monitoredPropertyArrayIndex);

        if (!ASN1.decode_is_closing_tag_number(buffer, offset + len, 1))
            return -1;
        len++;

        len += ASN1.decode_tag_number_and_value(buffer, offset + len, out tagNumber, out lenValueType);
        if (tagNumber != 2)
            return -1;
        IssueConfirmedNotifications = buffer[offset + len] > 0;
        len++;

        len += ASN1.decode_tag_number_and_value(buffer, offset + len, out tagNumber, out lenValueType);
        if (tagNumber != 3)
            return -1;
        len += ASN1.decode_unsigned(buffer, offset + len, lenValueType, out var timeRemaining);
        TimeRemaining = timeRemaining;

        if (len < count && !ASN1.IS_CLOSING_TAG(buffer[offset + len]))
        {
            ASN1.decode_tag_number_and_value(buffer, offset + len, out tagNumber, out lenValueType);
            if (tagNumber != 4)
                return len;
            len++;
            len += ASN1.decode_real(buffer, offset + len, out var covIncrement);
            CovIncrement = covIncrement;
        }

        return len;
    }
}
