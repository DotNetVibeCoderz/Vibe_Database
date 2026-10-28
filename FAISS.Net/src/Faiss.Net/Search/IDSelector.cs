namespace Faiss.Net;

/// <summary>
/// Restricts a search to a subset of ids, the <c>faiss.IDSelector</c> family.
/// <para>
/// A selector is applied <em>inside</em> the scan, so a search for <c>k</c> results returns the
/// <c>k</c> best members of the subset rather than the best <c>k</c> overall with non-members
/// filtered out afterwards. The second approach is the usual workaround and it silently
/// under-delivers: ask for 10, filter to one tenant, and 2 may be left.
/// </para>
/// <para>
/// Ids are the ids the index itself reports: positions for <see cref="IndexFlat"/>, caller ids for
/// <see cref="IndexIDMap"/> and the IVF family. Selectors are read concurrently by search threads
/// and must not be mutated while a search is running.
/// </para>
/// </summary>
/// <example>
/// <code>
/// var result = index.Search(query, k: 10, selector: IDSelector.Range(1000, 2000));
/// var visible = index.Search(query, k: 10, selector: IDSelector.Predicate(id => IsVisible(id)));
/// </code>
/// </example>
public abstract class IDSelector
{
    /// <summary>True when <paramref name="id"/> belongs to the subset. Named <c>is_member</c> in FAISS.</summary>
    public abstract bool IsMember(long id);

    /// <summary>
    /// Conservative range test: returns false only when no id in <c>[min, max]</c> can be a member.
    /// Indexes that know the id range of a block (an IVF list, a shard) use it to skip the block
    /// without scanning it. The default, true, is always correct.
    /// </summary>
    public virtual bool MayIntersect(long min, long max) => true;

    // ------------------------------------------------------------- Factories

    /// <summary>Ids in <c>[min, max)</c>, the <c>IDSelectorRange</c> of FAISS.</summary>
    public static IDSelectorRange Range(long min, long max) => new(min, max);

    /// <summary>An explicit set of ids, the <c>IDSelectorBatch</c> of FAISS.</summary>
    public static IDSelectorBatch Batch(IEnumerable<long> ids) => new(ids);

    /// <summary>Ids whose bit is set in a little-endian bitmap, the <c>IDSelectorBitmap</c> of FAISS.</summary>
    public static IDSelectorBitmap Bitmap(byte[] bitmap) => new(bitmap);

    /// <summary>An arbitrary predicate. Costs one delegate call per candidate.</summary>
    public static IDSelectorPredicate Predicate(Func<long, bool> predicate) => new(predicate);

    /// <summary>Every id; useful as a neutral element when composing.</summary>
    public static IDSelectorAll All() => new();

    /// <summary>Ids not selected by <paramref name="selector"/>.</summary>
    public static IDSelectorNot Not(IDSelector selector) => new(selector);

    /// <summary>Ids selected by both.</summary>
    public static IDSelectorAnd And(IDSelector left, IDSelector right) => new(left, right);

    /// <summary>Ids selected by either.</summary>
    public static IDSelectorOr Or(IDSelector left, IDSelector right) => new(left, right);

    /// <summary>Ids selected by exactly one.</summary>
    public static IDSelectorXOr XOr(IDSelector left, IDSelector right) => new(left, right);
}

/// <summary>Ids in <c>[Imin, Imax)</c>. The cheapest selector, and the one IVF can prune whole lists with.</summary>
public sealed class IDSelectorRange : IDSelector
{
    /// <summary>Inclusive lower bound.</summary>
    public long Imin { get; }

    /// <summary>Exclusive upper bound.</summary>
    public long Imax { get; }

    public IDSelectorRange(long imin, long imax)
    {
        if (imax < imin) throw new ArgumentException($"Empty range: imax {imax} is below imin {imin}.");
        Imin = imin;
        Imax = imax;
    }

    public override bool IsMember(long id) => id >= Imin && id < Imax;

    public override bool MayIntersect(long min, long max) => max >= Imin && min < Imax;
}

/// <summary>An explicit set of ids, backed by a hash set.</summary>
public sealed class IDSelectorBatch : IDSelector
{
    private readonly HashSet<long> _ids;
    private readonly long _min = long.MaxValue, _max = long.MinValue;

    public IDSelectorBatch(IEnumerable<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        _ids = [.. ids];
        foreach (long id in _ids)
        {
            if (id < _min) _min = id;
            if (id > _max) _max = id;
        }
    }

    /// <summary>Number of distinct ids selected.</summary>
    public int Count => _ids.Count;

    public override bool IsMember(long id) => _ids.Contains(id);

    public override bool MayIntersect(long min, long max) => _ids.Count > 0 && max >= _min && min <= _max;
}

