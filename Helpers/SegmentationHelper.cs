using System.Collections.Concurrent;

namespace System.IO.BACnet.Helpers
{
    public class SegmentationHelper
    {
        // Concurrent, because segments of replies and of requests are processed on different threads
        private readonly ConcurrentDictionary<byte, SegmentationInfo> _invokeIdToInfoMap = new();

        public void Clear(byte invokeId)
        {
            _invokeIdToInfoMap.TryRemove(invokeId, out _);
        }

        public SegmentationInfo GetSegmentationInfo(byte invokeId, bool createNew)
        {
            if (createNew)
            {
                var newInfo = new SegmentationInfo();
                _invokeIdToInfoMap[invokeId] = newInfo;
                return newInfo;
            }

            return _invokeIdToInfoMap.GetOrAdd(invokeId, _ => new SegmentationInfo());
        }
    }
}
