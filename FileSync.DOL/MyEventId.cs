using Microsoft.Extensions.Logging;

namespace FileSync.DOL
{
    public static class MyEventId
    {
        public static readonly EventId START = new EventId(2, nameof(START));

        public static readonly EventId SUCCESS = new EventId(200, nameof(SUCCESS));

        public static readonly EventId WARNING = new EventId(444, nameof(WARNING));

        public static readonly EventId DELETE = new EventId(127, nameof(DELETE));

        public static readonly EventId EXCEPTION = new EventId(21, nameof(EXCEPTION));

        public static readonly EventId ERROR = new EventId(404, nameof(ERROR));

        public static readonly EventId STOP = new EventId(27, nameof(STOP));
    }
}