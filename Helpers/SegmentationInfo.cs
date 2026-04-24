namespace System.IO.BACnet.Helpers
{
    public class SegmentationInfo
    {
        private Dictionary<byte, byte[]> _segments = new ();

        public byte? ExpectedSegments { get; set; }
        public byte? LastAckedSequenceNumber { get; set; }

        public byte GetMaxReceivedSequenceNumber()
        {
            return _segments.Any() ? _segments.Keys.Max() : (byte)0;
        }

        public bool IsContinuous()
        {
            for (byte s = GetMaxReceivedSequenceNumber(); s > 0; s--)
            {
                if (!_segments.ContainsKey(s))
                {
                    return false;
                }
            }

            return true;
        }

        public bool IsComplete()
        {
            if (ExpectedSegments == null)
            {
                return false;
            }

            for (byte i = 0; i < ExpectedSegments.Value; i++)
            {
                if (!_segments.ContainsKey(i))
                {
                    return false;
                }
            }

            return true;
        }

        public bool HasSegment(byte sequenceNumber)
        {
            return _segments.ContainsKey(sequenceNumber);
        }

        public void AddSegment(byte sequenceNumber, byte[] segment)
        {
            _segments[sequenceNumber] = segment;
        }

        public byte[] Build()
        {
            return _segments
                .OrderBy(s => s.Key)
                .SelectMany(s => s.Value)
                .ToArray();
        }

        public byte? GetSequenceNumberToAcknowledge(byte sequenceNumberCurrentlyProcessed, byte proposedWindowSize)
        {
            if (sequenceNumberCurrentlyProcessed == 0)
            {
                return 0;
            }

            if (LastAckedSequenceNumber == null)
            {
                // Segment 0 must be acked first
                return null;
            }

            if (sequenceNumberCurrentlyProcessed <= LastAckedSequenceNumber)
            {
                // Already acked
                return null;
            }

            var maxReceivedSequenceNumber = GetMaxReceivedSequenceNumber();
            for (byte i = (byte)(LastAckedSequenceNumber.Value + 1); i <= maxReceivedSequenceNumber; i++)
            {
                if (!_segments.ContainsKey(i))
                {
                    // Not continuous
                    return null;
                }

                if (i % proposedWindowSize == 0 || (ExpectedSegments != null && i == ExpectedSegments - 1))
                {
                    return i;
                }
            }

            return null;
        }
    }
}
