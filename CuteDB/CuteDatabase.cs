using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Dynamic.Core; 
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace CuteDB
{
    public class CuteDatabase
    {
        // Internal storage: TableName -> List of Objects
        private readonly ConcurrentDictionary<string, List<object>> _storage = new ConcurrentDictionary<string, List<object>>();
        private readonly string _jdbPath;

        public CuteDatabase(string path = "database.jdb")
        {
            _jdbPath = path;
            if (File.Exists(_jdbPath))
            {
                Load();
            }
        }

        // CREATE
        public void Insert<T>(string tableName, T data)
        {
            if (!_storage.ContainsKey(tableName))
            {
                _storage[tableName] = new List<object>();
            }
            _storage[tableName].Add(data);
        }

        // READ
        public List<T> GetCollection<T>(string tableName)
        {
             if (!_storage.ContainsKey(tableName))
            {
                _storage[tableName] = new List<object>();
            }
            // Warning: Returns a new list containing references. 
            // - Updates to object properties WILL be reflected in DB.
            // - Removing items from this returned list WILL NOT remove them from DB. Use Delete() for that.
            return _storage[tableName].OfType<T>().ToList();
        }

        // UPDATE (Helper)
        public int Update<T>(string tableName, Func<T, bool> predicate, Action<T> updateAction)
        {
            if (!_storage.ContainsKey(tableName)) return 0;

            var list = _storage[tableName];
            // Filter
            var targets = list.OfType<T>().Where(predicate).ToList();

            foreach(var item in targets)
            {
                updateAction(item);
            }
            return targets.Count;
        }

        // DELETE (Helper)
        public int Delete<T>(string tableName, Func<T, bool> predicate)
        {
            if (!_storage.ContainsKey(tableName)) return 0;

            var list = _storage[tableName];
            var targets = list.OfType<T>().Where(predicate).ToList();

            foreach (var item in targets)
            {
                list.Remove(item);
            }
            return targets.Count;
        }

        public void Save()
        {
            var settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.All,
                Formatting = Formatting.Indented
            };
            var json = JsonConvert.SerializeObject(_storage, settings);
            File.WriteAllText(_jdbPath, json);
        }

        public void Load()
        {
            if (!File.Exists(_jdbPath)) return;

            var json = File.ReadAllText(_jdbPath);
            var settings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.All
            };
            var loaded = JsonConvert.DeserializeObject<ConcurrentDictionary<string, List<object>>>(json, settings);
            
            if (loaded != null)
            {
                _storage.Clear();
                foreach (var kvp in loaded)
                {
                    _storage[kvp.Key] = kvp.Value;
                }
            }
        }

        public IEnumerable<dynamic> ExecuteSql(string sql)
        {
            var selectPattern = @"SELECT\s+\*\s+FROM\s+(?<table>\w+)(\s+WHERE\s+(?<condition>.+))?";
            var matchSelect = Regex.Match(sql, selectPattern, RegexOptions.IgnoreCase);

            if (matchSelect.Success)
            {
                string tableName = matchSelect.Groups["table"].Value;
                string condition = matchSelect.Groups["condition"].Value;

                if (!_storage.ContainsKey(tableName)) throw new Exception($"Table '{tableName}' not found.");

                var list = _storage[tableName];
                if (list.Count == 0) return new List<dynamic>();

                Type itemType = list[0].GetType();
                var queryable = list.AsQueryable();
                
                var castMethod = typeof(Queryable).GetMethod("Cast", BindingFlags.Public | BindingFlags.Static).MakeGenericMethod(itemType);
                var typedQueryable = (IQueryable)castMethod.Invoke(null, new object[] { queryable });

                if (!string.IsNullOrWhiteSpace(condition))
                {
                    typedQueryable = typedQueryable.Where(condition);
                }

                return typedQueryable.Cast<dynamic>().ToList();
            }
            
            var deletePattern = @"DELETE\s+FROM\s+(?<table>\w+)(\s+WHERE\s+(?<condition>.+))?";
            var matchDelete = Regex.Match(sql, deletePattern, RegexOptions.IgnoreCase);
            if (matchDelete.Success)
            {
                 string tableName = matchDelete.Groups["table"].Value;
                 string condition = matchDelete.Groups["condition"].Value;

                  if (!_storage.ContainsKey(tableName)) throw new Exception($"Table '{tableName}' not found.");
                  
                  var list = _storage[tableName];
                  if (list.Count == 0) return new List<dynamic>();
                  
                  if (string.IsNullOrWhiteSpace(condition))
                  {
                      int c = list.Count;
                      list.Clear();
                      return new List<dynamic> { new { Result = "Deleted " + c + " rows" } };
                  }
                  
                   Type itemType = list[0].GetType();
                   var queryable = list.AsQueryable();
                   var castMethod = typeof(Queryable).GetMethod("Cast", BindingFlags.Public | BindingFlags.Static).MakeGenericMethod(itemType);
                   var typedQueryable = (IQueryable)castMethod.Invoke(null, new object[] { queryable });
                   
                   var toDelete = typedQueryable.Where(condition).Cast<object>().ToList();
                   foreach(var item in toDelete)
                   {
                       list.Remove(item);
                   }
                   return new List<dynamic> { new { Result = "Deleted " + toDelete.Count + " rows" } };
            }

            throw new Exception("SQL Command not recognized.");
        }
    }
}
