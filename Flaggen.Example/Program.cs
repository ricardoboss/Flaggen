using TestNamespace;

var value = TestEnum.Foo | TestEnum.Bar;

value.Toggle(TestEnum.Foo);
value.Toggle(TestEnum.Qux);
value.Add(TestEnum.Baz);
value.Remove(TestEnum.Foo);

foreach (var flag in Enum.GetValues<TestEnum>())
{
    if (value.Has(flag))
        Console.WriteLine(flag);
}

var alphabet = TestClass<object>.NestedType<object>.NestedEnum.A;
alphabet.Add(TestClass<object>.NestedType<object>.NestedEnum.B);

[Flags]
public enum TestEnum
{
    Foo = 1 << 0,
    Bar = 1 << 1,
    Baz = 1 << 2,
    Qux = 1 << 3,
}

namespace TestNamespace
{
    public class TestClass<T> where T : class
    {
        public TestClass() {}

        public class NestedType<Q> where Q : T, new()
        {
            [Flags]
            public enum NestedEnum
            {
                A = 1 << 0,
                B = 1 << 1,
                C = 1 << 2,
            }
        }
    }
}
