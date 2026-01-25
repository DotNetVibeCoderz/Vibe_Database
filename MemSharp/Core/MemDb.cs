using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MemSharp.Core
{
    public class MemDb
    {
        // Penyimpanan utama: Key -> MemValue (yang membungkus tipe aslinya)
        private readonly ConcurrentDictionary<string, MemValue> _store = new ConcurrentDictionary<string, MemValue>();

        // Untuk Pub/Sub: Channel -> List of Subscribers (bisa berupa Action/Callback id)
        private readonly ConcurrentDictionary<string, List<Action<string>>> _pubSubChannels = new ConcurrentDictionary<string, List<Action<string>>>();
        
        // --- Operasi Dasar (Key-Value / String) ---

        public bool Set(string key, string value, TimeSpan? expiry = null)
        {
            var item = new MemValue 
            { 
                Type = MemType.String, 
                Value = value,
                Expiry = expiry.HasValue ? DateTime.UtcNow.Add(expiry.Value) : (DateTime?)null
            };
            _store[key] = item;
            return true;
        }

        public string Get(string key)
        {
            if (_store.TryGetValue(key, out var item) && !item.IsExpired())
            {
                if (item.Type != MemType.String) throw new InvalidOperationException($"WRONGTYPE Key is {item.Type}, not String");
                return (string)item.Value;
            }
            if (item != null && item.IsExpired()) _store.TryRemove(key, out _); // Lazy expiration
            return null;
        }

        // --- Operasi List ---
        public void LPush(string key, string value)
        {
            _store.AddOrUpdate(key, 
                k => new MemValue { Type = MemType.List, Value = new List<string> { value } },
                (k, v) => {
                    if (v.Type != MemType.List) throw new InvalidOperationException("WRONGTYPE");
                    lock(v.Value) { ((List<string>)v.Value).Insert(0, value); }
                    return v;
                });
        }

        public List<string> LRange(string key, int start, int stop)
        {
            if (_store.TryGetValue(key, out var item) && !item.IsExpired())
            {
                if (item.Type != MemType.List) return null;
                var list = (List<string>)item.Value;
                
                // Logic sederhana untuk range handling
                if (start < 0) start = 0;
                if (stop >= list.Count || stop == -1) stop = list.Count - 1;
                
                if(start > stop) return new List<string>();
                
                lock(list) { return list.GetRange(start, stop - start + 1); }
            }
            return new List<string>();
        }

        // --- Operasi Hash ---
        public void HSet(string key, string field, string value)
        {
             _store.AddOrUpdate(key,
                k => new MemValue { Type = MemType.Hash, Value = new Dictionary<string, string> { { field, value } } },
                (k, v) => {
                    if (v.Type != MemType.Hash) throw new InvalidOperationException("WRONGTYPE");
                    lock(v.Value) { ((Dictionary<string, string>)v.Value)[field] = value; }
                    return v;
                });
        }

        public string HGet(string key, string field)
        {
             if (_store.TryGetValue(key, out var item) && !item.IsExpired() && item.Type == MemType.Hash)
             {
                 var dict = (Dictionary<string, string>)item.Value;
                 lock(dict) { return dict.ContainsKey(field) ? dict[field] : null; }
             }
             return null;
        }

        // --- Operasi Set ---
        public void SAdd(string key, string member)
        {
             _store.AddOrUpdate(key,
                k => new MemValue { Type = MemType.Set, Value = new HashSet<string> { member } },
                (k, v) => {
                    if (v.Type != MemType.Set) throw new InvalidOperationException("WRONGTYPE");
                    lock(v.Value) { ((HashSet<string>)v.Value).Add(member); }
                    return v;
                });
        }

        public HashSet<string> SMembers(string key)
        {
             if (_store.TryGetValue(key, out var item) && !item.IsExpired() && item.Type == MemType.Set)
             {
                 return (HashSet<string>)item.Value;
             }
             return new HashSet<string>();
        }

        // --- Pub/Sub ---
        public void Subscribe(string channel, Action<string> callback)
        {
            _pubSubChannels.AddOrUpdate(channel, 
                new List<Action<string>> { callback },
                (ch, list) => { lock(list) { list.Add(callback); } return list; });
        }

        public void Publish(string channel, string message)
        {
            if (_pubSubChannels.TryGetValue(channel, out var subscribers))
            {
                lock(subscribers)
                {
                    foreach (var sub in subscribers)
                    {
                        // Fire and forget agar tidak blocking
                        System.Threading.Tasks.Task.Run(() => sub(message));
                    }
                }
            }
        }

        // --- SQL-Like Query Layer ---
        // Contoh Query: "SELECT * FROM KEYS WHERE KEY LIKE 'user%'"
        // Contoh Query: "SELECT * FROM HASH WHERE KEY = 'user:1'"
        public List<string> ExecuteSql(string query)
        {
            var result = new List<string>();
            query = query.Trim();
            
            // Parser sangat sederhana menggunakan Regex
            var match = Regex.Match(query, @"SELECT\s+\*\s+FROM\s+(KEYS|HASH|LIST)\s+WHERE\s+KEY\s+(LIKE|=)\s+'(.*)'", RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                result.Add("ERROR: Syntax error or unsupported query.");
                return result;
            }

            string table = match.Groups[1].Value.ToUpper();
            string op = match.Groups[2].Value.ToUpper();
            string criteria = match.Groups[3].Value; // value inside quotes

            IEnumerable<string> keys = _store.Keys;

            if (op == "=")
            {
                keys = keys.Where(k => k.Equals(criteria));
            }
            else if (op == "LIKE")
            {
                // Simple wildcard conversion
                string pattern = "^" + Regex.Escape(criteria).Replace("%", ".*") + "$";
                keys = keys.Where(k => Regex.IsMatch(k, pattern));
            }

            foreach (var key in keys)
            {
                if (_store.TryGetValue(key, out var item) && !item.IsExpired())
                {
                    // Filter by "Table" aka Type
                    if (table == "KEYS" || 
                       (table == "HASH" && item.Type == MemType.Hash) ||
                       (table == "LIST" && item.Type == MemType.List))
                    {
                        if (item.Type == MemType.String) result.Add($"{key} : {item.Value}");
                        else if (item.Type == MemType.List) result.Add($"{key} : List[{((List<string>)item.Value).Count}]");
                        else if (item.Type == MemType.Hash) result.Add($"{key} : Hash[{((Dictionary<string,string>)item.Value).Count}]");
                        else result.Add($"{key} : {item.Type}");
                    }
                }
            }

            return result;
        }

        // --- Linq Support ---
        // Mengembalikan IEnumerable agar bisa di-query pakai Linq dari luar
        public IEnumerable<KeyValuePair<string, MemValue>> AsEnumerable()
        {
            return _store.Where(x => !x.Value.IsExpired());
        }
    }
}