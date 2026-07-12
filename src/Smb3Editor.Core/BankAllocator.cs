namespace Smb3Editor.Core;

public sealed record BankRange(int Start, int Length)
{
    public int End => checked(Start + Length);
}

public sealed class BankAllocator
{
    private readonly int _start;
    private readonly int _end;
    private readonly List<BankRange> _used;

    public BankAllocator(int start, int length, IEnumerable<BankRange> used)
    {
        if (start < 0 || length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        _start = start;
        _end = checked(start + length);
        _used = used.OrderBy(static range => range.Start).ToList();
        if (_used.Any(range => range.Start < _start || range.End > _end) ||
            _used.Zip(_used.Skip(1), static (left, right) => left.End > right.Start).Any(static overlaps => overlaps))
        {
            throw new ArgumentException("Used ranges must be in-bank and non-overlapping.", nameof(used));
        }
    }

    public OperationResult<BankRange> Allocate(int length)
    {
        if (length <= 0)
        {
            return OperationResult<BankRange>.Failure(Diagnostics.Error("ALLOC_SIZE", "Allocation length must be positive."));
        }

        var cursor = _start;
        foreach (var range in _used)
        {
            if (range.Start - cursor >= length)
            {
                return Commit(cursor, length);
            }

            cursor = Math.Max(cursor, range.End);
        }

        return _end - cursor >= length
            ? Commit(cursor, length)
            : OperationResult<BankRange>.Failure(Diagnostics.Error("ALLOC_FULL", $"The bank has no contiguous {length}-byte region remaining."));
    }

    private OperationResult<BankRange> Commit(int start, int length)
    {
        var range = new BankRange(start, length);
        _used.Add(range);
        _used.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        return OperationResult<BankRange>.Success(range);
    }
}

