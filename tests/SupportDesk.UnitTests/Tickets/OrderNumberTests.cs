using SupportDesk.Core.Tickets;

namespace SupportDesk.UnitTests.Tickets;

public class OrderNumberTests
{
    [Theory]
    [InlineData("KO-000000")]
    [InlineData("KO-205002")]
    [InlineData("KO-999999")]
    public void Order_number_with_KO_and_six_digits_is_valid(string value) => Assert.True(OrderNumber.IsValid(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("KO-12345")]
    [InlineData("KO-1234567")]
    [InlineData("ko-123456")]
    [InlineData("KO123456")]
    [InlineData("KO-12345a")]
    [InlineData(" KO-123456")]
    [InlineData("KO-123456 ")]
    [InlineData("KO-123456\n")]
    [InlineData("XKO-123456")]
    [InlineData("KO-١٢٣٤٥٦")] // Arabic-Indic digits, which \d would accept
    public void Order_number_with_any_other_shape_is_invalid(string? value) => Assert.False(OrderNumber.IsValid(value));
}
