namespace System.IO.BACnet.Helpers
{
    /// <summary>
    /// Segmented messages being received. A message is identified by its sender, its invoke ID and whether it's a
    /// request to us or a reply to one of our requests: devices choose the invoke IDs of their own requests, and ours
    /// are shared by all the devices we send requests to.
    /// </summary>
    /// <remarks>
    /// Thread-safe, because segments of replies are processed on the receive thread and segments of requests on the
    /// dispatcher thread. A returned <see cref="SegmentationInfo"/> isn't: lock it while using it.
    /// </remarks>
    public class SegmentationHelper
    {
        private readonly object _lock = new();
        private readonly Dictionary<SegmentationKey, SegmentationInfo> _messages = new();

        /// <summary>
        /// Number of segmented messages being received
        /// </summary>
        public int Count
        {
            get
            {
                lock (_lock)
                    return _messages.Count;
            }
        }

        /// <summary>
        /// Returns the message that a received segment belongs to, or null if it belongs to none.
        /// </summary>
        /// <remarks>
        /// Segment 0 starts a new message and replaces an unfinished one with the same sender, invoke ID and
        /// direction. A later segment belongs to the message that segment 0 started. Without one, for example a
        /// duplicate that arrives after the message was reassembled, it belongs to none.
        /// </remarks>
        public SegmentationInfo GetSegmentationInfo(BacnetAddress sender, byte invokeId, bool server, byte sequenceNumber)
        {
            var key = new SegmentationKey(sender.FullHashString(), invokeId, server);
            var now = Environment.TickCount64;

            lock (_lock)
            {
                if (sequenceNumber == 0)
                    _messages[key] = new SegmentationInfo { Key = key, Sender = sender };

                if (!_messages.TryGetValue(key, out var info))
                    return null;

                info.LastSegmentReceivedAt = now;
                return info;
            }
        }

        /// <summary>
        /// Removes a message once it has been reassembled
        /// </summary>
        public void Remove(SegmentationInfo info)
        {
            lock (_lock)
            {
                // Unless segment 0 of a new message with the same key has replaced it already
                if (_messages.TryGetValue(info.Key, out var current) && current == info)
                    _messages.Remove(info.Key);
            }
        }

        /// <summary>
        /// Removes the messages that haven't received a segment for <paramref name="timeoutMs"/> or longer, and
        /// returns them
        /// </summary>
        public IReadOnlyList<SegmentationInfo> RemoveExpired(int timeoutMs)
        {
            var now = Environment.TickCount64;
            List<SegmentationInfo> expired = null;

            lock (_lock)
            {
                foreach (var (key, info) in _messages)
                {
                    if (now - info.LastSegmentReceivedAt < timeoutMs)
                        continue;

                    // Removing while enumerating is allowed since .NET Core 3.0
                    _messages.Remove(key);
                    (expired ??= new List<SegmentationInfo>()).Add(info);
                }
            }

            return expired ?? (IReadOnlyList<SegmentationInfo>)Array.Empty<SegmentationInfo>();
        }
    }

    internal readonly record struct SegmentationKey(string Sender, byte InvokeId, bool Server);
}
