﻿namespace System.IO.BACnet;

/*
BACnetTimeValue ::= SEQUENCE {
 time Time,
 value ABSTRACT-SYNTAX.&Type -- any primitive datatype, complex types cannot be decoded
 }
*/
public struct BacnetTimeValue : ASN1.IEncode, ASN1.IDecode
{
    public BacnetTime Time { get; set; }
    public uint? Value { get; set; }

    public void Encode(EncodeBuffer buffer)
    {
        //Time.Encode(buffer);
        //ASN1.encode_application_unsigned(buffer, Value);
        throw new NotImplementedException();
    }

    public int Decode(byte[] buffer, int offset, uint count)
    {
        int len = 0;
        Time = new BacnetTime();
        len += ASN1.decode_tag_number(buffer, offset + len, out var tagNumber);
        len += Time.Decode(buffer, offset + len, count);

        // TODO
        // This can be any primitive type - not only unsigned
        len += ASN1.decode_tag_number_and_value(buffer, offset + len, out tagNumber, out var lenValueType);
        if (tagNumber == (byte)BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL)
        {
            Value = null;
        }
        else if (tagNumber == (byte)BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT || tagNumber == (byte)BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED)
        {
            len += ASN1.decode_unsigned(buffer, offset + len, lenValueType, out var value);
            Value = value;
        }
        else
        {
            throw new NotImplementedException($"Decode for tag number {tagNumber} is not implemented.");
        }
        
        return len;
    }

    public override string ToString()
    {
        return $"{Time} - {Value}";
    }
}