/// <summary>
/// Ids whose bit is set: bit <c>id &amp; 7</c> of byte <c>id &gt;&gt; 3</c>. Ids beyond the bitmap are
/// not members. One shift and one mask per candidate, so the filter is close to free.
/// </summary>
public sealed class IDSelectorBitmap : IDSelector
{
    private readonly byte[] _bitmap;

    public IDSelectorBitmap(byte[] bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        _bitmap = bitmap;
    }

    /// <summary>Builds a bitmap selecting the given ids.</summary>
    public static IDSelectorBitmap FromIds(IEnumerable<long> ids, long capacity)
    {
        var bitmap = new byte[(capacity + 7) / 8];
        foreach (long id in ids)
            if (id >= 0 && id < capacity) bitmap[id >> 3] |= (byte)(1 << (int)(id & 7));
        return new IDSelectorBitmap(bitmap);
    }

    /// <summary>Ids the bitmap can describe.</summary>
    public long Capacity => (long)_bitmap.Length * 8;

    public override bool IsMember(long id) =>
        (ulong)id < (ulong)Capacity && (_bitmap[id >> 3] & (1 << (int)(id & 7))) != 0;

    public override bool MayIntersect(long min, long max) => max >= 0 && min < Capacity;
}

/// <summary>An arbitrary predicate over ids.</summary>
public sealed class IDSelectorPredicate : IDSelector
{
    private readonly Func<long, bool> _predicate;

    public IDSelectorPredicate(Func<long, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        _predicate = predicate;
    }

    public override bool IsMember(long id) => _predicate(id);
}

/// <summary>Selects every id.</summary>
public sealed class IDSelectorAll : IDSelector
{
    public override bool IsMember(long id) => true;
}

/// <summary>Complement of another selector.</summary>
public sealed class IDSelectorNot : IDSelector
{
    private readonly IDSelector _inner;

    public IDSelectorNot(IDSelector inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    public override bool IsMember(long id) => !_inner.IsMember(id);
}

/// <summary>Intersection of two selectors.</summary>
public sealed class IDSelectorAnd : IDSelector
{
    private readonly IDSelector _left, _right;

    public IDSelectorAnd(IDSelector left, IDSelector right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        (_left, _right) = (left, right);
    }

    public override bool IsMember(long id) => _left.IsMember(id) && _right.IsMember(id);

    public override bool MayIntersect(long min, long max) =>
        _left.MayIntersect(min, max) && _right.MayIntersect(min, max);
}

/// <summary>Union of two selectors.</summary>
public sealed class IDSelectorOr : IDSelector
{
    private readonly IDSelector _left, _right;

    public IDSelectorOr(IDSelector left, IDSelector right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        (_left, _right) = (left, right);
    }

    public override bool IsMember(long id) => _left.IsMember(id) || _right.IsMember(id);

    public override bool MayIntersect(long min, long max) =>
        _left.MayIntersect(min, max) || _right.MayIntersect(min, max);
}

/// <summary>Symmetric difference of two selectors.</summary>
public sealed class IDSelectorXOr : IDSelector
{
    private readonly IDSelector _left, _right;

    public IDSelectorXOr(IDSelector left, IDSelector right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        (_left, _right) = (left, right);
    }

    public override bool IsMember(long id) => _left.IsMember(id) ^ _right.IsMember(id);
}

/// <summary>
/// Re-expresses a selector over a different id space: <c>IsMember(local)</c> asks the inner
/// selector about <c>map(local)</c>. Composite indexes use it to hand their children a selector in
/// the children's own ids — positions inside an <see cref="IndexIDMap"/>, local ids inside a shard.
/// </summary>
internal sealed class IDSelectorTranslated : IDSelector
{
    private readonly IDSelector _inner;
    private readonly Func<long, long> _map;
    private readonly long _offset;
    private readonly bool _isOffset;

    private IDSelectorTranslated(IDSelector inner, Func<long, long>? map, long offset)
    {
        _inner = inner;
        _map = map ?? (static id => id);
        _offset = offset;
        _isOffset = map is null;
    }

    /// <summary>Translation by a fixed offset; range pruning survives the translation.</summary>
    public static IDSelectorTranslated Offset(IDSelector inner, long offset) => new(inner, null, offset);

    /// <summary>Translation through an arbitrary map; a local id the map rejects (returns &lt; 0) is not a member.</summary>
    public static IDSelectorTranslated Map(IDSelector inner, Func<long, long> map) => new(inner, map, 0);

    public override bool IsMember(long id)
    {
        if (_isOffset) return _inner.IsMember(id + _offset);
        long mapped = _map(id);
        return mapped >= 0 && _inner.IsMember(mapped);
    }

    public override bool MayIntersect(long min, long max) =>
        !_isOffset || _inner.MayIntersect(min + _offset, max + _offset);
}
