using Avalonia.Data;
using MaimaiTouchTool.Touch;
using Xunit;

namespace MaimaiTouchTool.Tests;

public sealed class SafeIntConverterTests
{
    private readonly SafeIntConverter converter = new();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("as20")]
    [InlineData("2147483648")]
    public void ConvertBack_InvalidTextDoesNothing(string text)
    {
        Assert.Same(BindingOperations.DoNothing, converter.ConvertBack(text, typeof(int), null, null!));
    }

    [Theory]
    [InlineData("20", 20)]
    [InlineData(" -20 ", -20)]
    [InlineData("+7", 7)]
    public void ConvertBack_ValidSignedIntegerReturnsValue(string text, int expected)
    {
        Assert.Equal(expected, converter.ConvertBack(text, typeof(int), null, null!));
    }
}
