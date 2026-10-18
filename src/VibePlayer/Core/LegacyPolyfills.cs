#if NETFRAMEWORK
// Language-feature support types that .NET Framework 4.8 doesn't ship (records, init, ^ indexes).
// They are only compiled into the Windows 7/8.1 edition.

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}

namespace System
{
    internal readonly struct Index
    {
        private readonly int _value;

        public Index(int value, bool fromEnd = false) => _value = fromEnd ? ~value : value;

        public static Index Start => new(0);
        public static Index End => new(0, true);
        public bool IsFromEnd => _value < 0;
        public int Value => _value < 0 ? ~_value : _value;

        public int GetOffset(int length) => IsFromEnd ? length - Value : Value;

        public static implicit operator Index(int value) => new(value);
    }

    internal readonly struct Range
    {
        public Range(Index start, Index end)
        {
            Start = start;
            End = end;
        }

        public Index Start { get; }
        public Index End { get; }

        public static Range StartAt(Index start) => new(start, Index.End);
        public static Range EndAt(Index end) => new(Index.Start, end);
        public static Range All => new(Index.Start, Index.End);

        public (int Offset, int Length) GetOffsetAndLength(int length)
        {
            var start = Start.GetOffset(length);
            var end = End.GetOffset(length);
            return (start, end - start);
        }
    }
}
#endif
