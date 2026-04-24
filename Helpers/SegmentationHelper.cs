namespace System.IO.BACnet.Helpers
{
    public class SegmentationHelper
    {
        private readonly Dictionary<byte, SegmentationInfo> _invokeIdToInfoMap = new();

        public void Clear(byte invokeId)
        {
            _invokeIdToInfoMap.Remove(invokeId);
        }

        public SegmentationInfo GetSegmentationInfo(byte invokeId, bool createNew)
        {
            if (createNew)
            {
                var newInfo = new SegmentationInfo();
                _invokeIdToInfoMap[invokeId] = newInfo;
                return newInfo;
            }

            if (!_invokeIdToInfoMap.TryGetValue(invokeId, out var info))
            {
                info = new SegmentationInfo();
                _invokeIdToInfoMap[invokeId] = info;
            }

            return info;
        }
    }
}
