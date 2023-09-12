namespace System.IO.BACnet;

/*
BACnetTimeValue ::= SEQUENCE {
 time Time,
 value ABSTRACT-SYNTAX.&Type -- any primitive datatype, complex types cannot be decoded
 }
*/
public class BacnetTimeValue : ASN1.IEncode, ASN1.IDecode
{
    public BacnetTime Time { get; set; }
    public BacnetValue Value { get; set; }

    public void Encode(EncodeBuffer buffer)
    {
        ASN1.encode_tag(buffer, (byte)BacnetApplicationTags.BACNET_APPLICATION_TAG_TIME, false, 4);
        Time.Encode(buffer);
        ASN1.bacapp_encode_application_data(buffer, Value);
    }

    public int Decode(byte[] buffer, int offset, uint count)
    {
        int len = 0;
        Time = new BacnetTime();
        len += ASN1.decode_tag_number(buffer, offset + len, out var tagNumber);
        len += Time.Decode(buffer, offset + len, count);

        len += ASN1.decode_tag_number_and_value(buffer, offset + len, out tagNumber, out var lenValueType);
        len += ASN1.bacapp_decode_data(buffer, offset + len, (int)count, (BacnetApplicationTags)tagNumber, lenValueType, out var value);
        Value = value;

        return len;
    }

    public override string ToString()
    {
        return $"{Time} - {Value}";
    }
}
