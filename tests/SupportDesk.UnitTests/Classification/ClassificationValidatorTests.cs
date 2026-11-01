using System.Text.Json;
using Microsoft.Extensions.AI;
using SupportDesk.Core.Classification;
using SupportDesk.Core.Tickets;

namespace SupportDesk.UnitTests.Classification;

public class ClassificationValidatorTests
{
    private const string Valid = """{"category":"Shipping","priority":"High","reason":"The parcel is late and the customer needs it."}""";

    [Fact]
    public void Valid_output_is_accepted_and_read()
    {
        var validation = ClassificationValidator.Validate("\n  " + Valid + "\n");

        Assert.True(validation.IsValid);
        Assert.Empty(validation.Errors);
        Assert.Equal(new ClassificationResult(Category.Shipping, Priority.High, "The parcel is late and the customer needs it."), validation.Result);
    }

    [Fact]
    public void A_reason_of_exactly_200_characters_is_accepted()
    {
        var reason = new string('a', 200);

        var validation = ClassificationValidator.Validate($$"""{"category":"Other","priority":"Low","reason":"{{reason}}"}""");

        Assert.Equal(reason, validation.Result?.Reason);
    }

    [Theory]
    [InlineData("Sure! Here is the classification: {\"category\": \"Account\", \"priority\": ", "isn't valid JSON")]
    [InlineData("```json\n{\"category\":\"Other\",\"priority\":\"Low\",\"reason\":\"A question.\"}\n```", "isn't valid JSON")]
    [InlineData("{\"category\":\"Other\",\"priority\":\"Low\",\"reason\":\"A question.\",}", "isn't valid JSON")]
    [InlineData("", "was empty")]
    [InlineData("   ", "was empty")]
    [InlineData("[]", "is a JSON array; expected a JSON object")]
    [InlineData("\"Billing\"", "is a JSON string; expected a JSON object")]
    public void Output_that_is_not_a_json_object_is_rejected(string text, string expected)
    {
        AssertRejected(text, expected);
    }

    [Theory]
    [InlineData("""{"category":"Privacy","priority":"Normal","reason":"r"}""", "category must be one of: Billing, Shipping, Returns, Account, Technical, Other; got \"Privacy\"")]
    [InlineData("""{"category":"billing","priority":"Normal","reason":"r"}""", "category must be one of")]
    [InlineData("""{"category":"Billing, Shipping","priority":"Normal","reason":"r"}""", "category must be one of")]
    [InlineData("""{"category":0,"priority":"Normal","reason":"r"}""", "as a string; got a JSON number")]
    [InlineData("""{"category":"Billing","priority":"Critical","reason":"r"}""", "priority must be one of: Low, Normal, High, Urgent; got \"Critical\"")]
    [InlineData("""{"category":"Billing","priority":"2","reason":"r"}""", "priority must be one of")]
    [InlineData("""{"category":"Billing","priority":null,"reason":"r"}""", "got a JSON null")]
    public void An_unknown_enum_value_is_rejected(string text, string expected)
    {
        AssertRejected(text, expected);
    }

    [Theory]
    [InlineData("""{"priority":"Normal","reason":"r"}""", "Missing property \"category\"")]
    [InlineData("""{"category":"Billing","reason":"r"}""", "Missing property \"priority\"")]
    [InlineData("""{"category":"Billing","priority":"Normal"}""", "Missing property \"reason\"")]
    public void A_missing_field_is_rejected(string text, string expected)
    {
        AssertRejected(text, expected);
    }

    [Theory]
    [InlineData("""{"category":"Billing","priority":"Normal","reason":"r","confidence":0.9}""", "Unexpected property \"confidence\"")]
    [InlineData("""{"category":"Billing","priority":"Normal","reason":"r","Category":"Other"}""", "Unexpected property \"Category\"")]
    public void An_extra_field_is_rejected(string text, string expected)
    {
        AssertRejected(text, expected);
    }

    [Fact]
    public void A_repeated_field_is_rejected_rather_than_one_copy_winning()
    {
        AssertRejected("""{"category":"Billing","category":"Other","priority":"Normal","reason":"r"}""", "isn't valid JSON");
    }

    [Theory]
    [InlineData(201, "reason is 201 characters long; it must be at most 200")]
    [InlineData(1000, "reason is 1000 characters long")]
    public void A_too_long_reason_is_rejected(int length, string expected)
    {
        AssertRejected($$"""{"category":"Billing","priority":"Normal","reason":"{{new string('a', length)}}"}""", expected);
    }

    [Theory]
    [InlineData("""{"category":"Billing","priority":"Normal","reason":"  "}""", "reason is empty")]
    [InlineData("""{"category":"Billing","priority":"Normal","reason":42}""", "reason must be a string; got a JSON number")]
    public void An_empty_or_non_string_reason_is_rejected(string text, string expected)
    {
        AssertRejected(text, expected);
    }

    [Fact]
    public void Every_problem_is_reported_so_the_retry_can_fix_them_all()
    {
        var validation = ClassificationValidator.Validate("""{"category":"Privacy","priority":"Normal","extra":1}""");

        Assert.Equal(3, validation.Errors.Count);
        Assert.Contains("Unexpected property \"extra\"", validation.ErrorText, StringComparison.Ordinal);
        Assert.Contains("got \"Privacy\"", validation.ErrorText, StringComparison.Ordinal);
        Assert.Contains("Missing property \"reason\"", validation.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_wrong_value_is_shortened_in_the_error()
    {
        var validation = ClassificationValidator.Validate($$"""{"category":"{{new string('x', 500)}}","priority":"Normal","reason":"r"}""");

        Assert.Contains(new string('x', 40) + "…\"", validation.ErrorText, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 41), validation.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public void The_schema_sent_with_the_request_matches_what_the_validator_checks()
    {
        var format = ChatResponseFormat.ForJsonSchema<ClassificationResult>(ClassificationJson.Options);

        var schema = format.Schema!.Value;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["category", "priority", "reason"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Order(StringComparer.Ordinal));
        var properties = schema.GetProperty("properties");
        Assert.Equal(["category", "priority", "reason"], properties.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(Enum.GetNames<Category>(), Strings(properties.GetProperty("category").GetProperty("enum")));
        Assert.Equal(Enum.GetNames<Priority>(), Strings(properties.GetProperty("priority").GetProperty("enum")));
        Assert.Equal(ClassificationResult.ReasonMaxLength, properties.GetProperty("reason").GetProperty("maxLength").GetInt32());
    }

    private static string?[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString())];

    private static void AssertRejected(string text, string expected)
    {
        var validation = ClassificationValidator.Validate(text);

        Assert.False(validation.IsValid);
        Assert.Null(validation.Result);
        Assert.Contains(expected, validation.ErrorText, StringComparison.Ordinal);
    }
}
