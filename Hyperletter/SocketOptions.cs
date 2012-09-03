using System;
using Hyperletter.Core.Batch;

namespace Hyperletter.Core {
    public class SocketOptions {
        public BatchOptions BatchOptions { get; private set; }
        public Guid Id { get; set; }

        public SocketOptions() {
            BatchOptions = new BatchOptions { Enabled = true, Extend = TimeSpan.FromMilliseconds(100), MaxExtend = TimeSpan.FromSeconds(1), MaxLetters = 4000 };
            Id = Guid.NewGuid();
        }
    }
}