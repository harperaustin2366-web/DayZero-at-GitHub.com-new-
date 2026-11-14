// Insertion-ordered dictionary with Godot Dictionary semantics:
//  * enumeration follows insertion order,
//  * assigning to an existing key keeps its position,
//  * Remove + re-insert moves the key to the end.
// C# Dictionary<K,V> only keeps insertion order while nothing is removed; game state removes keys
// constantly (fences, traps, conversions...) and several systems iterate them while consuming RNG,
// so order matters for matching the Godot behaviour.
// Implements IDictionary<K,V> so Newtonsoft serializes/deserializes it like a normal dictionary.
using System;
using System.Collections;
using System.Collections.Generic;

namespace DayZero.Core
{
    public sealed class ODict<TKey, TValue> : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>
    {
        readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _map;
        readonly LinkedList<KeyValuePair<TKey, TValue>> _list = new LinkedList<KeyValuePair<TKey, TValue>>();

        public ODict() { _map = new Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>>(); }
        public ODict(IEnumerable<KeyValuePair<TKey, TValue>> src) : this() { foreach (var kv in src) this[kv.Key] = kv.Value; }

        public TValue this[TKey key]
        {
            get => _map[key].Value.Value;
            set
            {
                if (_map.TryGetValue(key, out var n)) n.Value = new KeyValuePair<TKey, TValue>(key, value);
                else _map[key] = _list.AddLast(new KeyValuePair<TKey, TValue>(key, value));
            }
        }

        public int Count => _map.Count;
        public bool IsReadOnly => false;
        public bool IsEmpty => _map.Count == 0;

        /// <summary>Keys in insertion order (a snapshot, safe to iterate while mutating).</summary>
        public List<TKey> KeyList()
        {
            var o = new List<TKey>(_map.Count);
            foreach (var kv in _list) o.Add(kv.Key);
            return o;
        }
        public ICollection<TKey> Keys => KeyList();
        IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => KeyList();
        public ICollection<TValue> Values { get { var o = new List<TValue>(_map.Count); foreach (var kv in _list) o.Add(kv.Value); return o; } }
        IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => Values;

        public void Add(TKey key, TValue value)
        {
            if (_map.ContainsKey(key)) throw new ArgumentException("duplicate key " + key);
            this[key] = value;
        }
        public void Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);
        public bool ContainsKey(TKey key) => _map.ContainsKey(key);
        public bool Has(TKey key) => _map.ContainsKey(key);
        public bool Contains(KeyValuePair<TKey, TValue> item) => _map.TryGetValue(item.Key, out var n) && EqualityComparer<TValue>.Default.Equals(n.Value.Value, item.Value);
        public bool Remove(TKey key)
        {
            if (!_map.TryGetValue(key, out var n)) return false;
            _list.Remove(n);
            _map.Remove(key);
            return true;
        }
        public bool Remove(KeyValuePair<TKey, TValue> item) => Contains(item) && Remove(item.Key);
        public bool TryGetValue(TKey key, out TValue value)
        {
            if (_map.TryGetValue(key, out var n)) { value = n.Value.Value; return true; }
            value = default;
            return false;
        }
        /// <summary>GDScript dict.get(key, default).</summary>
        public TValue Get(TKey key, TValue dflt = default) => _map.TryGetValue(key, out var n) ? n.Value.Value : dflt;
        public void Clear() { _map.Clear(); _list.Clear(); }
        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) { foreach (var kv in _list) array[arrayIndex++] = kv; }
        public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _list.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public ODict<TKey, TValue> Duplicate() => new ODict<TKey, TValue>(this);
    }

    public static class ODictExt
    {
        /// <summary>d[k] = d.get(k, 0) + v</summary>
        public static void AddTo(this ODict<string, double> d, string k, double v) => d[k] = d.Get(k, 0.0) + v;
        public static void AddTo(this ODict<string, int> d, string k, int v) => d[k] = d.Get(k, 0) + v;
        public static void AddTo(this Dictionary<string, double> d, string k, double v) => d[k] = (d.TryGetValue(k, out var o) ? o : 0.0) + v;
        public static TV GetOr<TK, TV>(this Dictionary<TK, TV> d, TK k, TV dflt) => d.TryGetValue(k, out var v) ? v : dflt;
    }
}
