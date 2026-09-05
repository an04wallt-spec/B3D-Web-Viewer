using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace BazisOpenGlCapture.Common
{
    public sealed class CaptureInterface : MarshalByRefObject
    {
        private static readonly ConcurrentQueue<string> Messages = new ConcurrentQueue<string>();
        private static volatile bool _alive = true;

        public bool Ping(int pid) => _alive;

        public void Report(string message)
        {
            if (!string.IsNullOrWhiteSpace(message)) Messages.Enqueue(message);
        }

        public string[] Drain()
        {
            var list = new List<string>();
            while (Messages.TryDequeue(out var item)) list.Add(item);
            return list.ToArray();
        }

        public void Stop() => _alive = false;

        public override object InitializeLifetimeService() => null;
    }
}
