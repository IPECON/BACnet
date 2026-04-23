namespace System.IO.BACnet
{
    public class BacnetEventTimeStamps : ASN1.IDecode
    {
        public BacnetValue ToOffNormal { get; set; }
        public BacnetValue ToFault { get; set; }
        public BacnetValue ToNormal { get; set; }
        
        public int Decode(byte[] buffer, int offset, uint count)
        {
            int len = 0;
            
            for (int i = 1; i <= 3; i++)
            {
                BacnetValue value = new BacnetValue();
                
                ASN1.decode_tag_number_and_value(buffer, offset + len, out var tagNumber, out var lenValueType);
                len++; // skip Tag

                if (tagNumber == 0) // Time without date
                {
                    len += ASN1.decode_bacnet_time(buffer, offset + len, out var dt);
                    value.Tag = BacnetApplicationTags.BACNET_APPLICATION_TAG_TIMESTAMP;
                    value.Value = new BacnetGenericTime(dt, BacnetTimestampTags.TIME_STAMP_TIME);
                }
                else if (tagNumber == 1) // sequence number
                {
                    len += ASN1.decode_unsigned(buffer, offset + len, lenValueType, out var val);
                    value.Tag = BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT;
                    value.Value = new BacnetGenericTime(DateTime.MinValue, BacnetTimestampTags.TIME_STAMP_SEQUENCE, (ushort)val);
                }
                else if (tagNumber == 2) // date + time
                {
                    len += ASN1.decode_bacnet_datetime(buffer, offset + len, out var dt);
                    value.Tag = BacnetApplicationTags.BACNET_APPLICATION_TAG_TIMESTAMP;
                    len++; // closing Tag
                    value.Value = new BacnetGenericTime(dt, BacnetTimestampTags.TIME_STAMP_DATETIME);
                }
                else
                    throw new Exception($"Unexpected tagNumber ({tagNumber}) when decoding {nameof(BacnetEventTimeStamps)}");

                switch (i)
                {
                    case 1:
                        ToOffNormal = value;
                        break;
                    case 2:
                        ToFault = value;
                        break;
                    case 3:
                        ToNormal = value;
                        break;
                }
            }

            return len;
        }
    }
}
