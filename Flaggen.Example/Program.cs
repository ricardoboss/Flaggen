using Flaggen;

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

[Flags]
public enum TestEnum
{
    Foo = 1 << 0,
    Bar = 1 << 1,
    Baz = 1 << 2,
    Qux = 1 << 3,
}
