// netstandard2.0 兼容性填充
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    { }
}

namespace System
{
    internal readonly struct Index : IEquatable<Index>
    {
        private readonly int _value;

        public Index(int value, bool fromEnd = false)
        { _value = fromEnd ? ~value : value; }

        public int Value => _value < 0 ? ~_value : _value;
        public bool IsFromEnd => _value < 0;

        public int GetOffset(int length) => IsFromEnd ? length + _value : _value;

        public bool Equals(Index other) => _value == other._value;

        public override bool Equals(object obj) => obj is Index index && Equals(index);

        public override int GetHashCode() => _value;

        public static implicit operator Index(int value) => new(value);
    }
}
