using System;
using System.Collections.Generic;
using System.Numerics;

namespace MemSharp.Core
{
    // Struktur data untuk Geospatial
    public struct GeoPoint
    {
        public double Longitude { get; set; }
        public double Latitude { get; set; }
        public string Member { get; set; }
    }

    // Struktur data sederhana untuk TimeSeries
    public class TimeSeriesSample
    {
        public long Timestamp { get; set; }
        public double Value { get; set; }
    }

    // Enum untuk tipe data yang didukung
    public enum MemType
    {
        String,
        Hash,
        List,
        Set,
        SortedSet,
        Stream,
        Bitmap,
        Bitfield,
        HyperLogLog,
        Geo,
        TimeSeries,
        Json,
        Vector,
        None
    }

    // Kelas pembungkus nilai agar bisa menyimpan berbagai tipe dalam satu dictionary
    public class MemValue
    {
        public MemType Type { get; set; }
        public object Value { get; set; }
        public DateTime? Expiry { get; set; }

        public bool IsExpired()
        {
            if (Expiry == null) return false;
            return DateTime.UtcNow > Expiry;
        }
    }
}