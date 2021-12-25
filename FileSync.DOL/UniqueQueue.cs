namespace FileSync.DOL
{
    /// <summary>
    /// 不重複的隊列
    /// </summary>
    /// <typeparam name="T"></typeparam>
    public class UniqueQueue<T> : Queue<T>
    {
        private readonly HashSet<T> _hashSet;

        public UniqueQueue()
        {
            _hashSet = new HashSet<T>();
        }

        public UniqueQueue(IEqualityComparer<T> comparer)
        {
            _hashSet = new HashSet<T>(comparer);
        }

        public new void Clear()
        {
            this._hashSet.Clear();
            base.Clear();
        }

        public new void Enqueue(T item)
        {
            if (_hashSet.Add(item))
            {
                base.Enqueue(item);
            }
            else
            {
            }
        }

        public new T Dequeue()
        {
            T item = base.Dequeue();
            this._hashSet.Remove(item);
            return item;
        }
    }
}